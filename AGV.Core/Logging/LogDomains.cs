using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Logging
{
    /// <summary>
    /// Defines the logging domain constants used throughout the
    /// AGV host system.
    ///
    /// Domain names follow a hierarchical dot-notation pattern.
    /// Setting a log level on a parent domain applies to all
    /// child domains unless explicitly overridden.
    ///
    /// Example: setting AGV.Traffic to Warning suppresses Debug
    /// and Information from all traffic sub-domains unless
    /// AGV.Traffic.LockManager is explicitly set to Debug.
    ///
    /// Usage:
    ///   private readonly ILogger _logger;
    ///
    ///   public TrafficManagerService(ILoggerFactory factory)
    ///   {
    ///       _logger = factory.CreateLogger(LogDomains.LockManager);
    ///   }
    ///
    /// appsettings.json configuration:
    ///   "Logging": {
    ///     "LogLevel": {
    ///       "Default":                    "Information",
    ///       "AGV.Traffic":                "Warning",
    ///       "AGV.Traffic.LockManager":    "Debug",
    ///       "AGV.Traffic.Deadlock":       "Information",
    ///       "AGV.Router":                 "Warning",
    ///       "AGV.Mqtt.Messages":          "None",
    ///       "AGV.Forensic":               "Information"
    ///     }
    ///   }
    /// </summary>
    public static class LogDomains
    {
        // ----------------------------------------------------------------
        // Traffic management
        // ----------------------------------------------------------------

        /// <summary>Parent domain for all traffic management logging.</summary>
        public const string Traffic = "AGV.Traffic";

        /// <summary>Atomic check+lock cycle — node/move acquisition.</summary>
        public const string LockManager = "AGV.Traffic.LockManager";

        /// <summary>Deadlock detection, ghost filter, escape node resolution.</summary>
        public const string Deadlock = "AGV.Traffic.Deadlock";

        /// <summary>Detour evaluation and execution.</summary>
        public const string Detour = "AGV.Traffic.Detour";

        /// <summary>Redirect and truncate-and-append operations.</summary>
        public const string Redirect = "AGV.Traffic.Redirect";

        // ----------------------------------------------------------------
        // Routing
        // ----------------------------------------------------------------

        /// <summary>A* pose-space route calculation.</summary>
        public const string Router = "AGV.Router";

        // ----------------------------------------------------------------
        // Mission management
        // ----------------------------------------------------------------

        /// <summary>Parent domain for all mission management logging.</summary>
        public const string Mission = "AGV.Mission";

        /// <summary>Vehicle selection, dispatch decisions, queue management.</summary>
        public const string Dispatch = "AGV.Mission.Dispatch";

        /// <summary>Order stealing evaluation and execution.</summary>
        public const string OrderStealing = "AGV.Mission.OrderStealing";

        /// <summary>Mission swap detection and execution (P&G pattern).</summary>
        public const string Swap = "AGV.Mission.Swap";

        /// <summary>Mission transfer on vehicle fault.</summary>
        public const string Transfer = "AGV.Mission.Transfer";

        /// <summary>Dead mission detection and resolution.</summary>
        public const string DeadMission = "AGV.Mission.DeadMission";

        // ----------------------------------------------------------------
        // Fleet management
        // ----------------------------------------------------------------

        /// <summary>Parent domain for fleet management logging.</summary>
        public const string Fleet = "AGV.Fleet";

        /// <summary>Per-vehicle state updates and lifecycle events.</summary>
        public const string Vehicle = "AGV.Fleet.Vehicle";

        /// <summary>Zone assignment and rezoning events.</summary>
        public const string Zone = "AGV.Fleet.Zone";

        // ----------------------------------------------------------------
        // Charging
        // ----------------------------------------------------------------

        /// <summary>Opportunity and mandatory charge queue management.</summary>
        public const string Charging = "AGV.Charging";

        /// <summary>Maintenance cycle scheduling and execution.</summary>
        public const string Maintenance = "AGV.Charging.Maintenance";

        // ----------------------------------------------------------------
        // MQTT / VDA 5050
        // ----------------------------------------------------------------

        /// <summary>Parent domain for MQTT broker interface logging.</summary>
        public const string Mqtt = "AGV.Mqtt";

        /// <summary>
        /// Individual VDA 5050 message send/receive.
        /// Set to None in production to suppress RF message flood.
        /// Set to Debug when diagnosing vehicle communication issues.
        /// </summary>
        public const string VdaMessages = "AGV.Mqtt.Messages";

        /// <summary>Vehicle connection/disconnection events.</summary>
        public const string Connection = "AGV.Mqtt.Connection";

        /// <summary>Base/horizon window management and order updates.</summary>
        public const string OrderWindow = "AGV.Mqtt.OrderWindow";

        // ----------------------------------------------------------------
        // I/O layer
        // ----------------------------------------------------------------

        /// <summary>Parent domain for all I/O layer logging.</summary>
        public const string IO = "AGV.IO";

        /// <summary>OPC UA transport — tag reads, writes, subscriptions.</summary>
        public const string OpcUa = "AGV.IO.OpcUa";

        /// <summary>Modbus TCP transport — charger and concentrator I/O.</summary>
        public const string Modbus = "AGV.IO.Modbus";

        /// <summary>Charger monitoring — SOC, fault, temperature events.</summary>
        public const string Charger = "AGV.IO.Charger";

        // ----------------------------------------------------------------
        // Topology
        // ----------------------------------------------------------------

        /// <summary>Topology version loading and graph construction.</summary>
        public const string Topology = "AGV.Topology";

        // ----------------------------------------------------------------
        // Forensic buffer
        // ----------------------------------------------------------------

        /// <summary>
        /// Forensic buffer flush events and incident recording.
        /// Keep at Information or higher — always want flush events logged.
        /// </summary>
        public const string Forensic = "AGV.Forensic";

        // ----------------------------------------------------------------
        // History and analytics
        // ----------------------------------------------------------------

        /// <summary>History aggregation service — retention enforcement.</summary>
        public const string History = "AGV.History";

        // ----------------------------------------------------------------
        // Persistence
        // ----------------------------------------------------------------

        /// <summary>Database operations — migrations, context lifecycle.</summary>
        public const string Persistence = "AGV.Persistence";

        // ----------------------------------------------------------------
        // Host / startup
        // ----------------------------------------------------------------

        /// <summary>Host startup, shutdown, service registration.</summary>
        public const string Host = "AGV.Host";
    }
}
