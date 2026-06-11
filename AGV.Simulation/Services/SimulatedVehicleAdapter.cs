using AGV.Core.Entities;
using AGV.Core.Enums;
using AGV.Core.Interfaces;
using AGV.Core.Logging;
using AGV.Core.Messages;
using AGV.Fleet.Infrastructure;
using AGV.Topology.Services;
using AGV.Vehicle.Services;
using Microsoft.Extensions.Logging;

// Type aliases to avoid namespace collision with AGV.Simulation project name
using VehicleEntity = AGV.Core.Entities.Vehicle;
using VehicleFSEntity = AGV.Core.Entities.VehicleFactSheet;

namespace AGV.Simulation.Services
{
    /// <summary>
    /// IVehicleAdapter implementation for simulated vehicles.
    ///
    /// Drives simulated AGVs through the VehicleStateMachine using
    /// the BatteryModel for realistic SOC behavior. Publishes state
    /// updates to the ChannelRegistry exactly as the real MQTT adapter
    /// would — the fleet manager cannot tell the difference.
    ///
    /// This is the component that makes the NYT demo possible without
    /// real hardware. The host control system runs identically whether
    /// talking to simulated or real vehicles.
    ///
    /// Switching between simulation and real vehicles:
    ///   appsettings.json: "VehicleInterface": "Simulation"
    ///   → AGV.Host registers SimulatedVehicleAdapter as IVehicleAdapter
    ///   appsettings.json: "VehicleInterface": "Mqtt"
    ///   → AGV.Host registers MqttVehicleAdapter as IVehicleAdapter
    /// </summary>
    public sealed class SimulatedVehicleAdapter : IVehicleAdapter
    {
        private readonly VehicleRegistry _registry;
        private readonly ChannelRegistry _channels;
        private readonly RoadMapGraphHolder _roadMapHolder;
        private RoadMapGraph RoadMap => _roadMapHolder.GetRequired(); private readonly ILoggerFactory _loggerFactory;
        private readonly BatteryModelOptions _batteryOptions;
        private readonly ILogger _logger;

        // Per-vehicle simulation state
        private readonly Dictionary<int, SimulatedVehicleState>
            _simStates = new();

        // Registered callbacks
        private Func<VehicleStateUpdate,
            CancellationToken, Task>? _onStateReceived;
        private Func<VehiclePositionUpdate,
            CancellationToken, Task>? _onVisualizationReceived;
        private Func<VehicleConnectionEvent,
            CancellationToken, Task>? _onConnectionChanged;
        private Func<VehicleFactSheetEvent,
            CancellationToken, Task>? _onFactSheetReceived;

        public SimulatedVehicleAdapter(
            VehicleRegistry registry,
            ChannelRegistry channels,
            RoadMapGraphHolder roadMapHolder,
            BatteryModelOptions batteryOptions,
            ILoggerFactory loggerFactory)
        {
            _registry = registry;
            _channels = channels;
            _roadMapHolder = roadMapHolder;
            _batteryOptions = batteryOptions;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger(LogDomains.Fleet);
        }

        // ----------------------------------------------------------------
        // IVehicleAdapter — outbound commands
        // (simulation receives orders but executes them internally)
        // ----------------------------------------------------------------

        public Task SendOrderAsync(
            string serialNumber,
            VehicleOrder order,
            CancellationToken cancellationToken = default)
        {
            var state = GetSimStateBySerial(serialNumber);
            if (state is null) return Task.CompletedTask;

            // Store the order for the simulation engine to execute
            state.PendingOrder = order;
            state.CurrentOrderId = order.OrderId;

            state.NodeIndex = 0;
            state.TravelProgress = 0;
            state.StateMachine.TryTransition(ActivityState.TravelingToPickup);
            state.StateMachine.SetOrderState(OrderState.Waiting);

            _logger.LogDebug(
                "Simulated vehicle {Serial} received order {OrderId} " +
                "({NodeCount} nodes)",
                serialNumber, order.OrderId, order.Nodes.Count);

            return Task.CompletedTask;
        }

