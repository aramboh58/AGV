using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;
using AGV.Core.Logging;
using Microsoft.Extensions.Logging;

namespace AGV.Vehicle.Services
{
    /// <summary>
    /// VDA 5050 compliant vehicle state machine.
    ///
    /// Manages valid ActivityState transitions for a single vehicle.
    /// Guards prevent invalid transitions — e.g. a vehicle cannot
    /// transition directly from Idle to Dropping without first Picking.
    ///
    /// Two orthogonal state axes per VDA 5050:
    ///   OperatingMode — how the vehicle is being controlled
    ///   OrderState    — lifecycle of the current order
    ///   ActivityState — what the vehicle is physically doing (site-specific)
    ///
    /// The state machine is used by both the simulation engine
    /// (driving simulated vehicles through state transitions) and
    /// the MQTT listener (validating real vehicle state transitions
    /// as reported in incoming State messages).
    ///
    /// All transitions are logged at Debug level — use AGV.Fleet.Vehicle
    /// domain for filtering.
    /// </summary>
    public sealed class VehicleStateMachine
    {
        private ActivityState _currentActivity;
        private OrderState _currentOrderState;
        private OperatingMode _currentOperatingMode;

        private readonly int _vehicleId;
        private readonly ILogger _logger;

        // ----------------------------------------------------------------
        // Valid transition table
        // Key: from state, Value: set of valid to states
        // ----------------------------------------------------------------
        private static readonly IReadOnlyDictionary<ActivityState,
            IReadOnlySet<ActivityState>> ValidTransitions =
            new Dictionary<ActivityState, IReadOnlySet<ActivityState>>
            {
                [ActivityState.Idle] = new HashSet<ActivityState>
                {
                    ActivityState.TravelingToPickup,
                    ActivityState.TravelingToMandatoryCharge,
                    ActivityState.TravelingToMaintenance,
                    ActivityState.QueuedForCharge,
                    ActivityState.OutOfService,
                },
                [ActivityState.TravelingToPickup] = new HashSet<ActivityState>
                {
                    ActivityState.ApproachingStand,
                    ActivityState.Idle,              // mission cancelled
                    ActivityState.TravelingToPickup, // reroute
                },
                [ActivityState.ApproachingStand] = new HashSet<ActivityState>
                {
                    ActivityState.Picking,
                    ActivityState.TravelingToPickup, // held by traffic
                    ActivityState.Idle,              // mission cancelled
                },
                [ActivityState.Picking] = new HashSet<ActivityState>
                {
                    ActivityState.TravelingLoaded,
                    ActivityState.Idle,              // pick failed
                },
                [ActivityState.TravelingLoaded] = new HashSet<ActivityState>
                {
                    ActivityState.ApproachingDrop,
                    ActivityState.TravelingLoaded,   // reroute
                    ActivityState.Idle,              // emergency
                },
                [ActivityState.ApproachingDrop] = new HashSet<ActivityState>
                {
                    ActivityState.Dropping,
                    ActivityState.TravelingLoaded,   // held by traffic
                },
                [ActivityState.Dropping] = new HashSet<ActivityState>
                {
                    ActivityState.Idle,
                    ActivityState.TravelingEmpty,
                },
                [ActivityState.TravelingEmpty] = new HashSet<ActivityState>
                {
                    ActivityState.Idle,
                    ActivityState.QueuedForCharge,
                    ActivityState.TravelingToPickup, // new mission
                },
                [ActivityState.QueuedForCharge] = new HashSet<ActivityState>
                {
                    ActivityState.OpportunityCharging,
                    ActivityState.Idle,              // slot released
                    ActivityState.TravelingToPickup, // urgent dispatch
                },
                [ActivityState.OpportunityCharging] = new HashSet<ActivityState>
                {
                    ActivityState.Idle,
                    ActivityState.TravelingToPickup, // urgent dispatch
                },
                [ActivityState.TravelingToMandatoryCharge] =
                    new HashSet<ActivityState>
                {
                    ActivityState.MandatoryCharging,
                    ActivityState.Idle,
                },
                [ActivityState.MandatoryCharging] = new HashSet<ActivityState>
                {
                    ActivityState.Idle,
                },
                [ActivityState.TravelingToMaintenance] =
                    new HashSet<ActivityState>
                {
                    ActivityState.MaintenanceDrain,
                    ActivityState.Idle,
                },
                [ActivityState.MaintenanceDrain] = new HashSet<ActivityState>
                {
                    ActivityState.MaintenanceCharge,
                },
                [ActivityState.MaintenanceCharge] = new HashSet<ActivityState>
                {
                    ActivityState.Idle,
                },
                [ActivityState.OutOfService] = new HashSet<ActivityState>
                {
                    ActivityState.Idle,              // returned to service
                },
            };

        public VehicleStateMachine(
            int vehicleId,
            ILoggerFactory loggerFactory,
            ActivityState initialActivity = ActivityState.Idle,
            OrderState initialOrderState = OrderState.Idle,
            OperatingMode initialMode = OperatingMode.Automatic)
        {
            _vehicleId = vehicleId;
            _currentActivity = initialActivity;
            _currentOrderState = initialOrderState;
            _currentOperatingMode = initialMode;
            _logger = loggerFactory.CreateLogger(LogDomains.Vehicle);
        }

