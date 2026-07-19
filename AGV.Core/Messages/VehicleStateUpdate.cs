using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;

namespace AGV.Core.Messages
{
    /// <summary>
    /// Published by the MQTT listener (or simulation engine) when a
    /// full VDA 5050 State message is received from a vehicle.
    ///
    /// This is a richer update than VehiclePositionUpdate — it carries
    /// battery, activity, order state, and load information.
    ///
    /// Consumed by:
    ///   — FleetManagerService (updates vehicle registry, evaluates
    ///     charging needs, triggers dispatch)
    ///   — ChargeQueueManagerService (evaluates SOC thresholds)
    ///   — DashboardHub (forwards full state to SignalR clients)
    /// </summary>
    public sealed class VehicleStateUpdate
    {
        /// <summary>Vehicle that published the state.</summary>
        public int VehicleId { get; init; }

        /// <summary>VDA 5050 serialNumber.</summary>
        public string SerialNumber { get; init; } = string.Empty;

        /// <summary>Physical vehicle classification.</summary>
        public string VehicleType { get; init; } = string.Empty;

        /// <summary>Current activity state.</summary>
        public ActivityState ActivityState { get; init; }

        /// <summary>Current VDA 5050 order state.</summary>
        public OrderState OrderState { get; init; }

        /// <summary>Current operating mode.</summary>
        public OperatingMode OperatingMode { get; init; }

        /// <summary>
        /// Battery state of charge as a percentage (0.0 to 100.0).
        /// From VDA 5050 State.batteryState.batteryCharge.
        /// </summary>
        public decimal BatteryStateOfCharge { get; init; }

        /// <summary>
        /// True if the vehicle is currently charging.
        /// From VDA 5050 State.batteryState.charging.
        /// </summary>
        public bool IsCharging { get; init; }

        /// <summary>True if the vehicle is currently carrying a load.</summary>
        public bool IsLoaded { get; init; }

        /// <summary>
        /// The VDA 5050 orderId currently being executed.
        /// Empty string if the vehicle is idle.
        /// </summary>
        public string CurrentOrderId { get; init; } = string.Empty;

        /// <summary>
        /// Last confirmed node ID from VDA 5050 State.lastNodeId.
        /// Zero if not yet reported.
        /// </summary>
        public int LastNodeId { get; init; }

        /// <summary>
        /// The orderUpdateId from the most recent State message.
        /// Used by the host to detect missed or out-of-order updates.
        /// </summary>
        public int OrderUpdateId { get; init; }

        /// <summary>
        /// Any active errors reported by the vehicle.
        /// From VDA 5050 State.errors.
        /// </summary>
        public IReadOnlyList<string> Errors { get; init; }
            = Array.Empty<string>();

        /// <summary>UTC timestamp when this update was received.</summary>
        public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;
    }
}