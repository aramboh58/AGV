using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Messages
{
    /// <summary>
    /// Published by ChargeQueueManagerService when it assigns a vehicle
    /// to a charge slot (opportunity or mandatory).
    ///
    /// Consumed by:
    ///   — FleetManagerService (updates vehicle activity state,
    ///     suspends mission dispatch for this vehicle)
    ///   — MQTT publisher (constructs and sends VDA 5050 Order with
    ///     startCharging action at the charge node)
    ///   — TrafficManagerService (reserves the charge slot node)
    /// </summary>
    public sealed class ChargeAssignment
    {
        /// <summary>The vehicle being directed to charge.</summary>
        public int VehicleId { get; init; }

        /// <summary>VDA 5050 serialNumber of the vehicle.</summary>
        public string SerialNumber { get; init; } = string.Empty;

        /// <summary>
        /// The logical NodeId of the assigned charge station.
        /// The vehicle will be routed to this node and a
        /// startCharging action issued on arrival.
        /// </summary>
        public int ChargeNodeId { get; init; }

        /// <summary>
        /// Distinguishes opportunity charging from mandatory charging.
        /// Drives different SOC exit thresholds and station types.
        /// </summary>
        public ChargeType ChargeType { get; init; }

        /// <summary>
        /// The planned route as an ordered list of logical NodeIds
        /// from the vehicle's current position to the charge node.
        /// </summary>
        public IReadOnlyList<int> RouteNodeIds { get; init; }
            = Array.Empty<int>();

        /// <summary>
        /// The planned route as an ordered list of logical MoveIds.
        /// </summary>
        public IReadOnlyList<int> RouteMoveIds { get; init; }
            = Array.Empty<int>();

        /// <summary>
        /// SOC threshold at which the vehicle should stop charging
        /// and return to service (opportunity charge only).
        /// Not applicable for mandatory charge — vehicle charges
        /// to 100% regardless.
        /// </summary>
        public decimal? OpportunityExitSocThreshold { get; init; }

        /// <summary>UTC timestamp when this assignment was made.</summary>
        public DateTime AssignedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Distinguishes the type of charging being assigned.
    /// </summary>
    public enum ChargeType
    {
        /// <summary>
        /// Inline FIFO opportunity charge — vehicle charges between
        /// missions when SOC drops below threshold.
        /// Exits when SOC reaches OpportunityExitSocThreshold.
        /// </summary>
        Opportunity = 1,

        /// <summary>
        /// Discrete mandatory charge — vehicle is directed to a
        /// dedicated charge station when SOC is critically low.
        /// Charges to 100% before returning to service.
        /// </summary>
        Mandatory = 2,

        /// <summary>
        /// Full maintenance cycle — vehicle battery is first fully
        /// drained then recharged to 100%.
        /// Scheduled periodically to maintain lead-acid battery health.
        /// </summary>
        Maintenance = 3
    }
}