        // ----------------------------------------------------------------
        // Current state
        // ----------------------------------------------------------------

        public ActivityState Activity => _currentActivity;
        public OrderState OrderState => _currentOrderState;
        public OperatingMode OperatingMode => _currentOperatingMode;

        // ----------------------------------------------------------------
        // Transition
        // ----------------------------------------------------------------

        /// <summary>
        /// Attempts to transition to the specified activity state.
        /// Returns true if the transition is valid and was applied.
        /// Returns false if the transition is invalid — caller should
        /// log a warning and investigate.
        /// </summary>
        public bool TryTransition(ActivityState toActivity)
        {
            if (!ValidTransitions.TryGetValue(_currentActivity,
                out var validNext))
            {
                _logger.LogWarning(
                    "Vehicle {VehicleId}: no transitions defined " +
                    "from state {From} — blocked",
                    _vehicleId, _currentActivity);
                return false;
            }

            if (!validNext.Contains(toActivity))
            {
                _logger.LogWarning(
                    "Vehicle {VehicleId}: invalid transition " +
                    "{From} → {To} — rejected",
                    _vehicleId, _currentActivity, toActivity);
                return false;
            }

            _logger.LogDebug(
                "Vehicle {VehicleId}: {From} → {To}",
                _vehicleId, _currentActivity, toActivity);

            _currentActivity = toActivity;
            return true;
        }

        /// <summary>
        /// Forces a transition regardless of validity.
        /// Used when reconciling state with an incoming VDA 5050
        /// State message from a real vehicle — the vehicle's reported
        /// state is authoritative, even if it skipped transitions.
        /// </summary>
        public void ForceTransition(ActivityState toActivity,
                                     string reason)
        {
            _logger.LogInformation(
                "Vehicle {VehicleId}: forced transition " +
                "{From} → {To} (reason: {Reason})",
                _vehicleId, _currentActivity, toActivity, reason);

            _currentActivity = toActivity;
        }

        /// <summary>
        /// Updates the VDA 5050 order state.
        /// </summary>
        public void SetOrderState(OrderState state)
        {
            if (_currentOrderState != state)
            {
                _logger.LogDebug(
                    "Vehicle {VehicleId}: order state " +
                    "{From} → {To}",
                    _vehicleId, _currentOrderState, state);
                _currentOrderState = state;
            }
        }

        /// <summary>
        /// Updates the VDA 5050 operating mode.
        /// </summary>
        public void SetOperatingMode(OperatingMode mode)
        {
            if (_currentOperatingMode != mode)
            {
                _logger.LogInformation(
                    "Vehicle {VehicleId}: operating mode " +
                    "{From} → {To}",
                    _vehicleId, _currentOperatingMode, mode);
                _currentOperatingMode = mode;
            }
        }

        // ----------------------------------------------------------------
        // Convenience state checks
        // ----------------------------------------------------------------

        /// <summary>True if vehicle is available for mission dispatch.</summary>
        public bool IsAvailableForDispatch
            => _currentActivity == ActivityState.Idle
            && _currentOrderState == OrderState.Idle
            && _currentOperatingMode == OperatingMode.Automatic;

        /// <summary>True if vehicle is actively traveling.</summary>
        public bool IsTraveling
            => _currentActivity is
                ActivityState.TravelingToPickup or
                ActivityState.TravelingLoaded or
                ActivityState.TravelingEmpty or
                ActivityState.ApproachingStand or
                ActivityState.ApproachingDrop or
                ActivityState.TravelingToMandatoryCharge or
                ActivityState.TravelingToMaintenance;

        /// <summary>True if vehicle is performing a fork operation.</summary>
        public bool IsForking
            => _currentActivity is
                ActivityState.Picking or
                ActivityState.Dropping;

        /// <summary>True if vehicle is in any charging state.</summary>
        public bool IsCharging
            => _currentActivity is
                ActivityState.OpportunityCharging or
                ActivityState.MandatoryCharging or
                ActivityState.MaintenanceCharge or
                ActivityState.MaintenanceDrain or
                ActivityState.QueuedForCharge;

        /// <summary>True if vehicle has a load.</summary>
        public bool IsLoaded
            => _currentActivity is
                ActivityState.TravelingLoaded or
                ActivityState.ApproachingDrop or
                ActivityState.Dropping;

        /// <summary>
        /// Returns true if the specified transition would be valid
        /// without actually applying it.
        /// Used by the simulation engine for planning.
        /// </summary>
        public bool CanTransitionTo(ActivityState toActivity)
        {
            return ValidTransitions.TryGetValue(_currentActivity,
                out var validNext)
                && validNext.Contains(toActivity);
        }

        public override string ToString()
            => $"Vehicle[{_vehicleId}] " +
               $"Activity={_currentActivity} " +
               $"Order={_currentOrderState} " +
               $"Mode={_currentOperatingMode}";
    }
}
