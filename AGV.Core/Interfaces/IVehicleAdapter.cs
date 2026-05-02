using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Messages;

namespace AGV.Core.Interfaces
{
    /// <summary>
    /// Contract for the vehicle communication adapter.
    ///
    /// IVehicleAdapter is the seam between the host control system
    /// and the physical (or simulated) vehicles. It abstracts away
    /// whether the host is talking to real AGVs over MQTT/VDA 5050
    /// or to the simulation engine.
    ///
    /// Two implementations:
    ///   AGV.Mqtt.MqttVehicleAdapter    — real vehicles over MQTT
    ///   NYT.AGV.Simulation.SimulatedVehicleAdapter — simulation
    ///
    /// Switched via appsettings.json:
    ///   "VehicleInterface": "Mqtt"       → MqttVehicleAdapter
    ///   "VehicleInterface": "Simulation" → SimulatedVehicleAdapter
    ///
    /// The fleet manager, traffic manager, and charge queue manager
    /// never reference either implementation directly — they only
    /// use this interface. This means the host logic is identical
    /// whether running against real hardware or simulation.
    /// </summary>
    public interface IVehicleAdapter
    {
        /// <summary>
        /// Sends a VDA 5050 Order to the specified vehicle.
        /// The order contains the node/edge sequence for the current
        /// base/horizon window with all attached actions.
        /// </summary>
        Task SendOrderAsync(
            string serialNumber,
            VehicleOrder order,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a VDA 5050 InstantAction to the specified vehicle.
        /// Used for: cancelOrder, pauseOrder, startCharging,
        /// stopCharging, waitForTrigger, triggerRelease.
        /// </summary>
        Task SendInstantActionAsync(
            string serialNumber,
            VehicleInstantAction instantAction,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Registers a callback invoked when any vehicle publishes
        /// a VDA 5050 State message.
        /// The host fleet manager subscribes here to receive
        /// continuous vehicle state updates.
        /// </summary>
        void OnStateReceived(
            Func<VehicleStateUpdate, CancellationToken, Task> handler);

        /// <summary>
        /// Registers a callback invoked when any vehicle publishes
        /// a VDA 5050 Visualization message.
        /// Used for high-frequency position updates to the dashboard
        /// without the overhead of full State messages.
        /// </summary>
        void OnVisualizationReceived(
            Func<VehiclePositionUpdate, CancellationToken, Task> handler);

        /// <summary>
        /// Registers a callback invoked when a vehicle connects or
        /// disconnects from the broker.
        /// Used by the fleet manager to mark vehicles online/offline
        /// and trigger dead mission detection on unexpected dropouts.
        /// </summary>
        void OnConnectionChanged(
            Func<VehicleConnectionEvent, CancellationToken, Task> handler);

        /// <summary>
        /// Registers a callback invoked when a vehicle publishes its
        /// VDA 5050 Fact Sheet on initial connection.
        /// The fleet manager stores the fact sheet and uses it to
        /// size order windows for that vehicle.
        /// </summary>
        void OnFactSheetReceived(
            Func<VehicleFactSheetEvent, CancellationToken, Task> handler);

        /// <summary>
        /// Returns the current connection state of a specific vehicle.
        /// </summary>
        bool IsVehicleOnline(string serialNumber);

        /// <summary>
        /// Starts the adapter — opens broker connection (MQTT) or
        /// starts the simulation loop (simulation).
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops the adapter gracefully.
        /// </summary>
        Task StopAsync(CancellationToken cancellationToken = default);
    }

    // ----------------------------------------------------------------
    // Supporting types for IVehicleAdapter
    // ----------------------------------------------------------------

    /// <summary>
    /// A VDA 5050 Order payload — the node/edge sequence with
    /// attached actions for the current base/horizon window.
    /// </summary>
    public sealed class VehicleOrder
    {
        public string OrderId { get; init; } = string.Empty;
        public int OrderUpdateId { get; init; }
        public IReadOnlyList<OrderNode> Nodes { get; init; }
            = Array.Empty<OrderNode>();
        public IReadOnlyList<OrderEdge> Edges { get; init; }
            = Array.Empty<OrderEdge>();
    }

    /// <summary>A node in a VDA 5050 Order message.</summary>
    public sealed class OrderNode
    {
        public string NodeId { get; init; } = string.Empty;
        public int SequenceId { get; init; }
        public bool Released { get; init; }
        public decimal X { get; init; }
        public decimal Y { get; init; }
        public string MapId { get; init; } = string.Empty;
        public IReadOnlyList<OrderAction> Actions { get; init; }
            = Array.Empty<OrderAction>();
    }

    /// <summary>An edge in a VDA 5050 Order message.</summary>
    public sealed class OrderEdge
    {
        public string EdgeId { get; init; } = string.Empty;
        public int SequenceId { get; init; }
        public bool Released { get; init; }
        public string StartNodeId { get; init; } = string.Empty;
        public string EndNodeId { get; init; } = string.Empty;
        public decimal MaxSpeed { get; init; }
        public IReadOnlyList<OrderAction> Actions { get; init; }
            = Array.Empty<OrderAction>();
    }

    /// <summary>A VDA 5050 action attached to a node or edge.</summary>
    public sealed class OrderAction
    {
        public string ActionId { get; init; } = string.Empty;
        public string ActionType { get; init; } = string.Empty;

        /// <summary>
        /// HARD — vehicle waits for action completion before moving.
        /// SOFT — vehicle continues moving while action executes.
        /// </summary>
        public string BlockingType { get; init; } = "HARD";

        public IReadOnlyList<ActionParameter> Parameters { get; init; }
            = Array.Empty<ActionParameter>();
    }

    /// <summary>A key/value parameter for a VDA 5050 action.</summary>
    public sealed class ActionParameter
    {
        public string Key { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
    }

    /// <summary>A VDA 5050 InstantAction payload.</summary>
    public sealed class VehicleInstantAction
    {
        public string HeaderId { get; init; } = string.Empty;
        public IReadOnlyList<OrderAction> InstantActions { get; init; }
            = Array.Empty<OrderAction>();
    }

    /// <summary>
    /// Published when a vehicle connects or disconnects.
    /// </summary>
    public sealed class VehicleConnectionEvent
    {
        public string SerialNumber { get; init; } = string.Empty;
        public bool IsOnline { get; init; }
        public DateTime EventAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Published when a vehicle sends its VDA 5050 Fact Sheet
    /// on initial connection.
    /// </summary>
    public sealed class VehicleFactSheetEvent
    {
        public string SerialNumber { get; init; } = string.Empty;
        public int MaxOrderHorizonDepth { get; init; }
        public bool SupportsNurbsTrajectory { get; init; }
        public string SupportedActionTypes { get; init; } = string.Empty;
        public decimal MaxSpeedMs { get; init; }
        public decimal MaxPayloadKg { get; init; }
        public decimal LengthMeters { get; init; }
        public decimal WidthMeters { get; init; }
        public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;
    }
}