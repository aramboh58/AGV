using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;

namespace AGV.Core.Entities
{
    /// <summary>
    /// Represents a physical AGV in the fleet.
    ///
    /// This is the host-side vehicle record — it tracks identity,
    /// configuration, and current operational state. It is NOT the
    /// VDA 5050 state message (that is a separate DTO in AGV.Mqtt).
    ///
    /// Key design points:
    ///   — A vehicle belongs to exactly one Zone at any time.
    ///     Zone changes are runtime operations managed by the fleet manager.
    ///   — CurrentNodeId tracks the last known confirmed node position.
    ///     Interpolated positions during travel are tracked separately
    ///     in the visualization layer.
    ///   — BatteryStateOfCharge is updated continuously from incoming
    ///     VDA 5050 State messages.
    ///   — The VehicleFactSheet (buffer depth, capabilities) is stored
    ///     separately and populated when the vehicle first connects.
    /// </summary>
    public class Vehicle
    {
        /// <summary>
        /// Primary key — stable identity of this vehicle.
        /// Used in all host-side references and VDA 5050 order routing.
        /// </summary>
        public int VehicleId { get; private set; }

        /// <summary>
        /// Human-readable name or label for this vehicle.
        /// Examples: "F01", "W03", "Fork-07"
        /// </summary>
        public string VehicleName { get; private set; }

        /// <summary>
        /// The VDA 5050 serial number transmitted in all MQTT topic paths
        /// and message headers for this vehicle.
        /// Format: manufacturer-defined, e.g. "SN-F01"
        /// </summary>
        public string SerialNumber { get; private set; }

        /// <summary>
        /// Physical vehicle classification.
        /// Determines which RoutingTypes and moves this vehicle
        /// may traverse.
        /// </summary>
        public VehicleType VehicleType { get; private set; }

        /// <summary>
        /// The zone this vehicle is currently assigned to.
        /// Null means unassigned — fleet manager will assign on next
        /// dispatch cycle.
        /// </summary>
        public int? CurrentZoneId { get; private set; }

        /// <summary>
        /// The logical NodeId of the vehicle's last confirmed position.
        /// Updated from VDA 5050 State message lastNodeId field.
        /// Null if the vehicle has not yet reported a position.
        /// </summary>
        public int? CurrentNodeId { get; private set; }

        /// <summary>
        /// The map identifier of the floor/coordinate space the vehicle
        /// is currently on.
        /// For single-floor facilities this never changes.
        /// </summary>
        public string CurrentMapId { get; private set; }

        /// <summary>
        /// Current battery state of charge as a percentage (0.0 to 100.0).
        /// Updated from VDA 5050 State message batteryState.batteryCharge.
        /// </summary>
        public decimal BatteryStateOfCharge { get; private set; }

        /// <summary>
        /// Current activity state of this vehicle.
        /// Updated by the fleet manager as missions progress.
        /// </summary>
        public ActivityState ActivityState { get; private set; }

        /// <summary>
        /// Current VDA 5050 order state.
        /// </summary>
        public OrderState OrderState { get; private set; }

        /// <summary>
        /// Current VDA 5050 operating mode.
        /// </summary>
        public OperatingMode OperatingMode { get; private set; }

        /// <summary>
        /// True if this vehicle is currently carrying a load.
        /// </summary>
        public bool IsLoaded { get; private set; }

        /// <summary>
        /// True if this vehicle is in service and available for dispatch.
        /// False when out for maintenance, mandatory charge cycle, or
        /// manually taken out of service.
        /// </summary>
        public bool IsInService { get; private set; }

        /// <summary>
        /// True if the vehicle is currently connected to the MQTT broker
        /// and publishing state messages.
        /// Updated by the connection state tracker in AGV.Mqtt.
        /// </summary>
        public bool IsOnline { get; private set; }

        /// <summary>
        /// The ID of the mission currently assigned to this vehicle.
        /// Null when idle.
        /// </summary>
        public int? CurrentMissionId { get; private set; }

        /// <summary>
        /// Timestamp of the last State message received from this vehicle.
        /// </summary>
        public DateTime? LastStateReceivedAt { get; private set; }

        /// <summary>
        /// Timestamp when this vehicle record was created.
        /// </summary>
        public DateTime CreatedAt { get; private set; }

        // Private constructor for EF Core
        private Vehicle()
        {
            VehicleName = null!;
            SerialNumber = null!;
            CurrentMapId = null!;
        }

