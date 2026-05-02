using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Messages
{
    /// <summary>
    /// Published by FleetManagerService when it decides to assign a
    /// mission to a vehicle.
    ///
    /// This message flows from the fleet manager to the traffic manager
    /// and MQTT publisher via their respective channels.
    ///
    /// The traffic manager consumes it to:
    ///   — Reserve the vehicle's planned route nodes
    ///   — Update zone occupancy projections
    ///
    /// The MQTT publisher consumes it to:
    ///   — Construct and send the initial VDA 5050 Order message
    ///     to the assigned vehicle
    /// </summary>
    public sealed class MissionDispatchDecision
    {
        /// <summary>The mission being dispatched.</summary>
        public int MissionId { get; init; }

        /// <summary>The VDA 5050 orderId for this mission.</summary>
        public string OrderId { get; init; } = string.Empty;

        /// <summary>The vehicle assigned to execute this mission.</summary>
        public int VehicleId { get; init; }

        /// <summary>VDA 5050 serialNumber of the assigned vehicle.</summary>
        public string SerialNumber { get; init; } = string.Empty;

        /// <summary>
        /// The planned route as an ordered list of logical NodeIds
        /// from the vehicle's current position to the pickup node.
        /// </summary>
        public IReadOnlyList<int> RouteNodeIds { get; init; }
            = Array.Empty<int>();

        /// <summary>
        /// The planned route as an ordered list of logical MoveIds
        /// connecting the route nodes.
        /// RouteNodeIds and RouteMoveIds are paired —
        /// RouteMoveIds[i] connects RouteNodeIds[i] to RouteNodeIds[i+1].
        /// </summary>
        public IReadOnlyList<int> RouteMoveIds { get; init; }
            = Array.Empty<int>();

        /// <summary>
        /// The pickup LocationAssignment this mission targets.
        /// </summary>
        public int PickupAssignmentId { get; init; }

        /// <summary>
        /// The dropoff LocationAssignment this mission targets.
        /// </summary>
        public int DropoffAssignmentId { get; init; }

        /// <summary>
        /// Estimated travel time to pickup in seconds.
        /// Used by the traffic manager for route reservation timing.
        /// </summary>
        public double EstimatedTravelTimeSeconds { get; init; }

        /// <summary>UTC timestamp when this dispatch decision was made.</summary>
        public DateTime DecidedAt { get; init; } = DateTime.UtcNow;
    }
}