        public Task SendInstantActionAsync(
            string serialNumber,
            VehicleInstantAction instantAction,
            CancellationToken cancellationToken = default)
        {
            var state = GetSimStateBySerial(serialNumber);
            if (state is null) return Task.CompletedTask;

            foreach (var action in instantAction.InstantActions)
            {
                switch (action.ActionType.ToLowerInvariant())
                {
                    case "cancelorder":
                        state.PendingOrder = null;
                        state.CurrentOrderId = string.Empty;
                        state.StateMachine.ForceTransition(
                            ActivityState.Idle, "cancelOrder received");
                        break;

                    case "waitfortrigger":
                        state.IsWaitingForTrigger = true;
                        state.TriggerId = action.Parameters
                            .FirstOrDefault(p => p.Key == "triggerId")
                            ?.Value ?? string.Empty;
                        break;

                    case "triggerrelease":
                        state.IsWaitingForTrigger = false;
                        state.TriggerId = string.Empty;
                        break;

                    case "startcharging":
                        state.StateMachine.TryTransition(
                            ActivityState.OpportunityCharging);
                        break;

                    case "stopcharging":
                        state.StateMachine.TryTransition(
                            ActivityState.Idle);
                        break;
                }
            }

            return Task.CompletedTask;
        }

        // ----------------------------------------------------------------
        // IVehicleAdapter — callback registration
        // ----------------------------------------------------------------

        public void OnStateReceived(
            Func<VehicleStateUpdate, CancellationToken, Task> handler)
            => _onStateReceived = handler;

        public void OnVisualizationReceived(
            Func<VehiclePositionUpdate, CancellationToken, Task> handler)
            => _onVisualizationReceived = handler;

        public void OnConnectionChanged(
            Func<VehicleConnectionEvent, CancellationToken, Task> handler)
            => _onConnectionChanged = handler;

        public void OnFactSheetReceived(
            Func<VehicleFactSheetEvent, CancellationToken, Task> handler)
            => _onFactSheetReceived = handler;

        public bool IsVehicleOnline(string serialNumber)
        {
            var state = GetSimStateBySerial(serialNumber);
            return state?.IsOnline ?? false;
        }

        // ----------------------------------------------------------------
        // IVehicleAdapter — lifecycle
        // ----------------------------------------------------------------

        public async Task StartAsync(
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "SimulatedVehicleAdapter starting — " +
                "initializing {Count} simulated vehicles",
                _registry.Count);

            // Initialize simulation state for each registered vehicle
            foreach (var vehicle in _registry.GetAll())
            {
                var simState = new SimulatedVehicleState(
                    vehicle,
                    new VehicleStateMachine(
                        vehicle.VehicleId, _loggerFactory),
                    new BatteryModel(
                        _batteryOptions,
                        vehicle.BatteryStateOfCharge / 100m));

                _simStates[vehicle.VehicleId] = simState;

                // Publish initial connection event
                var connEvent = VehicleStateFactory
                    .BuildConnectionEvent(vehicle, isOnline: true);

                simState.IsOnline = true;
                vehicle.SetOnline();

                await _channels.VehicleStateUpdates.Writer
                    .WriteAsync(new VehicleStateUpdate
                    {
                        VehicleId = vehicle.VehicleId,
                        SerialNumber = vehicle.SerialNumber,
                        ActivityState = ActivityState.Idle,
                        OrderState = OrderState.Idle,
                        OperatingMode = OperatingMode.Automatic,
                        BatteryStateOfCharge =
                            vehicle.BatteryStateOfCharge,
                        ReceivedAt = DateTime.UtcNow,
                    }, cancellationToken);

                // Publish fact sheet
                if (_onFactSheetReceived is not null)
                {
                    var factSheet = BuildDefaultFactSheet(vehicle);
                    var factSheetEvent = VehicleStateFactory
                        .BuildFactSheetEvent(vehicle, factSheet);
                    await _onFactSheetReceived(
                        factSheetEvent, cancellationToken);
                }
            }

            _logger.LogInformation(
                "SimulatedVehicleAdapter started — " +
                "{Count} vehicles online",
                _simStates.Count);
        }

