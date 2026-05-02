using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Entities
{
    /// <summary>
    /// Stores the capability declaration published by a vehicle when it
    /// first connects to the MQTT broker.
    ///
    /// In VDA 5050, the Fact Sheet is the vehicle's formal declaration
    /// of what it can do and what constraints the host must respect.
    /// The host reads this once on connection and uses it permanently
    /// to govern how it communicates with that vehicle.
    ///
    /// Key values the host uses from the Fact Sheet:
    ///
    ///   MaxOrderHorizonDepth — the vehicle's command buffer size.
    ///     The host NEVER sends an order window deeper than this.
    ///     This is the VDA 5050 answer to the legacy drip-feed buffer
    ///     depth constraint.
    ///
    ///   SupportsNurbsTrajectory — whether the vehicle can consume
    ///     NURBS trajectory data on edges. For vehicles using onboard
    ///     clothoid navigation (Option B), this is false — the host
    ///     sends node IDs and max speeds only.
    ///
    ///   SupportedActionTypes — comma-separated list of VDA 5050 action
    ///     types this vehicle supports. The host will not send unsupported
    ///     actions to this vehicle.
    ///
    /// The FactSheet is updated each time the vehicle reconnects —
    /// firmware updates may change capabilities.
    /// </summary>
    public class VehicleFactSheet
    {
        /// <summary>
        /// Primary key — matches VehicleId in the Vehicle entity.
        /// One-to-one relationship.
        /// </summary>
        public int VehicleId { get; private set; }

        /// <summary>
        /// VDA 5050 protocol version supported by this vehicle.
        /// Example: "2.0.0"
        /// </summary>
        public string ProtocolVersion { get; private set; }

        /// <summary>
        /// Maximum number of nodes + edges the vehicle's command buffer
        /// can hold. The host sizes its order window (base + horizon)
        /// to never exceed this depth.
        ///
        /// This is the formal declaration of what legacy systems handled
        /// as a hardcoded drip-feed buffer depth constant.
        /// </summary>
        public int MaxOrderHorizonDepth { get; private set; }

        /// <summary>
        /// True if this vehicle can consume NURBS trajectory geometry
        /// on order edges. False for vehicles using onboard clothoid
        /// path following (Option B — host sends node IDs only).
        /// </summary>
        public bool SupportsNurbsTrajectory { get; private set; }

        /// <summary>
        /// Comma-separated list of VDA 5050 action types supported
        /// by this vehicle.
        /// Examples: "pick,drop,startCharging,stopCharging,waitForTrigger"
        /// </summary>
        public string SupportedActionTypes { get; private set; }

        /// <summary>
        /// Maximum speed of this vehicle in meters per second.
        /// The host will not issue orders with speeds exceeding this.
        /// </summary>
        public decimal MaxSpeedMs { get; private set; }

        /// <summary>
        /// Maximum payload weight this vehicle can carry in kilograms.
        /// </summary>
        public decimal MaxPayloadKg { get; private set; }

        /// <summary>
        /// Vehicle length in meters (for traffic spacing calculations).
        /// </summary>
        public decimal LengthMeters { get; private set; }

        /// <summary>
        /// Vehicle width in meters (for aisle clearance validation).
        /// </summary>
        public decimal WidthMeters { get; private set; }

        /// <summary>
        /// Timestamp when this Fact Sheet was last received from the vehicle.
        /// Updated on each reconnection — firmware updates may change
        /// declared capabilities.
        /// </summary>
        public DateTime LastReceivedAt { get; private set; }

        // Private constructor for EF Core
        private VehicleFactSheet()
        {
            ProtocolVersion = null!;
            SupportedActionTypes = null!;
        }

        public VehicleFactSheet(
            int vehicleId,
            string protocolVersion,
            int maxOrderHorizonDepth,
            bool supportsNurbsTrajectory,
            string supportedActionTypes,
            decimal maxSpeedMs,
            decimal maxPayloadKg,
            decimal lengthMeters,
            decimal widthMeters)
        {
            if (vehicleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(vehicleId),
                    "VehicleId must be a positive integer.");

            if (string.IsNullOrWhiteSpace(protocolVersion))
                throw new ArgumentException(
                    "ProtocolVersion cannot be null or empty.",
                    nameof(protocolVersion));

            if (maxOrderHorizonDepth <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxOrderHorizonDepth),
                    "MaxOrderHorizonDepth must be greater than zero.");

            if (string.IsNullOrWhiteSpace(supportedActionTypes))
                throw new ArgumentException(
                    "SupportedActionTypes cannot be null or empty.",
                    nameof(supportedActionTypes));

            if (maxSpeedMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSpeedMs),
                    "MaxSpeedMs must be greater than zero.");

            if (maxPayloadKg <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPayloadKg),
                    "MaxPayloadKg must be greater than zero.");

            if (lengthMeters <= 0)
                throw new ArgumentOutOfRangeException(nameof(lengthMeters),
                    "LengthMeters must be greater than zero.");

            if (widthMeters <= 0)
                throw new ArgumentOutOfRangeException(nameof(widthMeters),
                    "WidthMeters must be greater than zero.");

            VehicleId = vehicleId;
            ProtocolVersion = protocolVersion;
            MaxOrderHorizonDepth = maxOrderHorizonDepth;
            SupportsNurbsTrajectory = supportsNurbsTrajectory;
            SupportedActionTypes = supportedActionTypes;
            MaxSpeedMs = maxSpeedMs;
            MaxPayloadKg = maxPayloadKg;
            LengthMeters = lengthMeters;
            WidthMeters = widthMeters;
            LastReceivedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Updates this Fact Sheet with freshly received data.
        /// Called each time the vehicle reconnects — capabilities
        /// may change after firmware updates.
        /// </summary>
        public void Update(
            string protocolVersion,
            int maxOrderHorizonDepth,
            bool supportsNurbsTrajectory,
            string supportedActionTypes,
            decimal maxSpeedMs,
            decimal maxPayloadKg,
            decimal lengthMeters,
            decimal widthMeters)
        {
            ProtocolVersion = protocolVersion;
            MaxOrderHorizonDepth = maxOrderHorizonDepth;
            SupportsNurbsTrajectory = supportsNurbsTrajectory;
            SupportedActionTypes = supportedActionTypes;
            MaxSpeedMs = maxSpeedMs;
            MaxPayloadKg = maxPayloadKg;
            LengthMeters = lengthMeters;
            WidthMeters = widthMeters;
            LastReceivedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Returns true if this vehicle supports a specific VDA 5050
        /// action type.
        /// </summary>
        public bool SupportsAction(string actionType)
        {
            if (string.IsNullOrWhiteSpace(actionType))
                return false;
            return SupportedActionTypes
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(a => a.Trim().Equals(
                    actionType.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        public override string ToString()
            => $"FactSheet[Vehicle={VehicleId}] " +
               $"Protocol={ProtocolVersion} " +
               $"BufferDepth={MaxOrderHorizonDepth} " +
               $"NURBS={SupportsNurbsTrajectory} " +
               $"MaxSpeed={MaxSpeedMs:F4}m/s " +
               $"Updated={LastReceivedAt:HH:mm:ss}";
    }
}