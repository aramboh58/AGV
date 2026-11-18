using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AGV.Core.Entities;
using AGV.Core.Enums;
using AGV.Core.Interfaces;
using AGV.Core.Logging;
using AGV.Core.Messages;
using AGV.Fleet.Infrastructure;
using AGV.Topology.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AGV.Fleet.Services
{
    /// <summary>
    /// Implements IFleetManager — central coordinator of the host system.
    ///
    /// Owns:
    ///   — Vehicle registry (sole writer of vehicle state)
    ///   — Mission queue (sole writer of queue state)
    ///   — Dispatch decisions (publishes to Channel)
    ///
    /// Consumes via Channel<T>:
    ///   — VehicleStateUpdate (from MQTT listener / simulator)
    ///   — MissionTransfer (from traffic manager on fault)
    ///   — MissionSwap (from swap candidate evaluator)
    ///
    /// Does NOT perform routing — delegates to IRoutingEngine.
    /// Does NOT manage charge slots — delegates to IChargeQueueManager.
    /// Does NOT manage resource locks — delegates to ITrafficManager.
    /// </summary>
    public sealed class FleetManagerService
        : BackgroundService, IFleetManager
    {
        private readonly VehicleRegistry _registry;
        private readonly ActiveMissionRegistry _activeMissions;
        private readonly RouteWindowManager _windows;
        private readonly MissionQueueService _missionQueue;
        private readonly ChannelRegistry _channels;
        private readonly IRoutingEngine _routing;
        private readonly ITrafficManager _traffic;
        private readonly IChargeQueueManager _charging;
        private readonly ICustomizationApi _customization;
        private readonly IVehicleAdapter _adapter;
        private readonly RoadMapGraphHolder _graphHolder;
        private readonly ILogger _logger;
        private readonly ILogger _dispatchLogger;

        // Signals the dispatch loop that new missions are available
        private readonly SemaphoreSlim _dispatchSignal = new(0, int.MaxValue);

        private int _nextMissionId = 0;

        // Completed mission counter for metrics
        private int _completedMissions;
        private int _transferredMissions;
        private int _swappedMissions;

        private int _enqueuedMissions;
        private int _dispatchedMissions;

        // Vehicle -> mission whose drop leg has already been sent, and the
        // earliest time to retry routing a drop leg that found no route.
        private readonly ConcurrentDictionary<int, int> _dropLegSent = new();
        private readonly ConcurrentDictionary<int, DateTime> _dropLegRetryAt = new();

        // Vehicles returned to service that still owe a route to their
        // pickup, with the earliest time to retry when none was found.
        private readonly ConcurrentDictionary<int, bool> _resumePickup = new();
        private readonly ConcurrentDictionary<int, DateTime> _resumeRetryAt = new();

        // Vehicles held (paused) until the order that resumes their mission
        // has been delivered; the pause is then released.
        private readonly ConcurrentDictionary<int, bool> _resumeAfterOrder = new();

        private readonly ParkingPools _pools;

        // Vehicle -> its current park / charge-park order (see ParkOrder).
        private readonly ConcurrentDictionary<int, ParkOrder> _parkOrders = new();
        private readonly ConcurrentDictionary<int, DateTime> _parkLogAt = new();

        // SOC bands (percent) that decide where an idle vehicle goes.
        private const decimal MandatoryBelowSoc = 30m;   // < : mandatory charge
        private const decimal ParkOnlyAboveSoc = 75m;    // > : park only, no charge
        private const decimal QueueFloorSoc = 50m;       // queue only if >=

        public FleetManagerService(
            VehicleRegistry registry,
            ActiveMissionRegistry activeMissions,
            RouteWindowManager windows,
            MissionQueueService missionQueue,
            ChannelRegistry channels,
            IRoutingEngine routing,
            ITrafficManager traffic,
            IChargeQueueManager charging,
            ICustomizationApi customization,
            IVehicleAdapter adapter,
            RoadMapGraphHolder graphHolder,
            ParkingPools pools,
            ILoggerFactory loggerFactory)
        {
            _pools = pools;
            _registry = registry;
            _activeMissions = activeMissions;
            _windows = windows;
            _missionQueue = missionQueue;
            _channels = channels;
            _routing = routing;
            _traffic = traffic;
            _charging = charging;
            _customization = customization;
            _adapter = adapter;
            _graphHolder = graphHolder;
            _logger = loggerFactory.CreateLogger(LogDomains.Fleet);
            _dispatchLogger = loggerFactory.CreateLogger(LogDomains.Dispatch);
        }

        // ----------------------------------------------------------------
        // BackgroundService — main loop
        // ----------------------------------------------------------------

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            _logger.LogInformation("FleetManagerService starting");

            // Every vehicle standing on a node holds it from the start.
            await HoldStandingNodesAsync(stoppingToken);

            // Start parallel consumers
            var stateTask = ConsumeVehicleStateUpdatesAsync(stoppingToken);
            var transferTask = ConsumeMissionTransfersAsync(stoppingToken);
            var swapTask = ConsumeMissionSwapsAsync(stoppingToken);
            var dispatchTask = RunDispatchLoopAsync(stoppingToken);

            var decisionTask = ConsumeDispatchDecisionsAsync(stoppingToken);
            var deadlockTask = ConsumeDeadlockRequestsAsync(stoppingToken);

            await Task.WhenAll(stateTask, transferTask,
                               swapTask, dispatchTask, decisionTask,
                               deadlockTask);

            _logger.LogInformation("FleetManagerService stopped");
        }

        /// <summary>
        /// A vehicle standing on a node must hold its lock: take it for
        /// the node if no one holds it. Used at startup (initial or
        /// restored positions) and when a vehicle stays where it is.
        /// </summary>
        private async Task EnsureNodeHeldAsync(Vehicle v, CancellationToken ct)
        {
            if (!v.IsInService) return;
            if (v.CurrentNodeId is not int n || n <= 0) return;
            if (_traffic.GetLockedNodeIds().Contains(n)) return;
            await _traffic.HoldNodeAsync(v.VehicleId, n, ct);
        }

        private async Task HoldStandingNodesAsync(CancellationToken ct)
        {
            var held = 0;
            foreach (var v in _registry.GetAll().OrderBy(x => x.VehicleId))
            {
                var before = _traffic.GetVehicleLockedNodeIds(v.VehicleId).Count;
                await EnsureNodeHeldAsync(v, ct);
                if (_traffic.GetVehicleLockedNodeIds(v.VehicleId).Count > before)
                    held++;
            }
            _logger.LogInformation(
                "Startup: {Held} vehicles now hold the node they stand on", held);
        }

        // ----------------------------------------------------------------
        // IFleetManager implementation
        // ----------------------------------------------------------------

        public IReadOnlyCollection<Vehicle> GetAllVehicles()
            => _registry.GetAll();

        public Vehicle? GetVehicle(int vehicleId)
            => _registry.GetById(vehicleId);

        public Vehicle? GetVehicleBySerialNumber(string serialNumber)
            => _registry.GetBySerialNumber(serialNumber);

        public IReadOnlyCollection<Vehicle> GetAvailableVehicles()
            => _registry.GetAvailableForDispatch();

        public async Task EnqueueMissionAsync(
            MissionContext missionContext,
            CancellationToken cancellationToken = default)
        {
            // Allow customization to modify before queuing
            var context = await _customization.OnMissionCreatedAsync(
                missionContext, cancellationToken);

            var id = Interlocked.Increment(ref _nextMissionId);
            context = context with { MissionId = id };

            _missionQueue.Enqueue(context);
            Interlocked.Increment(ref _enqueuedMissions);
            await _channels.MissionCounters.Writer.WriteAsync(
                new MissionCounterUpdate(
                    _enqueuedMissions, _dispatchedMissions, _completedMissions,
                    _missionQueue.Count),
                cancellationToken);
            _dispatchSignal.Release(); // wakee dispatch loop

            _dispatchLogger.LogInformation(
                "Mission {MissionId} enqueued " +
                "(priority={Priority}, queue depth={Depth})",
                context.MissionId,
                context.Priority,
                _missionQueue.Count);
        }

        public int PendingMissionCount => _missionQueue.Count;

        public FleetMetrics GetMetrics()
        {
            var counts = _registry.GetCounts();
            return new FleetMetrics
            {
                TotalVehicles = counts.Total,
                VehiclesInService = counts.InService,
                VehiclesOnline = counts.Online,
                VehiclesIdle = counts.Idle,
                VehiclesCharging = counts.Charging,
                VehiclesOnMission = counts.OnMission,
                VehiclesOutOfService = counts.OutOfService,
                PendingMissions = _missionQueue.Count,
                CompletedMissionsTotal = _completedMissions,
                TransferredMissionsTotal = _transferredMissions,
                SwappedMissionsTotal = _swappedMissions,
                AverageBatterySoc = counts.AverageSoc,
            };
        }

        public async Task RemoveVehicleFromServiceAsync(
            int vehicleId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            var vehicle = _registry.GetById(vehicleId);
            if (vehicle is null) return;

            // Release all locks immediately
            _windows.Cancel(vehicleId);
            await _traffic.ReleaseAllLocksAsync(
                vehicleId, cancellationToken);

            // Transfer any active mission
            if (vehicle.CurrentMissionId.HasValue)
            {
                await HandleVehicleFaultAsync(
                    vehicleId, cancellationToken);
            }

            vehicle.TakeOutOfService();
            _logger.LogInformation(
                "Vehicle {VehicleId} removed from service: {Reason}",
                vehicleId, reason);
        }

        /// <summary>
        /// Restarts an out-of-service vehicle on a node chosen by the
        /// operator (used after its locks were released and it may have
        /// been moved by hand). Fails, without changing anything, if
        /// another vehicle holds or stands on that node. In the simulator
        /// the vehicle is relocated to the node; a real vehicle is sent
        /// an initPosition for it.
        /// </summary>
        public async Task<(bool Ok, string Message)> RestartVehicleAsync(
            int vehicleId,
            int nodeId,
            CancellationToken cancellationToken = default)
        {
            var vehicle = _registry.GetById(vehicleId);
            if (vehicle is null) return (false, "Unknown vehicle.");
            if (vehicle.IsInService)
                return (false, "Vehicle is already in service.");
            if (nodeId <= 0) return (false, "Enter a restart node.");

            foreach (var other in _registry.GetAll())
            {
                if (other.VehicleId == vehicleId) continue;
                if (_traffic.GetVehicleLockedNodeIds(other.VehicleId)
                        .Contains(nodeId)
                    || other.CurrentNodeId == nodeId)
                {
                    var why = $"Restart refused: node {nodeId} is already " +
                        $"held by vehicle F{other.VehicleId:D2}.";
                    _logger.LogWarning(
                        "Vehicle {VehicleId} restart on node {NodeId} " +
                        "refused - held by vehicle {Other}",
                        vehicleId, nodeId, other.VehicleId);
                    return (false, why);
                }
            }

            var relocated = false;
            var node = _graphHolder.GetRequired().GetNode(nodeId);
            if (node is null)
                return (false, $"Restart refused: node {nodeId} does not exist.");

            if (vehicle.CurrentNodeId != nodeId)
            {
                if (_adapter is ISimulationTestHooks hooks)
                {
                    // Simulator: relocate the simulated vehicle.
                    await hooks.PlaceVehicleAsync(
                        vehicleId, nodeId, false, cancellationToken,
                        keepLoad: true);
                    relocated = true;
                }
                else
                {
                    // Real vehicle: the operator has leashed it to this node
                    // and chosen it; tell the vehicle where it is
                    // (VDA 5050 initPosition) instead of waiting for it to
                    // report the node itself.
                    static ActionParameter P(string k, string v)
                        => new() { Key = k, Value = v };
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    await _adapter.SendInstantActionAsync(
                        vehicle.SerialNumber,
                        new VehicleInstantAction
                        {
                            HeaderId = Guid.NewGuid().ToString("N")[..8],
                            InstantActions = new[]
                            {
                                new OrderAction
                                {
                                    ActionId = Guid.NewGuid().ToString("N")[..8],
                                    ActionType = "initPosition",
                                    BlockingType = "HARD",
                                    Parameters = new[]
                                    {
                                        P("x", node.Position.X.ToString(inv)),
                                        P("y", node.Position.Y.ToString(inv)),
                                        P("theta", "0"),
                                        P("mapId", vehicle.CurrentMapId),
                                        P("lastNodeId", nodeId.ToString()),
                                    },
                                }
                            },
                        },
                        cancellationToken);
                    _logger.LogInformation(
                        "Vehicle {VehicleId}: initPosition sent for node " +
                        "{NodeId} (operator restart)", vehicleId, nodeId);
                }
                vehicle.UpdatePosition(nodeId, vehicle.CurrentMapId);
            }

            await ReturnVehicleToServiceAsync(vehicleId, cancellationToken);

            // A relocated simulated vehicle has dropped its old order and
            // reports Idle. Clear the in-flight order state to match, or
            // the stale-idle guard in the state handler discards every
            // report and the resumed route is never sent.
            if (relocated && !vehicle.IsLoaded)
                vehicle.UpdateState(ActivityState.Idle, OrderState.Idle);

            if (!_traffic.GetLockedNodeIds().Contains(nodeId))
                await _traffic.HoldNodeAsync(
                    vehicleId, nodeId, cancellationToken);

            _logger.LogInformation(
                "Vehicle {VehicleId} restarted on node {NodeId}",
                vehicleId, nodeId);
            return (true, $"Restarted on node {nodeId}.");
        }

        public async Task ReturnVehicleToServiceAsync(
            int vehicleId,
            CancellationToken cancellationToken = default)
        {
            var vehicle = _registry.GetById(vehicleId);
            if (vehicle is null || vehicle.IsInService) return;

            // Any locks kept while out of service are dropped: the resumed
            // route locks what it needs as it goes.
            _windows.Cancel(vehicleId);
            await _traffic.ReleaseAllLocksAsync(vehicleId, cancellationToken);
            _dropLegSent.TryRemove(vehicleId, out _);
            _dropLegRetryAt.TryRemove(vehicleId, out _);
            _resumeRetryAt.TryRemove(vehicleId, out _);

            vehicle.ReturnToService();

            var mission = _activeMissions.GetByVehicle(vehicleId);
            var hasMission = mission is not null
                && vehicle.CurrentMissionId.HasValue;

            if (hasMission && vehicle.IsLoaded
                && vehicle.ActivityState == ActivityState.TravelingLoaded)
            {
                // The drop leg is re-routed from the vehicle's node by the
                // normal drop-leg logic on its next state update; release
                // the pause once that order has been delivered.
                _resumeAfterOrder[vehicleId] = true;
            }
            else if (hasMission && !vehicle.IsLoaded
                && vehicle.ActivityState != ActivityState.Picking
                && vehicle.ActivityState != ActivityState.Dropping)
            {
                // Resume toward the pickup on a new route.
                _resumePickup[vehicleId] = true;
                _resumeAfterOrder[vehicleId] = true;
            }
            else
            {
                // Idle, or in the middle of a pick/drop: just carry on.
                await SendPauseAsync(vehicle, false, cancellationToken);
            }

            _logger.LogInformation(
                "Vehicle {VehicleId} returned to service (mission {MissionId})",
                vehicleId, vehicle.CurrentMissionId);
        }

        public async Task<bool> SetVehicleManualAsync(
            int vehicleId,
            bool manual,
            CancellationToken cancellationToken = default)
        {
            var vehicle = _registry.GetById(vehicleId);
            if (vehicle is null || !vehicle.IsInService
                || vehicle.IsManual == manual)
                return false;

            vehicle.SetManual(manual);
            await SendPauseAsync(vehicle, manual, cancellationToken);

            if (!manual)
                await PublishRemainingRouteAsync(vehicle, cancellationToken);

            _logger.LogInformation(
                "Vehicle {VehicleId} {Mode}",
                vehicleId, manual ? "set to MANUAL" : "returned to AUTO");
            return true;
        }

        public async Task<bool> TakeVehicleOutOfServiceAsync(
            int vehicleId,
            bool keepLocks,
            CancellationToken cancellationToken = default)
        {
            var vehicle = _registry.GetById(vehicleId);
            if (vehicle is null || !vehicle.IsInService) return false;

            // Out of service retracts the charger contact: a charging
            // vehicle stops charging. (Paused/manual does not: a paused
            // vehicle on a charger keeps charging.)
            if (vehicle.ActivityState == ActivityState.OpportunityCharging
                || vehicle.ActivityState == ActivityState.MandatoryCharging)
                await SendActionAsync(vehicle, "stopCharging", cancellationToken);

            // Stop the vehicle, then flush its route. The mission stays.
            await SendPauseAsync(vehicle, true, cancellationToken);
            _windows.Cancel(vehicleId);
            if (!keepLocks)
                await _traffic.ReleaseAllLocksAsync(vehicleId, cancellationToken);

            _dropLegSent.TryRemove(vehicleId, out _);
            _dropLegRetryAt.TryRemove(vehicleId, out _);
            _resumePickup.TryRemove(vehicleId, out _);
            _resumeRetryAt.TryRemove(vehicleId, out _);
            _resumeAfterOrder.TryRemove(vehicleId, out _);
            _parkOrders.TryRemove(vehicleId, out _);

            vehicle.ClearPlannedRoute();
            vehicle.SetManual(false);
            vehicle.TakeOutOfService();

            // Tell the dashboard the route is gone.
            await _channels.MissionUpdates.Writer.WriteAsync(
                new VehicleMissionUpdate(
                    vehicleId, vehicle.CurrentMissionId, Array.Empty<int>()),
                cancellationToken);

            _logger.LogInformation(
                "Vehicle {VehicleId} taken out of service " +
                "(mission {MissionId} kept, locks {Locks})",
                vehicleId, vehicle.CurrentMissionId,
                keepLocks ? "KEPT" : "released");
            return true;
        }

        private Task SendActionAsync(
            Vehicle vehicle, string actionType,
            CancellationToken cancellationToken)
            => _adapter.SendInstantActionAsync(
                vehicle.SerialNumber,
                new VehicleInstantAction
                {
                    HeaderId = Guid.NewGuid().ToString("N")[..8],
                    InstantActions = new[]
                    {
                        new OrderAction
                        {
                            ActionId = Guid.NewGuid().ToString("N")[..8],
                            ActionType = actionType,
                            BlockingType = "HARD",
                        }
                    },
                },
                cancellationToken);

        private Task SendPauseAsync(
            Vehicle vehicle, bool pause, CancellationToken cancellationToken)
            => _adapter.SendInstantActionAsync(
                vehicle.SerialNumber,
                new VehicleInstantAction
                {
                    HeaderId = Guid.NewGuid().ToString("N")[..8],
                    InstantActions = new[]
                    {
                        new OrderAction
                        {
                            ActionId = Guid.NewGuid().ToString("N")[..8],
                            ActionType = pause ? "startPause" : "stopPause",
                            BlockingType = "HARD",
                        }
                    },
                },
                cancellationToken);

        /// <summary>
        /// After manual mode ends, re-sends the remaining part of the
        /// planned route to the dashboard so the map continues from the
        /// vehicle's node.
        /// </summary>
        private async Task PublishRemainingRouteAsync(
            Vehicle vehicle, CancellationToken cancellationToken)
        {
            var planned = vehicle.PlannedRouteNodeIds;
            if (planned.Count == 0 || !vehicle.CurrentMissionId.HasValue) return;

            var list = planned.ToList();
            var idx = vehicle.CurrentNodeId is int cur ? list.IndexOf(cur) : -1;
            var remaining = idx > 0 ? list.Skip(idx).ToList() : list;

            await _channels.MissionUpdates.Writer.WriteAsync(
                new VehicleMissionUpdate(
                    vehicle.VehicleId, vehicle.CurrentMissionId, remaining),
                cancellationToken);
        }

        // ----------------------------------------------------------------
        // Channel consumers
        // ----------------------------------------------------------------

        private async Task ConsumeVehicleStateUpdatesAsync(
            CancellationToken stoppingToken)
        {
            await foreach (var update in
                _channels.VehicleStateUpdates.Reader
                    .ReadAllAsync(stoppingToken))
            {
                await ProcessVehicleStateUpdateAsync(update, stoppingToken);
            }
        }

        private async Task ConsumeMissionTransfersAsync(
            CancellationToken stoppingToken)
        {
            await foreach (var transfer in
                _channels.MissionTransfers.Reader
                    .ReadAllAsync(stoppingToken))
            {
                await ProcessMissionTransferAsync(transfer, stoppingToken);
            }
        }

        // ----------------------------------------------------------------
        // Deadlock resolution
        // ----------------------------------------------------------------

        // Last time an "unresolved" alert was raised, per cycle signature.
        // Touched only by the deadlock consumer loop.
        private readonly Dictionary<string, DateTime> _deadlockAlertedAt = new();

        private async Task ConsumeDeadlockRequestsAsync(
            CancellationToken stoppingToken)
        {
            await foreach (var request in
                _channels.DeadlockRequests.Reader
                    .ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ResolveDeadlockAsync(request, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _dispatchLogger.LogError(ex,
                        "Deadlock resolution failed for {Vehicles}",
                        string.Join(",", request.VehicleIds));
                }
            }
        }

        /// <summary>
        /// Breaks a circular wait by re-routing one vehicle. The route is
        /// planned from the vehicle's commanded-to node to the original
        /// destination, avoiding every node and move held by other
        /// vehicles. Locks beyond the commanded-to node are released and
        /// the new path replaces the rest of the itinerary; the vehicle
        /// keeps driving what it has already been commanded.
        /// </summary>
        private async Task ResolveDeadlockAsync(
            DeadlockResolutionRequest request,
            CancellationToken stoppingToken)
        {
            // Every vehicle in the cycle must have come to rest at the end
            // of its commanded route. A vehicle still driving will release
            // nodes as it goes, so the wait may clear by itself.
            var points = new Dictionary<int, RouteWindowManager.CommitPoint>();
            foreach (var id in request.VehicleIds)
            {
                var point = await _windows.GetCommitPointAsync(
                    id, stoppingToken);
                if (point is { } p) points[id] = p;
            }

            if (points.Count < request.VehicleIds.Count
                || points.Values.Any(p => p.ReachedIndex < p.ReleasedIndex))
                return;

            // Victim order: fewest locks first
            var candidates = request.VehicleIds
                .OrderBy(id => _traffic.GetVehicleLockedNodeIds(id).Count)
                .ThenByDescending(id => id)
                .ToList();

            foreach (var id in candidates)
            {
                var vehicle = _registry.GetById(id);
                if (vehicle is null || !vehicle.IsInService
                    || vehicle.IsManual)
                    continue;

                var point = points[id];

                // Block what OTHER vehicles hold (and engineer blocks);
                // the vehicle's own locks are not obstacles to itself.
                var blockedNodes = new HashSet<int>(_traffic.GetLockedNodeIds());
                blockedNodes.ExceptWith(_traffic.GetVehicleLockedNodeIds(id));
                var blockedMoves = new HashSet<int>(_traffic.GetLockedMoveIds());
                blockedMoves.ExceptWith(_traffic.GetVehicleLockedMoveIds(id));

                var route = await _routing.FindRouteAsync(
                    point.NodeId,
                    point.FinalNodeId,
                    id,
                    blockedNodes,
                    blockedMoves,
                    stoppingToken);

                if (route is null)
                {
                    _dispatchLogger.LogInformation(
                        "Deadlock: no alternate route for vehicle {VehicleId} " +
                        "from node {From} to {To}",
                        id, point.NodeId, point.FinalNodeId);
                    continue;
                }

                var nodeIds = route.Nodes.Select(n => n.NodeId).ToList();
                if (!await _windows.ReplaceTailAsync(
                        id, point, nodeIds, route.MoveIds, stoppingToken))
                    return; // itinerary moved on; the request is retried

                _traffic.ClearContentions(id);
                DeadlockMetrics.RecordResolved();

                // Show the new route on the dashboard
                var remaining = await _windows.GetRemainingNodesAsync(
                    id, stoppingToken);
                vehicle.SetPlannedRoute(remaining.AsReadOnly());
                await _channels.MissionUpdates.Writer.WriteAsync(
                    new VehicleMissionUpdate(
                        id, vehicle.CurrentMissionId, remaining),
                    stoppingToken);

                _dispatchLogger.LogWarning(
                    "Deadlock resolved: vehicle {VehicleId} re-routed from " +
                    "node {From} to {To} ({Hops} hops) around nodes held " +
                    "by {Cycle}",
                    id, point.NodeId, point.FinalNodeId, nodeIds.Count,
                    string.Join(",", request.VehicleIds));
                return;
            }

            // Nobody could be re-routed: tell the operator (not too often)
            var signature = string.Join(",",
                request.VehicleIds.OrderBy(v => v));
            if (_deadlockAlertedAt.TryGetValue(signature, out var alertedAt)
                && DateTime.UtcNow - alertedAt < TimeSpan.FromSeconds(60))
                return;

            _deadlockAlertedAt[signature] = DateTime.UtcNow;
            DeadlockMetrics.RecordUnresolved();
            _ = _channels.Alerts.Writer.TryWrite(
                new AlertUpdate(
                    AlertType.Deadlock,
                    request.VehicleIds[0],
                    "Deadlock unresolved: no alternate route for " +
                    string.Join(", ", request.VehicleIds.Select(v => $"V{v}")) +
                    " — operator intervention required",
                    DateTime.UtcNow));
        }

        private async Task ConsumeMissionSwapsAsync(
            CancellationToken stoppingToken)
        {
            await foreach (var swap in
                _channels.MissionSwaps.Reader
                    .ReadAllAsync(stoppingToken))
            {
                await ProcessMissionSwapAsync(swap, stoppingToken);
            }
        }

        // ----------------------------------------------------------------
        // Dispatch loop
        // ----------------------------------------------------------------

        private async Task RunDispatchLoopAsync(
            CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for a mission signal OR periodic wake for charging eval
                    await _dispatchSignal.WaitAsync(
                        TimeSpan.FromSeconds(5), stoppingToken);

                    if (stoppingToken.IsCancellationRequested) break;

                    // Escalate missions approaching deadline
                    EscalateApproachingDeadlines();

                    // Evaluate charging needs for idle vehicles
                    await TryDispatchPendingMissionsAsync(stoppingToken);

                    // Evaluate charging needs for idle vehicles
                    await EvaluateChargingAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Dispatch loop iteration failed — continuing");
                }
            }
        }
        // No-route diagnostics: say WHY, and at most once per mission+vehicle
        // every 30 s (this used to log every candidate on every 5 s pass).
        private readonly ConcurrentDictionary<(int Mission, int Vehicle), DateTime>
            _noRouteLoggedAt = new();

        private async Task LogNoRouteAsync(
            Vehicle vehicle, int missionId, int targetNodeId,
            IReadOnlySet<int> blockedNodes, CancellationToken ct)
        {
            var key = (missionId, vehicle.VehicleId);
            var now = DateTime.UtcNow;
            if (_noRouteLoggedAt.TryGetValue(key, out var last)
                && (now - last).TotalSeconds < 30)
                return;
            _noRouteLoggedAt[key] = now;

            string why;
            try
            {
                // Route ignoring all locks: does one exist at all?
                var free = await _routing.FindRouteAsync(
                    vehicle.CurrentNodeId ?? 0, targetNodeId, vehicle.VehicleId,
                    new HashSet<int>(), new HashSet<int>(), ct);

                if (free is null)
                {
                    why = "no route exists even with no locks (topology / stop-node rules)";
                }
                else
                {
                    var holders = free.Nodes
                        .Select(n => n.NodeId)
                        .Where(blockedNodes.Contains)
                        .Select(n =>
                        {
                            var by = StaticLockHolders(vehicle.VehicleId)
                                .Select(id => _registry.GetById(id)!)
                                .Where(v => _traffic.GetVehicleLockedNodeIds(v.VehicleId).Contains(n))
                                .Select(v => "F" + v.VehicleId.ToString("D2"))
                                .FirstOrDefault() ?? "?";
                            return $"{n}({by})";
                        })
                        .Distinct()
                        .Take(8);
                    why = "blocked by out-of-service/manual vehicle locks on the unobstructed route: "
                        + string.Join(" ", holders);
                }
            }
            catch (Exception ex)
            {
                why = "diagnostic failed: " + ex.Message;
            }

            _dispatchLogger.LogWarning(
                "No route found for vehicle {VehicleId} from node {From} to mission " +
                "{MissionId} target {NodeId} — {Why}",
                vehicle.VehicleId, vehicle.CurrentNodeId, missionId, targetNodeId, why);
        }

        // Route PLANNING treats only locks that will not clear by themselves
        // as obstacles: those held by vehicles that are out of service or in
        // manual. Locks of in-service vehicles are transient (taken and
        // released as they move); the route window / clearance check makes
        // a vehicle wait for them at run time.
        private IEnumerable<int> StaticLockHolders(int exceptVehicleId)
            => _registry.GetAll()
                .Where(v => v.VehicleId != exceptVehicleId
                            && (!v.IsInService || v.IsManual))
                .Select(v => v.VehicleId)
                .ToList();

        private HashSet<int> BlockedNodesFor(int vehicleId)
        {
            var s = new HashSet<int>();
            foreach (var id in StaticLockHolders(vehicleId))
                s.UnionWith(_traffic.GetVehicleLockedNodeIds(id));
            return s;
        }

        private HashSet<int> BlockedMovesFor(int vehicleId)
        {
            var s = new HashSet<int>();
            foreach (var id in StaticLockHolders(vehicleId))
                s.UnionWith(_traffic.GetVehicleLockedMoveIds(id));
            return s;
        }

        private async Task TryDispatchPendingMissionsAsync(
            CancellationToken stoppingToken)
        {
            while (!_missionQueue.IsEmpty)
            {
                var mission = _missionQueue.Peek();
                if (mission is null) break;

                var candidates = SelectCandidateVehicles(mission);
                if (candidates.Count == 0) break;

                // Route every candidate to the pickup and rank by the
                // shortest ROUTE (estimated travel time), not straight-line
                // distance. SOC breaks ties.
                var routedAll = await Task.WhenAll(candidates.Select(async cand =>
                {
                    // A vehicle's own locks (the node it stands on) are not
                    // obstacles to its own route.
                    var candBlocked = BlockedNodesFor(cand.VehicleId);
                    var candRoute = await _routing.FindRouteAsync(
                        cand.CurrentNodeId ?? 0,
                        mission.PickupNodeId,
                        cand.VehicleId,
                        candBlocked,
                        BlockedMovesFor(cand.VehicleId),
                        stoppingToken);

                    if (candRoute is null)
                        await LogNoRouteAsync(
                            cand, mission.MissionId, mission.PickupNodeId,
                            candBlocked, stoppingToken);

                    return new { Vehicle = cand, Route = candRoute };
                }));

                var ranked = routedAll
                    .Where(x => x.Route is not null)
                    .OrderBy(x => x.Route!.EstimatedTravelTimeSeconds)
                    .ThenByDescending(x => x.Vehicle.BatteryStateOfCharge)
                    .ToList();

                bool dispatched = false;
                foreach (var rc in ranked)
                {
                    var vehicle = rc.Vehicle;
                    var route = rc.Route!;
                    // Route found — dequeue and dispatch
                    _missionQueue.Dequeue();

                    // A park / discretionary-charge order is crushed
                    // (cancelled) by the mission; the new order replaces it.
                    await CrushParkOrderAsync(
                        vehicle, mission.MissionId, stoppingToken);

                    _dispatchLogger.LogInformation(
                        "Dispatching vehicle {VehicleId} from node {NodeId} to pickup {PickupNodeId}",
                        vehicle.VehicleId,
                        vehicle.CurrentNodeId,
                        mission.PickupNodeId);

                    vehicle.AssignMission(
                        mission.MissionId,
                        route.Nodes.Select(n => n.NodeId).ToList().AsReadOnly());
                    Interlocked.Increment(ref _dispatchedMissions);
                    await _channels.MissionCounters.Writer.WriteAsync(
                        new MissionCounterUpdate(
                            _enqueuedMissions, _dispatchedMissions, _completedMissions,
                            _missionQueue.Count),
                        stoppingToken);
                    vehicle.UpdateState(
                        ActivityState.TravelingToPickup,
                        OrderState.Waiting);
                    var dispatchedMission = mission with
                    {
                        CurrentVehicleId = vehicle.VehicleId,
                        CurrentOrderId = Guid.NewGuid()
                            .ToString("N")[..8].ToUpper()
                    };
                    _activeMissions.Set(vehicle.VehicleId, dispatchedMission);
                    var decision = new MissionDispatchDecision
                    {
                        MissionId = mission.MissionId,
                        OrderId = dispatchedMission.CurrentOrderId,
                        VehicleId = vehicle.VehicleId,
                        SerialNumber = vehicle.SerialNumber,
                        RouteNodeIds = route.Nodes
                            .Select(n => n.NodeId).ToList().AsReadOnly(),
                        RouteMoveIds = route.MoveIds,
                        PickupAssignmentId = mission.PickupNodeId,
                        DropoffAssignmentId = mission.DropNodeId,
                        EstimatedTravelTimeSeconds =
                            route.EstimatedTravelTimeSeconds,
                    };
                    await _channels.DispatchDecisions.Writer
                        .WriteAsync(decision, stoppingToken);
                    await _channels.MissionUpdates.Writer.WriteAsync(
                        new VehicleMissionUpdate(
                            vehicle.VehicleId,
                            mission.MissionId,
                            vehicle.PlannedRouteNodeIds),
                        stoppingToken);
                    _dispatchLogger.LogInformation(
                        "Dispatched vehicle {VehicleId} → " +
                        "mission {MissionId} " +
                        "(ETA {ETA:F0}s, route {Hops} hops)",
                        vehicle.VehicleId,
                        mission.MissionId,
                        route.EstimatedTravelTimeSeconds,
                        route.Nodes.Count);

                    dispatched = true;
                    break;
                }

                if (!dispatched)
                {
                    _dispatchLogger.LogWarning(
                        "No vehicle could route to mission {MissionId} pickup {NodeId} — re-queuing",
                        mission.MissionId,
                        mission.PickupNodeId);
                    _missionQueue.Dequeue();
                    _missionQueue.Enqueue(mission);
                    break;
                }
            }
        }

        // ----------------------------------------------------------------
        // Vehicle state processing
        // ----------------------------------------------------------------

        private async Task ProcessVehicleStateUpdateAsync(
            VehicleStateUpdate update,
            CancellationToken stoppingToken)
        {

            var vehicle = _registry.GetBySerialNumber(update.SerialNumber);
            if (vehicle is null)
            {
                _logger.LogWarning(
                    "State update for unknown serial {Serial}",
                    update.SerialNumber);
                return;
            }

            // Don't downgrade from an active dispatch state
            if (update.ActivityState == ActivityState.Idle
                && update.OrderState == OrderState.Idle
                && vehicle.OrderState == OrderState.Waiting
                && update.OrderState != OrderState.Finished)
            {
                _logger.LogDebug(
                    "Guard fired: Activity={Act} Order={Ord} VehicleOrder={VOrd}",
                    update.ActivityState, update.OrderState, vehicle.OrderState);

                return; // stale idle report — order is in-flight
            }

            // Update vehicle state
            vehicle.UpdateBattery(update.BatteryStateOfCharge);
            vehicle.UpdateState(update.ActivityState, update.OrderState);
            vehicle.SetLoaded(update.IsLoaded);

            if (update.LastNodeId > 0)
            {
                vehicle.UpdatePosition(update.LastNodeId,
                    vehicle.CurrentMapId);

                // Slide the lock window as the vehicle reaches nodes.
                // A manual vehicle holds what it has: no sliding.
                if (vehicle.IsInService && !vehicle.IsManual)
                    await _windows.OnVehicleStateAsync(
                        vehicle.VehicleId, update.LastNodeId, stoppingToken);
            }

            // Arrived at the node of its park / charge-park order
            if (_parkOrders.TryGetValue(vehicle.VehicleId, out var po)
                && !po.Arrived && update.LastNodeId == po.NodeId
                && !vehicle.CurrentMissionId.HasValue)
            {
                _parkOrders[vehicle.VehicleId] = po with { Arrived = true };
                vehicle.ClearPlannedRoute();
                await _channels.MissionUpdates.Writer.WriteAsync(
                    new VehicleMissionUpdate(
                        vehicle.VehicleId, null, Array.Empty<int>()),
                    stoppingToken);
                _dispatchLogger.LogInformation(
                    "Vehicle {VehicleId} parked at node {NodeId} ({Kind}, SOC {Soc:F0}%)",
                    vehicle.VehicleId, po.NodeId, po.Kind,
                    vehicle.BatteryStateOfCharge);
            }

            // Picked up: send the drop leg as a new order
            await TryDispatchDropLegAsync(vehicle, update, stoppingToken);

            // Returned to service before its pickup: route there now
            await TryDispatchResumePickupAsync(vehicle, stoppingToken);

            // Check for connection/error states
            if (update.Errors.Count > 0)
            {
                _logger.LogWarning(
                    "Vehicle {VehicleId} reporting {ErrorCount} error(s): {Errors}",
                    vehicle.VehicleId,
                    update.Errors.Count,
                    string.Join(", ", update.Errors));
            }

            // Mission completion detection
            _logger.LogDebug(
                "CompletionCheck: Vehicle={VehicleId} UpdateOrder={UOrd} MissionId={Mid}",
                vehicle.VehicleId, update.OrderState, vehicle.CurrentMissionId);

            // Mission completion detection
            if (update.OrderState == OrderState.Finished
                && vehicle.CurrentMissionId.HasValue)
            {
                vehicle.ClearMission();
                _activeMissions.Remove(vehicle.VehicleId);
                _dropLegSent.TryRemove(vehicle.VehicleId, out _);
                _dropLegRetryAt.TryRemove(vehicle.VehicleId, out _);
                await _channels.MissionUpdates.Writer.WriteAsync(
                    new VehicleMissionUpdate(
                        vehicle.VehicleId,
                        null,
                        Array.Empty<int>()),
                    stoppingToken);
                // Idle again: let the dispatch loop park/charge it now
                // instead of waiting for the periodic wake.
                _dispatchSignal.Release();
                Interlocked.Increment(ref _completedMissions); _dispatchLogger.LogInformation(
                    "Vehicle {VehicleId} completed mission — total completed: {Total}",
                    vehicle.VehicleId, _completedMissions);
                await _channels.MissionCounters.Writer.WriteAsync(
                    new MissionCounterUpdate(
                        _enqueuedMissions, _dispatchedMissions, _completedMissions,
                        _missionQueue.Count),
                    stoppingToken);
            }

            await Task.CompletedTask;
        }

        // ----------------------------------------------------------------
        // Drop leg
        // ----------------------------------------------------------------

        /// <summary>
        /// Once the vehicle has picked up (loaded, traveling loaded) and
        /// has not yet been given a route to the drop, routes from its
        /// current node to the mission's drop node and sends that as a new
        /// order. Retries every two seconds when no route is available.
        /// </summary>
        private async Task TryDispatchDropLegAsync(
            Vehicle vehicle,
            VehicleStateUpdate update,
            CancellationToken stoppingToken)
        {
            if (!vehicle.CurrentMissionId.HasValue) return;
            if (!vehicle.IsInService || vehicle.IsManual) return;
            if (!update.IsLoaded
                || update.ActivityState != ActivityState.TravelingLoaded)
                return;

            var mission = _activeMissions.GetByVehicle(vehicle.VehicleId);
            if (mission is null || mission.DropNodeId <= 0) return;

            if (_dropLegSent.TryGetValue(vehicle.VehicleId, out var sentFor)
                && sentFor == mission.MissionId)
                return;

            if (_dropLegRetryAt.TryGetValue(vehicle.VehicleId, out var retryAt)
                && DateTime.UtcNow < retryAt)
                return;

            var route = await _routing.FindRouteAsync(
                vehicle.CurrentNodeId ?? 0,
                mission.DropNodeId,
                vehicle.VehicleId,
                BlockedNodesFor(vehicle.VehicleId),
                BlockedMovesFor(vehicle.VehicleId),
                stoppingToken);

            if (route is null)
            {
                _dropLegRetryAt[vehicle.VehicleId] =
                    DateTime.UtcNow.AddSeconds(2);
                _dispatchLogger.LogWarning(
                    "No route for vehicle {VehicleId} from node {From} " +
                    "to drop {DropNodeId} (mission {MissionId}) — retrying",
                    vehicle.VehicleId, vehicle.CurrentNodeId,
                    mission.DropNodeId, mission.MissionId);
                return;
            }

            var orderId = Guid.NewGuid().ToString("N")[..8].ToUpper();
            _activeMissions.Set(
                vehicle.VehicleId, mission with { CurrentOrderId = orderId });
            _dropLegSent[vehicle.VehicleId] = mission.MissionId;
            _dropLegRetryAt.TryRemove(vehicle.VehicleId, out _);

            var routeNodeIds = route.Nodes
                .Select(n => n.NodeId).ToList().AsReadOnly();

            vehicle.AssignMission(mission.MissionId, routeNodeIds);
            vehicle.UpdateState(
                ActivityState.TravelingLoaded, OrderState.Waiting);

            await _channels.DispatchDecisions.Writer.WriteAsync(
                new MissionDispatchDecision
                {
                    MissionId = mission.MissionId,
                    OrderId = orderId,
                    VehicleId = vehicle.VehicleId,
                    SerialNumber = vehicle.SerialNumber,
                    RouteNodeIds = routeNodeIds,
                    RouteMoveIds = route.MoveIds,
                    PickupAssignmentId = mission.PickupNodeId,
                    DropoffAssignmentId = mission.DropNodeId,
                    EstimatedTravelTimeSeconds =
                        route.EstimatedTravelTimeSeconds,
                }, stoppingToken);

            await _channels.MissionUpdates.Writer.WriteAsync(
                new VehicleMissionUpdate(
                    vehicle.VehicleId, mission.MissionId, routeNodeIds),
                stoppingToken);

            _dispatchLogger.LogInformation(
                "Drop leg sent: vehicle {VehicleId} mission {MissionId} " +
                "node {From} → drop {DropNodeId} " +
                "(ETA {ETA:F0}s, route {Hops} hops)",
                vehicle.VehicleId, mission.MissionId,
                vehicle.CurrentNodeId, mission.DropNodeId,
                route.EstimatedTravelTimeSeconds, route.Nodes.Count);
        }

        /// <summary>
        /// A vehicle returned to service before reaching its pickup gets a
        /// new route from wherever it is. Retries every two seconds when
        /// no route is available.
        /// </summary>
        private async Task TryDispatchResumePickupAsync(
            Vehicle vehicle,
            CancellationToken stoppingToken)
        {
            if (!_resumePickup.ContainsKey(vehicle.VehicleId)) return;
            if (!vehicle.IsInService || vehicle.IsManual) return;

            var mission = _activeMissions.GetByVehicle(vehicle.VehicleId);
            if (mission is null || vehicle.IsLoaded)
            {
                _resumePickup.TryRemove(vehicle.VehicleId, out _);
                await SendPauseAsync(vehicle, false, stoppingToken);
                return;
            }

            if (_resumeRetryAt.TryGetValue(vehicle.VehicleId, out var retryAt)
                && DateTime.UtcNow < retryAt)
                return;

            var route = await _routing.FindRouteAsync(
                vehicle.CurrentNodeId ?? 0,
                mission.PickupNodeId,
                vehicle.VehicleId,
                BlockedNodesFor(vehicle.VehicleId),
                BlockedMovesFor(vehicle.VehicleId),
                stoppingToken);

            if (route is null)
            {
                _resumeRetryAt[vehicle.VehicleId] =
                    DateTime.UtcNow.AddSeconds(2);
                _dispatchLogger.LogWarning(
                    "No route for resumed vehicle {VehicleId} from node " +
                    "{From} to pickup {PickupNodeId} (mission {MissionId}) " +
                    "— retrying",
                    vehicle.VehicleId, vehicle.CurrentNodeId,
                    mission.PickupNodeId, mission.MissionId);
                return;
            }

            _resumePickup.TryRemove(vehicle.VehicleId, out _);
            _resumeRetryAt.TryRemove(vehicle.VehicleId, out _);

            var orderId = Guid.NewGuid().ToString("N")[..8].ToUpper();
            _activeMissions.Set(
                vehicle.VehicleId, mission with { CurrentOrderId = orderId });

            var routeNodeIds = route.Nodes
                .Select(n => n.NodeId).ToList().AsReadOnly();

            vehicle.AssignMission(mission.MissionId, routeNodeIds);
            vehicle.UpdateState(
                ActivityState.TravelingToPickup, OrderState.Waiting);

            await _channels.DispatchDecisions.Writer.WriteAsync(
                new MissionDispatchDecision
                {
                    MissionId = mission.MissionId,
                    OrderId = orderId,
                    VehicleId = vehicle.VehicleId,
                    SerialNumber = vehicle.SerialNumber,
                    RouteNodeIds = routeNodeIds,
                    RouteMoveIds = route.MoveIds,
                    PickupAssignmentId = mission.PickupNodeId,
                    DropoffAssignmentId = mission.DropNodeId,
                    EstimatedTravelTimeSeconds =
                        route.EstimatedTravelTimeSeconds,
                }, stoppingToken);

            await _channels.MissionUpdates.Writer.WriteAsync(
                new VehicleMissionUpdate(
                    vehicle.VehicleId, mission.MissionId, routeNodeIds),
                stoppingToken);

            _dispatchLogger.LogInformation(
                "Resume: vehicle {VehicleId} mission {MissionId} " +
                "node {From} → pickup {PickupNodeId} (route {Hops} hops)",
                vehicle.VehicleId, mission.MissionId,
                vehicle.CurrentNodeId, mission.PickupNodeId,
                route.Nodes.Count);
        }

        // ----------------------------------------------------------------
        // Mission transfer processing
        // ----------------------------------------------------------------

        private async Task ProcessMissionTransferAsync(
            MissionTransfer transfer,
            CancellationToken stoppingToken)
        {
            // CRITICAL: release all locks FIRST
            _windows.Cancel(transfer.FromVehicleId);
            await _traffic.ReleaseAllLocksAsync(
                transfer.FromVehicleId, stoppingToken);

            // Take the full mission BEFORE it is removed from the registry.
            // The transfer's own MissionContext can be a stub (fault and
            // deadlock paths), which would lose pickup, drop, parts and
            // SourceSystemReference.
            var activeMission =
                _activeMissions.GetByVehicle(transfer.FromVehicleId);

            var fromVehicle = _registry.GetById(transfer.FromVehicleId);
            fromVehicle?.ClearMission();
            _activeMissions.Remove(transfer.FromVehicleId);
            _dropLegSent.TryRemove(transfer.FromVehicleId, out _);
            _dropLegRetryAt.TryRemove(transfer.FromVehicleId, out _);

            var context = activeMission ?? transfer.MissionContext;

            if (context.MissionId <= 0)
            {
                // No real mission behind this transfer (e.g. the deadlock
                // stub on a vehicle that has none): nothing to re-queue.
                _logger.LogWarning(
                    "Transfer from vehicle {VehicleId} carries no mission " +
                    "(reason={Reason}) — nothing re-queued",
                    transfer.FromVehicleId, transfer.Reason);
            }
            else if (transfer.ReturnToQueue || !transfer.HasReceivingVehicle)
            {
                // Re-queue with escalated priority
                var escalated = context.WithTransfer(
                    transfer.FromVehicleId,
                    null,
                    string.Empty,
                    transfer.Reason);

                _missionQueue.RequeueAfterFault(
                    escalated,
                    (MissionPriority)transfer.EscalatedPriority);

                Interlocked.Increment(ref _transferredMissions);

                _logger.LogInformation(
                    "Mission {MissionId} returned to queue after " +
                    "fault on vehicle {VehicleId} " +
                    "(reason={Reason})",
                    context.MissionId,
                    transfer.FromVehicleId,
                    transfer.Reason);
            }

            await _customization.OnMissionFaultedAsync(
                transfer, stoppingToken);
        }

        // ----------------------------------------------------------------
        // Mission swap processing
        // ----------------------------------------------------------------

        private async Task ProcessMissionSwapAsync(
            MissionSwap swap,
            CancellationToken stoppingToken)
        {
            var approved = await _customization
                .OnSwapCandidateDetectedAsync(swap, stoppingToken);

            if (!approved)
            {
                _logger.LogInformation(
                    "Mission swap declined by customization layer: " +
                    "V{VehicleA}/M{MissionA} ↔ V{VehicleB}/M{MissionB}",
                    swap.VehicleIdA, swap.MissionIdA,
                    swap.VehicleIdB, swap.MissionIdB);
                return;
            }

            // Swap is approved — vehicles exchange drop destinations
            // Full implementation in SwapExecutorService
            Interlocked.Increment(ref _swappedMissions);

            _logger.LogInformation(
                "Mission swap approved: " +
                "V{VehicleA}/M{MissionA} ↔ V{VehicleB}/M{MissionB} " +
                "(reason={Reason})",
                swap.VehicleIdA, swap.MissionIdA,
                swap.VehicleIdB, swap.MissionIdB,
                swap.Reason);
        }

        // ----------------------------------------------------------------
        // Vehicle selection
        // ----------------------------------------------------------------

        /// <summary>
        /// Vehicles that can take the mission: idle ones, plus vehicles
        /// heading to / sitting at a park or discretionary-charge location
        /// (their park order is crushed when chosen). Mandatory-charge
        /// vehicles and anything below the mandatory SOC are never
        /// candidates. Ordered by proximity to the pickup.
        /// </summary>
        private List<Vehicle> SelectCandidateVehicles(MissionContext mission)
        {
            var all = _registry.GetInService()
                .Where(v => v.IsOnline && !v.IsManual && !v.IsLoaded
                         && !v.CurrentMissionId.HasValue
                         && v.BatteryStateOfCharge >= MandatoryBelowSoc)
                .ToList();

            var candidates = all
                .Where(v => v.IsAvailableForDispatch
                         || (_parkOrders.TryGetValue(v.VehicleId, out var po)
                             && po.Kind != ParkKind.Mandatory))
                .ToList();

            // Queue discipline: vehicles still on their way to (or waiting
            // behind) the queue head cannot pass it, so they are not
            // candidates while another vehicle sits at the head.
            if (_pools.QueueNodeId is int qn
                && _parkOrders.Any(kv => kv.Value.NodeId == qn && kv.Value.Arrived))
            {
                candidates = candidates
                    .Where(v => !(_parkOrders.TryGetValue(v.VehicleId, out var qpo)
                                  && qpo.NodeId == qn && !qpo.Arrived))
                    .ToList();
            }

            if (candidates.Count == 0 && (DateTime.UtcNow - _lastNoCandLog).TotalSeconds > 30)
            {
                _lastNoCandLog = DateTime.UtcNow;
                var why = _registry.GetAll().OrderBy(v => v.VehicleId).Select(v =>
                {
                    string r;
                    if (!v.IsInService) r = "oos";
                    else if (!v.IsOnline) r = "offline";
                    else if (v.IsManual) r = "manual";
                    else if (v.IsLoaded) r = "loaded";
                    else if (v.CurrentMissionId.HasValue) r = "mission" + v.CurrentMissionId;
                    else if (v.BatteryStateOfCharge < MandatoryBelowSoc) r = "soc" + (int)v.BatteryStateOfCharge;
                    else if (_parkOrders.TryGetValue(v.VehicleId, out var d))
                        r = d.Kind + (d.Arrived ? "@" : "->") + d.NodeId;
                    else r = "noOrder:" + v.ActivityState + "/" + v.OrderState;
                    return "F" + v.VehicleId + "=" + r;
                });
                _logger.LogInformation("No candidates: {Why}", string.Join(" ", why));
            }

            _logger.LogInformation(
                "SelectCandidateVehicles: {Total} registered, {Candidates} candidates " +
                "({Parked} parked/parking)",
                _registry.Count, candidates.Count,
                candidates.Count(v => _parkOrders.ContainsKey(v.VehicleId)));

            return candidates
                .OrderBy(v => DistanceBetween(
                    v.CurrentNodeId ?? 0, mission.PickupNodeId))
                .ThenByDescending(v => v.BatteryStateOfCharge)
                .ToList();
        }

        /// <summary>Straight-line distance between two nodes (map units).</summary>
        private double DistanceBetween(int nodeA, int nodeB)
        {
            if (!_graphHolder.IsReady) return double.MaxValue;
            var g = _graphHolder.GetRequired();
            var a = g.GetNode(nodeA);
            var b = g.GetNode(nodeB);
            if (a is null || b is null) return double.MaxValue;
            var dx = Convert.ToDouble(a.Position.X) - Convert.ToDouble(b.Position.X);
            var dy = Convert.ToDouble(a.Position.Y) - Convert.ToDouble(b.Position.Y);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ----------------------------------------------------------------
        // Charging evaluation
        // ----------------------------------------------------------------

        private async Task EvaluateChargingAsync(
            CancellationToken stoppingToken)
        {
            if (!_graphHolder.IsReady) return;

            var vehicles = _registry.GetInService()
                .Where(v => v.IsOnline && !v.IsManual && !v.IsLoaded
                         && !v.CurrentMissionId.HasValue
                         && (v.CurrentNodeId ?? 0) > 0)
                // Neediest first: free chargers and park spots go to the
                // lowest battery before anyone else gets a look at them.
                .OrderBy(v => v.BatteryStateOfCharge)
                .ThenBy(v => v.VehicleId)
                .ToList();

            foreach (var vehicle in vehicles)
            {
                if (!_parkOrders.TryGetValue(vehicle.VehicleId, out var po))
                {
                    if (!vehicle.IsAvailableForDispatch) continue;
                    await AssignParkAsync(vehicle, stoppingToken);
                    continue;
                }

                // Moved off its park node some other way (restart elsewhere):
                // forget the order, it is re-evaluated as an idle vehicle.
                if (po.Arrived && vehicle.CurrentNodeId != po.NodeId)
                {
                    _parkOrders.TryRemove(vehicle.VehicleId, out _);
                    continue;
                }

                await ReviewParkAsync(vehicle, po, stoppingToken);
            }
        }

        // ----------------------------------------------------------------
        // Park / charge-park orders
        // ----------------------------------------------------------------

        private enum ParkKind { Park, Discretionary, Mandatory }

        /// <summary>
        /// A vehicle's park / charge-park order: where it is going (or
        /// sitting) and why. Arrived = reported the node. A Mandatory order
        /// is uncrushable.
        /// </summary>
        private sealed record ParkOrder(
            ParkKind Kind, int NodeId, string OrderId, bool Arrived);

        private int _lastPoolTried;
        private DateTime _lastNoCandLog = DateTime.MinValue;

        private bool IsChargerNode(int nodeId)
            => _pools.ChargerNodes.Contains(nodeId);

        /// <summary>Nodes another vehicle stands on, holds, or is bound for.</summary>
        private HashSet<int> OccupiedByOthers(int vehicleId)
        {
            var s = new HashSet<int>();
            foreach (var other in _registry.GetAll())
            {
                if (other.VehicleId == vehicleId) continue;
                if (other.CurrentNodeId.HasValue) s.Add(other.CurrentNodeId.Value);
                s.UnionWith(_traffic.GetVehicleLockedNodeIds(other.VehicleId));
                if (_parkOrders.TryGetValue(other.VehicleId, out var po))
                    s.Add(po.NodeId);
            }
            return s;
        }

        /// <summary>
        /// Nearest free node of the pool that has a route from the vehicle.
        /// The queue node is never "occupied": vehicles stack behind it.
        /// </summary>
        private async Task<(int NodeId, IReadOnlyList<int> Nodes,
                            IReadOnlyList<int>? Moves)?> PickFromPoolAsync(
            Vehicle v, IEnumerable<int> pool, bool ignoreOccupancy,
            CancellationToken ct)
        {
            var from = v.CurrentNodeId ?? 0;
            var occupied = ignoreOccupancy
                ? new HashSet<int>() : OccupiedByOthers(v.VehicleId);

            var ordered = pool
                .Where(n => ignoreOccupancy || !occupied.Contains(n))
                .OrderBy(n => DistanceBetween(from, n))
                .ToList();
            _lastPoolTried = ordered.Count;

            foreach (var node in ordered)
            {
                if (node == from)
                    return (node, new[] { from }, null);

                var route = await _routing.FindRouteAsync(
                    from, node, v.VehicleId,
                    BlockedNodesFor(v.VehicleId),
                    BlockedMovesFor(v.VehicleId), ct);
                if (route is null)
                {
                    await LogNoRouteAsync(
                        v, -node, node, BlockedNodesFor(v.VehicleId), ct);
                    continue;
                }
                return (node,
                    route.Nodes.Select(n => n.NodeId).ToList(),
                    route.MoveIds?.ToList());
            }
            return null;
        }

        /// <summary>
        /// Picks the park / charge location for the vehicle by SOC:
        ///   &lt;30%   mandatory: a free auto charger only.
        ///   30-75% discretionary: free charger; else (SOC &gt;= 50%) free
        ///          park-only, then the queue; below 50%: free park-only,
        ///          else stay where it is.
        ///   &gt;75%   park-only, then the queue.
        /// </summary>
        private async Task<(ParkKind Kind, int NodeId, IReadOnlyList<int> Nodes,
                            IReadOnlyList<int>? Moves)?> ChooseParkAsync(
            Vehicle v, CancellationToken ct)
        {
            var soc = v.BatteryStateOfCharge;

            if (soc < MandatoryBelowSoc)
            {
                var c = await PickFromPoolAsync(v, _pools.ChargerNodes, false, ct);
                return c is null ? null
                    : (ParkKind.Mandatory, c.Value.NodeId, c.Value.Nodes, c.Value.Moves);
            }

            if (soc <= ParkOnlyAboveSoc)
            {
                var c = await PickFromPoolAsync(v, _pools.ChargerNodes, false, ct);
                if (c is not null)
                    return (ParkKind.Discretionary, c.Value.NodeId, c.Value.Nodes, c.Value.Moves);
            }

            var p = await PickFromPoolAsync(v, _pools.ParkOnlyNodes, false, ct);
            if (p is not null)
                return (ParkKind.Park, p.Value.NodeId, p.Value.Nodes, p.Value.Moves);

            if (soc >= QueueFloorSoc && _pools.QueueNodeId.HasValue)
            {
                var q = await PickFromPoolAsync(
                    v, new[] { _pools.QueueNodeId.Value }, true, ct);
                if (q is not null)
                    return (ParkKind.Park, q.Value.NodeId, q.Value.Nodes, q.Value.Moves);
            }
            return null;
        }

        private async Task AssignParkAsync(Vehicle v, CancellationToken ct)
        {
            var choice = await ChooseParkAsync(v, ct);
            if (choice is null)
            {
                var occ = OccupiedByOthers(v.VehicleId);
                var busy = _pools.ParkOnlyNodes.Concat(_pools.ChargerNodes)
                    .Where(occ.Contains).ToList();
                var who = busy.Select(n => n + "(" + string.Join("/",
                    _registry.GetAll()
                        .Where(o => o.VehicleId != v.VehicleId
                            && (o.CurrentNodeId == n
                                || _traffic.GetVehicleLockedNodeIds(o.VehicleId).Contains(n)
                                || (_parkOrders.TryGetValue(o.VehicleId, out var opo) && opo.NodeId == n)))
                        .Select(o => "F" + o.VehicleId)) + ")");
                LogParkThrottled(v, "no free park/charge location - staying at node " +
                    v.CurrentNodeId + ". Occupied pool nodes: " + string.Join(" ", who));
                return;
            }
            await IssueParkOrderAsync(v, choice.Value, ct);
        }

        private async Task IssueParkOrderAsync(
            Vehicle v,
            (ParkKind Kind, int NodeId, IReadOnlyList<int> Nodes,
             IReadOnlyList<int>? Moves) c,
            CancellationToken ct)
        {
            var orderId = "PARK-" + Guid.NewGuid().ToString("N")[..8].ToUpper();

            // Already standing on the chosen node: just claim it.
            if (c.NodeId == v.CurrentNodeId || c.Nodes.Count < 2)
            {
                _parkOrders[v.VehicleId] = new ParkOrder(c.Kind, c.NodeId, orderId, true);
                _dispatchLogger.LogInformation(
                    "Vehicle {VehicleId} (SOC {Soc:F0}%) stays at node {Node} ({Kind})",
                    v.VehicleId, v.BatteryStateOfCharge, c.NodeId, c.Kind);
                await EnsureNodeHeldAsync(v, ct);
                return;
            }

            _parkOrders[v.VehicleId] = new ParkOrder(c.Kind, c.NodeId, orderId, false);
            v.UpdateState(ActivityState.TravelingEmpty, OrderState.Waiting);

            // Show the park itinerary in the vehicle detail / on the map.
            v.SetPlannedRoute(c.Nodes);
            await _channels.MissionUpdates.Writer.WriteAsync(
                new VehicleMissionUpdate(v.VehicleId, null, c.Nodes), ct);

            _dispatchLogger.LogInformation(
                "Vehicle {VehicleId} (SOC {Soc:F0}%) {From} → node {Node} ({Kind}, {Hops} hops)",
                v.VehicleId, v.BatteryStateOfCharge, v.CurrentNodeId,
                c.NodeId, c.Kind, c.Nodes.Count);

            if (await _windows.BeginAsync(
                    v.SerialNumber, v.VehicleId, orderId,
                    c.Nodes, c.Moves, ct))
                return;

            var order = new VehicleOrder
            {
                OrderId = orderId,
                OrderUpdateId = 1,
                Nodes = c.Nodes
                    .Select((nodeId, i) => new OrderNode
                    {
                        NodeId = nodeId.ToString(),
                        SequenceId = i,
                        Released = true,
                        X = 0m,
                        Y = 0m,
                        MapId = string.Empty,
                        Actions = Array.Empty<OrderAction>(),
                    })
                    .ToList(),
                Edges = Array.Empty<OrderEdge>().ToList(),
            };
            await _adapter.SendOrderAsync(v.SerialNumber, order, ct);
        }

        /// <summary>
        /// Re-evaluates a standing park order: a vehicle that dropped below
        /// 30% becomes mandatory; one below 75% parked on a non-charger moves
        /// to a charger as soon as one is free.
        /// </summary>
        private async Task ReviewParkAsync(
            Vehicle v, ParkOrder po, CancellationToken ct)
        {
            if (po.Kind == ParkKind.Mandatory) return;
            var soc = v.BatteryStateOfCharge;

            if (IsChargerNode(po.NodeId))
            {
                // On (or heading to) a charger: only the kind can change.
                if (soc < MandatoryBelowSoc)
                {
                    _parkOrders[v.VehicleId] = po with { Kind = ParkKind.Mandatory };
                    _dispatchLogger.LogInformation(
                        "Vehicle {VehicleId} SOC {Soc:F0}% - charge at node {Node} is now MANDATORY",
                        v.VehicleId, soc, po.NodeId);
                }
                return;
            }

            if (soc > ParkOnlyAboveSoc) return;

            var c = await PickFromPoolAsync(v, _pools.ChargerNodes, false, ct);
            if (c is null) return;

            var kind = soc < MandatoryBelowSoc
                ? ParkKind.Mandatory : ParkKind.Discretionary;
            await IssueParkOrderAsync(
                v, (kind, c.Value.NodeId, c.Value.Nodes, c.Value.Moves), ct);
        }

        /// <summary>
        /// A mission chose this vehicle: cancel its park / charge-park order.
        /// A charging vehicle retracts its contact first. The mission's
        /// order replaces the itinerary when it is delivered.
        /// </summary>
        private async Task CrushParkOrderAsync(
            Vehicle v, int missionId, CancellationToken ct)
        {
            if (!_parkOrders.TryRemove(v.VehicleId, out var po)) return;

            if (v.ActivityState == ActivityState.OpportunityCharging
                || v.ActivityState == ActivityState.MandatoryCharging)
                await SendActionAsync(v, "stopCharging", ct);

            _dispatchLogger.LogInformation(
                "Vehicle {VehicleId} {Kind} order to node {Node} crushed by mission {MissionId}",
                v.VehicleId, po.Kind, po.NodeId, missionId);
        }

        private void LogParkThrottled(Vehicle v, string text)
        {
            var now = DateTime.UtcNow;
            if (_parkLogAt.TryGetValue(v.VehicleId, out var last)
                && (now - last).TotalSeconds < 60) return;
            _parkLogAt[v.VehicleId] = now;
            _dispatchLogger.LogInformation(
                "Vehicle {VehicleId} (SOC {Soc:F0}%): {Text}",
                v.VehicleId, v.BatteryStateOfCharge, text);
        }

        // ----------------------------------------------------------------
        // Deadline escalation
        // ----------------------------------------------------------------

        private void EscalateApproachingDeadlines()
        {
            var approaching = _missionQueue
                .GetApproachingDeadline(TimeSpan.FromMinutes(5));

            foreach (var mission in approaching)
            {
                if (mission.Priority > MissionPriority.TimeCritical)
                {
                    _missionQueue.Cancel(mission.MissionId);
                    _missionQueue.Enqueue(
                        mission.WithEscalatedPriority(
                            MissionPriority.TimeCritical));

                    _dispatchLogger.LogWarning(
                        "Mission {MissionId} escalated to TimeCritical " +
                        "— pickup deadline approaching: {Deadline}",
                        mission.MissionId,
                        mission.PickupDeadline);
                }
            }
        }

        // ----------------------------------------------------------------
        // Fault handling
        // ----------------------------------------------------------------

        private async Task HandleVehicleFaultAsync(
            int vehicleId,
            CancellationToken stoppingToken)
        {
            var vehicle = _registry.GetById(vehicleId);
            if (vehicle?.CurrentMissionId is null) return;

            await _channels.MissionTransfers.Writer.WriteAsync(
                new MissionTransfer
                {
                    FromVehicleId = vehicleId,
                    ToVehicleId = null,
                    Reason = TransferReason.VehicleRemovedFromService,
                    ReturnToQueue = true,
                    EscalatedPriority = (int)MissionPriority.TimeCritical,
                    FaultedAtNodeId = vehicle.CurrentNodeId,
                    FaultedVehicleIsOnline = vehicle.IsOnline,
                    MissionContext = new MissionContext
                    {
                        MissionId = vehicle.CurrentMissionId.Value,
                        CurrentOrderId = string.Empty,
                        Priority = MissionPriority.Normal,
                    }
                }, stoppingToken);
        }
        private async Task ConsumeDispatchDecisionsAsync(
            CancellationToken stoppingToken)
        {
            await foreach (var decision in
                _channels.DispatchDecisions.Reader
                    .ReadAllAsync(stoppingToken))
            {
                // Sliding lock window: lock ahead and release the route
                // to the vehicle in stages. Returns false when windowing
                // is disabled (Traffic:LockWindowNodes = 0) or the route
                // cannot be windowed; the whole route is then sent at once.
                if (await _windows.BeginAsync(
                        decision.SerialNumber,
                        decision.VehicleId,
                        decision.OrderId,
                        decision.RouteNodeIds,
                        decision.RouteMoveIds,
                        stoppingToken))
                {
                    await ReleaseResumePauseAsync(
                        decision.VehicleId, stoppingToken);
                    continue;
                }

                var order = new VehicleOrder
                {
                    OrderId = decision.OrderId,
                    OrderUpdateId = 1,
                    Nodes = decision.RouteNodeIds
                        .Select((nodeId, i) => new OrderNode
                        {
                            NodeId = nodeId.ToString(),
                            SequenceId = i,
                            Released = true,
                            X = 0m,
                            Y = 0m,
                            MapId = string.Empty,
                            Actions = Array.Empty<OrderAction>(),
                        })
                        .ToList(),
                    Edges = Array.Empty<OrderEdge>().ToList(),
                };

                await _adapter.SendOrderAsync(
                    decision.SerialNumber, order, stoppingToken);

                await ReleaseResumePauseAsync(
                    decision.VehicleId, stoppingToken);
            }
        }

        /// <summary>
        /// A vehicle returned to service is held until the order that
        /// resumes its mission has been delivered, then released.
        /// </summary>
        private async Task ReleaseResumePauseAsync(
            int vehicleId, CancellationToken stoppingToken)
        {
            if (!_resumeAfterOrder.TryRemove(vehicleId, out _)) return;
            var vehicle = _registry.GetById(vehicleId);
            if (vehicle is not null)
                await SendPauseAsync(vehicle, false, stoppingToken);
        }
    }
}
