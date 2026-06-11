using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        public FleetManagerService(
            VehicleRegistry registry,
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
                    _enqueuedMissions, _dispatchedMissions, _completedMissions),
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

        public Task ReturnVehicleToServiceAsync(
            int vehicleId,
            CancellationToken cancellationToken = default)
        {
            var vehicle = _registry.GetById(vehicleId);
            if (vehicle is null) return Task.CompletedTask;

            vehicle.ReturnToService();
            _logger.LogInformation(
                "Vehicle {VehicleId} returned to service",
                vehicleId);
            return Task.CompletedTask;
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

                var vehicle = SelectBestVehicle(mission);
                if (vehicle is null)
                {
                    // No vehicle available — stop trying for now
                    break;
                }

                // Dequeue now that we have a vehicle
                _missionQueue.Dequeue();

                // Plan route
                var blockedNodes = _traffic.GetLockedNodeIds();
                var blockedMoves = _traffic.GetLockedMoveIds();

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
                        "to mission {MissionId} pickup {NodeId} — " +
                        "re-queuing",
                        vehicle.VehicleId,
                        mission.MissionId,
                        mission.PickupNodeId);
                    _missionQueue.Enqueue(mission);
                    break;
                }

                // Assign mission to vehicle
                vehicle.AssignMission(mission.MissionId);
                Interlocked.Increment(ref _dispatchedMissions);
                await _channels.MissionCounters.Writer.WriteAsync(
                    new MissionCounterUpdate(
                        _enqueuedMissions, _dispatchedMissions, _completedMissions),
                    stoppingToken);
                vehicle.UpdateState(
                    ActivityState.TravelingToPickup,
                    OrderState.Waiting);

                var dispatched = mission with
                {
                    CurrentVehicleId = vehicle.VehicleId,
                    CurrentOrderId = Guid.NewGuid()
                        .ToString("N")[..8].ToUpper()
                };

                // Publish dispatch decision
                var decision = new MissionDispatchDecision
                {
                    MissionId = mission.MissionId,
                    OrderId = dispatched.CurrentOrderId,
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

                _dispatchLogger.LogInformation(
                    "Dispatched vehicle {VehicleId} → " +
                    "mission {MissionId} " +
                    "(ETA {ETA:F0}s, route {Hops} hops)",
                    vehicle.VehicleId,
                    mission.MissionId,
                    route.EstimatedTravelTimeSeconds,
                    route.Nodes.Count);
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
                vehicle.UpdatePosition(update.LastNodeId,
                    vehicle.CurrentMapId);

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
                Interlocked.Increment(ref _completedMissions);
                _dispatchLogger.LogInformation(
                    "Vehicle {VehicleId} completed mission — total completed: {Total}",
                    vehicle.VehicleId, _completedMissions);
                await _channels.MissionCounters.Writer.WriteAsync(
                    new MissionCounterUpdate(
                        _enqueuedMissions, _dispatchedMissions, _completedMissions),
                    stoppingToken);
            }

            await Task.CompletedTask;
        }

        // ----------------------------------------------------------------
        // Mission transfer processing
        // ----------------------------------------------------------------

        private async Task ProcessMissionTransferAsync(
            MissionTransfer transfer,
            CancellationToken stoppingToken)
        {
            // CRITICAL: release all locks FIRST
            await _traffic.ReleaseAllLocksAsync(
                transfer.FromVehicleId, stoppingToken);

            var fromVehicle = _registry.GetById(transfer.FromVehicleId);
            fromVehicle?.ClearMission();

            var context = transfer.MissionContext;

            if (transfer.ReturnToQueue || !transfer.HasReceivingVehicle)
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

        private Vehicle? SelectBestVehicle(MissionContext mission)
        {
            var candidates = _registry.GetAvailableForDispatch()
                .Where(v => !v.BatteryStateOfCharge
                    .Equals(_charging.GetThresholds().MandatoryEnterSoc)
                    && v.BatteryStateOfCharge >
                    _charging.GetThresholds().MandatoryEnterSoc)
                .ToList();

            _logger.LogInformation(
                "SelectBestVehicle: {Total} registered, {Available} available for dispatch, {Candidates} above SOC threshold",
                _registry.Count,
                _registry.GetAvailableForDispatch().Count,
                candidates.Count);

            if (candidates.Count == 0) return null;

            // Select closest vehicle by current node proximity
            // Full travel-time based selection in Phase 2 order stealing
            var pickupNodeId = mission.PickupNodeId;
            return candidates
                .OrderByDescending(v => v.BatteryStateOfCharge)
                .FirstOrDefault();
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
            }
        }
    }
}