        public Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "SimulatedVehicleAdapter stopping");
            return Task.CompletedTask;
        }

        // ----------------------------------------------------------------
        // Simulation tick — called by SimulationEngineService
        // ----------------------------------------------------------------

        /// <summary>
        /// Advances all simulated vehicles by one tick.
        /// Called by SimulationEngineService on each simulation step.
        /// </summary>
        public async Task TickAsync(
            decimal elapsedSeconds,
            CancellationToken cancellationToken)
        {
            foreach (var (vehicleId, state) in _simStates)
            {
                if (!state.IsOnline) continue;

                await TickVehicleAsync(
                    state, elapsedSeconds, cancellationToken);
            }
        }

        // ----------------------------------------------------------------
        // Per-vehicle tick logic
        // ----------------------------------------------------------------

        private async Task TickVehicleAsync(
            SimulatedVehicleState state,
            decimal elapsedSeconds,
            CancellationToken cancellationToken)
        {
            var activity = state.StateMachine.Activity;

            // Discharge battery based on current activity
            var dischargeActivity = activity switch
            {
                ActivityState.TravelingToPickup =>
                    DischargeActivity.Traveling,
                ActivityState.TravelingLoaded =>
                    DischargeActivity.Traveling,
                ActivityState.TravelingEmpty =>
                    DischargeActivity.Traveling,
                ActivityState.ApproachingStand =>
                    DischargeActivity.Traveling,
                ActivityState.ApproachingDrop =>
                    DischargeActivity.Traveling,
                ActivityState.Picking =>
                    DischargeActivity.Forking,
                ActivityState.Dropping =>
                    DischargeActivity.Forking,
                ActivityState.OpportunityCharging =>
                    DischargeActivity.Charging,
                ActivityState.MandatoryCharging =>
                    DischargeActivity.Charging,
                _ =>
                    DischargeActivity.Idle,
            };

            if (dischargeActivity != DischargeActivity.Charging)
                state.Battery.Discharge(
                    dischargeActivity,
                    state.StateMachine.IsLoaded,
                    elapsedSeconds);

            // Apply charging if in charge state
            if (activity == ActivityState.OpportunityCharging)
                state.Battery.ChargeOpportunity(elapsedSeconds);
            else if (activity == ActivityState.MandatoryCharging)
                state.Battery.ChargeMandatory(elapsedSeconds);
            else if (activity == ActivityState.MaintenanceDrain)
                state.Battery.DrainForMaintenance(elapsedSeconds);
            else if (activity == ActivityState.MaintenanceCharge)
                state.Battery.ChargeAfterMaintenance(elapsedSeconds);

            // Advance travel if vehicle is moving
            if (state.StateMachine.IsTraveling
                && state.PendingOrder is not null
                && !state.IsWaitingForTrigger)
            {
                await AdvanceTravelAsync(
                    state, elapsedSeconds, cancellationToken);
            }

            // Advance fork operations
            if (state.StateMachine.IsForking)
            {
                await AdvanceForkingAsync(
                    state, elapsedSeconds, cancellationToken);
            }

            // Publish state update
            await PublishStateUpdateAsync(state, cancellationToken);
        }

        private async Task AdvanceTravelAsync(
            SimulatedVehicleState state,
            decimal elapsedSeconds,
            CancellationToken cancellationToken)
        {
            var order = state.PendingOrder;
            if (order is null) return;

            // Find current position in order node list
            var releasedNodes = order.Nodes
                .Where(n => n.Released)
                .ToList();

            if (state.NodeIndex >= releasedNodes.Count - 1)
            {
                // Reached end of released base — arrived at destination
                var lastNode = releasedNodes.LastOrDefault();
                if (lastNode is not null)
                {
                    if (int.TryParse(lastNode.NodeId,
                        out var nodeId))
                    {
                        state.CurrentNodeId = nodeId;
                        state.Vehicle.UpdatePosition(
                            nodeId, state.Vehicle.CurrentMapId);
                    }
                }

                // Transition based on current activity
                var currentActivity = state.StateMachine.Activity;
                if (currentActivity == ActivityState.TravelingToPickup
                    || currentActivity == ActivityState.ApproachingStand)
                {
                    // TravelingToPickup must pass through ApproachingStand first
                    if (currentActivity == ActivityState.TravelingToPickup)
                        state.StateMachine.TryTransition(ActivityState.ApproachingStand);

                    state.StateMachine.TryTransition(
                        ActivityState.Picking);
                    state.ForkOperationTimer = 0m;
                }
                else if (currentActivity ==
                    ActivityState.TravelingLoaded
                    || currentActivity ==
                    ActivityState.ApproachingDrop)
                {
                    if (currentActivity == ActivityState.TravelingLoaded)
                        state.StateMachine.TryTransition(ActivityState.ApproachingDrop);

                    state.StateMachine.TryTransition(
                        ActivityState.Dropping);
                    state.StateMachine.SetOrderState(OrderState.Running);
                    state.ForkOperationTimer = 0m;
                }
                else
                {
                    state.StateMachine.SetOrderState(OrderState.Finished);
                    state.StateMachine.TryTransition(ActivityState.Idle);
                    await PublishStateUpdateAsync(state, cancellationToken);
                    state.StateMachine.SetOrderState(OrderState.Idle);
                    state.PendingOrder = null;
                    state.NodeIndex = 0;
                }
                return;
            }

            // Advance along route
            state.TravelProgress += (double)elapsedSeconds
                * SimulationConstants.DefaultSpeedCmPerSec;

            // Check if we've reached the next node
            var currentNode = releasedNodes[state.NodeIndex];
            var nextNode = releasedNodes[state.NodeIndex + 1];

            if (int.TryParse(currentNode.NodeId, out var fromId)
                && int.TryParse(nextNode.NodeId, out var toId))
            {
                var move = RoadMap.GetMove(fromId, toId);
                var segmentLengthCm = move is not null
                    ? (double)move.Clothoid.ArcLength
                    : SimulationConstants.DefaultSegmentLengthCm;

                if (state.TravelProgress >= segmentLengthCm)
                {
                    state.TravelProgress -= segmentLengthCm;
                    state.NodeIndex++;
                    state.CurrentNodeId = toId;
                    state.Vehicle.UpdatePosition(
                        toId, state.Vehicle.CurrentMapId);

                    // Update interpolated position
                    var node = RoadMap.GetNode(toId);
                    if (node is not null)
                    {
                        state.CurrentX = node.Position.X;
                        state.CurrentY = node.Position.Y;
                    }
                }
                else
                {
                    // Interpolate position between nodes
                    var fromNode = RoadMap.GetNode(fromId);
                    var toNode = RoadMap.GetNode(toId);
                    if (fromNode is not null && toNode is not null)
                    {
                        var pct = (decimal)(state.TravelProgress
                            / segmentLengthCm);
                        state.CurrentX = fromNode.Position.X
                            + (toNode.Position.X
                               - fromNode.Position.X) * pct;
                        state.CurrentY = fromNode.Position.Y
                            + (toNode.Position.Y
                               - fromNode.Position.Y) * pct;
                    }
                }
            }

            await Task.CompletedTask;
        }

        private async Task AdvanceForkingAsync(
            SimulatedVehicleState state,
            decimal elapsedSeconds,
            CancellationToken cancellationToken)
        {
            state.ForkOperationTimer += elapsedSeconds;

            var duration = state.StateMachine.Activity ==
                ActivityState.Picking
                ? SimulationConstants.PickDurationSeconds
                : SimulationConstants.DropDurationSeconds;

            if (state.ForkOperationTimer >= duration)
            {
                state.ForkOperationTimer = 0m;

                if (state.StateMachine.Activity == ActivityState.Picking)
                {
                    state.StateMachine.TryTransition(
                        ActivityState.TravelingLoaded);
                    state.NodeIndex = 0;
                }
                else // Dropping
                {
                    state.StateMachine.SetOrderState(OrderState.Finished);
                    state.StateMachine.TryTransition(ActivityState.Idle);
                    // Publish the Finished state before resetting to Idle
                    await PublishStateUpdateAsync(state, cancellationToken);
                    state.StateMachine.SetOrderState(OrderState.Idle);
                    state.PendingOrder = null;
                    state.CurrentOrderId = string.Empty;
                    state.NodeIndex = 0;

                }
            }

            await Task.CompletedTask;
        }

        private async Task PublishStateUpdateAsync(
            SimulatedVehicleState state,
            CancellationToken cancellationToken)
        {
            var update = new VehicleStateUpdate
            {
                VehicleId = state.Vehicle.VehicleId,
                SerialNumber = state.Vehicle.SerialNumber,
                ActivityState = state.StateMachine.Activity,
                OrderState = state.StateMachine.OrderState,
                OperatingMode = state.StateMachine.OperatingMode,
                BatteryStateOfCharge = state.Battery.StateOfChargePercent,
                IsCharging = state.Battery.IsCharging,
                IsLoaded = state.StateMachine.IsLoaded,
                CurrentOrderId = state.CurrentOrderId,
                LastNodeId = state.CurrentNodeId,
                OrderUpdateId = 0,
                Errors = Array.Empty<string>(),
                ReceivedAt = DateTime.UtcNow,
            };

            await _channels.VehicleStateUpdates.Writer
                .WriteAsync(update, cancellationToken);
            await _channels.DashboardStateUpdates.Writer
                .WriteAsync(update, cancellationToken);

            // Publish position update for dashboard
            var posUpdate = new VehiclePositionUpdate
            {
                VehicleId = state.Vehicle.VehicleId,
                SerialNumber = state.Vehicle.SerialNumber,
                NodeId = state.CurrentNodeId,
                MapId = state.Vehicle.CurrentMapId,
                X = state.CurrentX,
                Y = state.CurrentY,
                ReceivedAt = DateTime.UtcNow,
            };

            await _channels.VehiclePositionUpdates.Writer
                .WriteAsync(posUpdate, cancellationToken);
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private SimulatedVehicleState? GetSimStateBySerial(
            string serialNumber)
        {
            var vehicle = _registry.GetBySerialNumber(serialNumber);
            if (vehicle is null) return null;
            return _simStates.TryGetValue(vehicle.VehicleId,
                out var state) ? state : null;
        }

        private static VehicleFactSheet BuildDefaultFactSheet(
            VehicleEntity vehicle)
            => new(
                vehicleId: vehicle.VehicleId,
                protocolVersion: "2.0.0",
                maxOrderHorizonDepth: 10,
                supportsNurbsTrajectory: false,
                supportedActionTypes:
                    "pick,drop,startCharging,stopCharging," +
                    "waitForTrigger,triggerRelease,cancelOrder",
                maxSpeedMs: 1.5m,
                maxPayloadKg: 1500m,
                lengthMeters: 2.5m,
                widthMeters: 1.2m);
    }

    // ----------------------------------------------------------------
    // Simulation state per vehicle
    // ----------------------------------------------------------------

    internal sealed class SimulatedVehicleState
    {
        public VehicleEntity Vehicle { get; }
        public VehicleStateMachine StateMachine { get; }
        public BatteryModel Battery { get; }

        public bool IsOnline { get; set; }
        public int CurrentNodeId { get; set; }
        public decimal CurrentX { get; set; }
        public decimal CurrentY { get; set; }
        public string CurrentOrderId { get; set; } = string.Empty;

        // Travel state
        public VehicleOrder? PendingOrder { get; set; }
        public int NodeIndex { get; set; }
        public double TravelProgress { get; set; }  // cm

        // Fork operation timing
        public decimal ForkOperationTimer { get; set; }  // seconds

        // waitForTrigger state
        public bool IsWaitingForTrigger { get; set; }
        public string TriggerId { get; set; } = string.Empty;

        public SimulatedVehicleState(
            VehicleEntity vehicle,
            VehicleStateMachine stateMachine,
            BatteryModel battery)
        {
            Vehicle = vehicle;
            StateMachine = stateMachine;
            Battery = battery;
            CurrentNodeId = vehicle.CurrentNodeId ?? 0;
        }
    }

    // ----------------------------------------------------------------
    // Simulation constants
    // ----------------------------------------------------------------

    internal static class SimulationConstants
    {
        public const double DefaultSpeedCmPerSec = 120.0; // 1.2 m/s
        public const double DefaultSegmentLengthCm = 500.0; // 5m default
        public const decimal PickDurationSeconds = 45m;
        public const decimal DropDurationSeconds = 35m;
    }
}
