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
            ILoggerFactory loggerFactory)
        {
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

            // Start parallel consumers
            var stateTask = ConsumeVehicleStateUpdatesAsync(stoppingToken);
            var transferTask = ConsumeMissionTransfersAsync(stoppingToken);
            var swapTask = ConsumeMissionSwapsAsync(stoppingToken);
            var dispatchTask = RunDispatchLoopAsync(stoppingToken);

            var decisionTask = ConsumeDispatchDecisionsAsync(stoppingToken);

            await Task.WhenAll(stateTask, transferTask,
                               swapTask, dispatchTask, decisionTask);

            _logger.LogInformation("FleetManagerService stopped");
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
        private async Task TryDispatchPendingMissionsAsync(
            CancellationToken stoppingToken)
        {
            while (!_missionQueue.IsEmpty)
            {
                var mission = _missionQueue.Peek();
                if (mission is null) break;

                var candidates = SelectCandidateVehicles(mission);
                if (candidates.Count == 0) break;

                var blockedNodes = _traffic.GetLockedNodeIds();
                var blockedMoves = _traffic.GetLockedMoveIds();

                bool dispatched = false;
                foreach (var vehicle in candidates)
                {
                    var route = await _routing.FindRouteAsync(
                        vehicle.CurrentNodeId ?? 0,
                        mission.PickupNodeId,
                        vehicle.VehicleId,
                        blockedNodes,
                        blockedMoves,
                        stoppingToken);

                    if (route is null)
                    {
                        _dispatchLogger.LogWarning(
                            "No route found for vehicle {VehicleId} " +
                            "to mission {MissionId} pickup {NodeId} — trying next candidate",
                            vehicle.VehicleId,
                            mission.MissionId,
                            mission.PickupNodeId);
                        continue;
                    }

                    // Route found — dequeue and dispatch
                    _missionQueue.Dequeue();

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
                _traffic.GetLockedNodeIds(),
                _traffic.GetLockedMoveIds(),
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
                _traffic.GetLockedNodeIds(),
                _traffic.GetLockedMoveIds(),
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

        private List<Vehicle> SelectCandidateVehicles(MissionContext mission)
        {
            var candidates = _registry.GetAvailableForDispatch()
                .Where(v => v.BatteryStateOfCharge >
                    _charging.GetThresholds().MandatoryEnterSoc)
                .ToList();

            _logger.LogInformation(
                "SelectCandidateVehicles: {Total} registered, {Available} available for dispatch, {Candidates} above SOC threshold",
                _registry.Count,
                _registry.GetAvailableForDispatch().Count,
                candidates.Count);

            return candidates
                .OrderByDescending(v => v.BatteryStateOfCharge)
                .ToList();
        }

        // ----------------------------------------------------------------
        // Charging evaluation
        // ----------------------------------------------------------------

        private async Task EvaluateChargingAsync(
            CancellationToken stoppingToken)
        {
            var idleVehicles = _registry.GetInService()
                .Where(v => v.IsOnline
                         && v.OrderState == OrderState.Idle
                         && !_charging.IsVehicleCharging(v.VehicleId))
                .ToList();

            foreach (var vehicle in idleVehicles)
            {
                var assignment = await _charging.EvaluateChargingNeedAsync(
                    vehicle.VehicleId,
                    vehicle.BatteryStateOfCharge,
                    vehicle.CurrentNodeId ?? 0,
                    stoppingToken);

                if (assignment is not null)
                {
                    await _channels.ChargeAssignments.Writer
                        .WriteAsync(assignment, stoppingToken);
                }
            }
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