        public Vehicle(
            int vehicleId,
            string vehicleName,
            string serialNumber,
            VehicleType vehicleType,
            string initialMapId,
            int? initialZoneId = null)
        {
            if (vehicleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(vehicleId),
                    "VehicleId must be a positive integer.");

            if (string.IsNullOrWhiteSpace(vehicleName))
                throw new ArgumentException(
                    "VehicleName cannot be null or empty.",
                    nameof(vehicleName));

            if (string.IsNullOrWhiteSpace(serialNumber))
                throw new ArgumentException(
                    "SerialNumber cannot be null or empty.",
                    nameof(serialNumber));

            if (string.IsNullOrWhiteSpace(initialMapId))
                throw new ArgumentException(
                    "InitialMapId cannot be null or empty.",
                    nameof(initialMapId));

            VehicleId = vehicleId;
            VehicleName = vehicleName;
            SerialNumber = serialNumber;
            VehicleType = vehicleType;
            CurrentMapId = initialMapId;
            CurrentZoneId = initialZoneId;
            BatteryStateOfCharge = 0m;
            ActivityState = ActivityState.Idle;
            OrderState = OrderState.Idle;
            OperatingMode = OperatingMode.Automatic;
            IsLoaded = false;
            IsInService = true;
            IsOnline = false;
            CreatedAt = DateTime.UtcNow;
        }

        // ----------------------------------------------------------------
        // State update methods — called by fleet manager and MQTT listener
        // ----------------------------------------------------------------

        /// <summary>
        /// Updates position from an incoming VDA 5050 State message.
        /// </summary>
        public void UpdatePosition(int nodeId, string mapId)
        {
            if (string.IsNullOrWhiteSpace(mapId))
                throw new ArgumentException(
                    "MapId cannot be null or empty.", nameof(mapId));
            CurrentNodeId = nodeId;
            CurrentMapId = mapId;
        }

        /// <summary>
        /// Updates battery SOC from an incoming VDA 5050 State message.
        /// </summary>
        public void UpdateBattery(decimal stateOfCharge)
        {
            if (stateOfCharge < 0m || stateOfCharge > 100m)
                throw new ArgumentOutOfRangeException(
                    nameof(stateOfCharge),
                    "Battery state of charge must be between 0 and 100.");
            BatteryStateOfCharge = stateOfCharge;
            LastStateReceivedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Updates the vehicle's activity and order states.
        /// Called by the fleet manager as mission execution progresses.
        /// </summary>
        public void UpdateState(ActivityState activity, OrderState orderState)
        {
            ActivityState = activity;
            OrderState = orderState;
        }

        /// <summary>
        /// Assigns a mission to this vehicle.
        /// </summary>
        public void AssignMission(int missionId)
        {
            if (missionId <= 0)
                throw new ArgumentOutOfRangeException(nameof(missionId),
                    "MissionId must be a positive integer.");
            CurrentMissionId = missionId;
            OrderState = OrderState.Waiting;
        }

        /// <summary>
        /// Clears the current mission assignment.
        /// Called when a mission completes or fails.
        /// </summary>
        public void ClearMission()
        {
            CurrentMissionId = null;
            OrderState = OrderState.Idle;
            ActivityState = ActivityState.Idle;
            IsLoaded = false;
        }

        /// <summary>
        /// Reassigns this vehicle to a different zone.
        /// </summary>
        public void Rezone(int? newZoneId)
            => CurrentZoneId = newZoneId;

        /// <summary>
        /// Marks the vehicle as carrying a load.
        /// </summary>
        public void SetLoaded(bool loaded) => IsLoaded = loaded;

        /// <summary>
        /// Takes this vehicle out of service.
        /// </summary>
        public void TakeOutOfService()
        {
            IsInService = false;
            ActivityState = ActivityState.OutOfService;
        }

        /// <summary>
        /// Returns this vehicle to service.
        /// </summary>
        public void ReturnToService()
        {
            IsInService = true;
            ActivityState = ActivityState.Idle;
            OrderState = OrderState.Idle;
        }

        /// <summary>
        /// Marks the vehicle as online (MQTT connection established).
        /// </summary>
        public void SetOnline() => IsOnline = true;

        /// <summary>
        /// Marks the vehicle as offline (MQTT connection lost).
        /// </summary>
        public void SetOffline() => IsOnline = false;

        /// <summary>
        /// True if this vehicle is available for mission dispatch.
        /// </summary>
        public bool IsAvailableForDispatch
            => IsInService
            && IsOnline
            && OrderState == OrderState.Idle
            && ActivityState == ActivityState.Idle
            && !IsLoaded;

        /// <summary>
        /// True if this vehicle needs opportunity charging.
        /// Threshold is defined in fleet configuration.
        /// </summary>
        public bool NeedsOpportunityCharge(decimal socThreshold)
            => BatteryStateOfCharge < socThreshold;

        /// <summary>
        /// True if this vehicle requires mandatory charging.
        /// Threshold is defined in fleet configuration.
        /// </summary>
        public bool NeedsMandatoryCharge(decimal socThreshold)
            => BatteryStateOfCharge < socThreshold;

        public override string ToString()
            => $"Vehicle[{VehicleId}] {VehicleName} " +
               $"({VehicleType}) " +
               $"SOC={BatteryStateOfCharge:F1}% " +
               $"Activity={ActivityState} " +
               $"Online={IsOnline} " +
               $"InService={IsInService}";
    }
}