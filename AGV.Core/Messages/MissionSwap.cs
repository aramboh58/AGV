using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Messages
{
    /// <summary>
    /// Published by SwapCandidateEvaluator when it detects that two
    /// vehicles have arrived at sibling pickup nodes in an order that
    /// mismatches their pre-assigned drop destinations.
    ///
    /// The canonical case is the P&G Tabler Station pattern:
    ///   — SAP pre-assigns drop destinations before pickup
    ///   — Two vehicles are dispatched to adjacent conveyor pickups
    ///   — Actual arrival order differs from dispatch order
    ///   — Without swap: wrong SKU goes to wrong rack
    ///   — With swap: missions exchanged at pickup, storage
    ///     strategy preserved
    ///
    /// The swap executes BEFORE the pick action fires at either node.
    /// The HARD blocking action at each pickup node holds the vehicle
    /// while the host resolves the swap and reissues corrected orders.
    ///
    /// Consumed by:
    ///   — MissionSwapExecutor (cancels post-pickup order legs,
    ///     reissues with swapped drop destinations)
    ///   — FleetManagerService (updates mission assignment table)
    ///   — TrafficManagerService (updates route reservations)
    ///   — MQTT publisher (sends updated orders to both vehicles)
    /// </summary>
    public sealed record MissionSwap
    {
        /// <summary>First vehicle involved in the swap.</summary>
        public int VehicleIdA { get; init; }

        /// <summary>Mission currently assigned to VehicleA.</summary>
        public int MissionIdA { get; init; }

        /// <summary>
        /// The pickup node VehicleA has arrived at.
        /// After the swap VehicleA will execute MissionB's drop.
        /// </summary>
        public int PickupNodeIdA { get; init; }

        /// <summary>Second vehicle involved in the swap.</summary>
        public int VehicleIdB { get; init; }

        /// <summary>Mission currently assigned to VehicleB.</summary>
        public int MissionIdB { get; init; }

        /// <summary>
        /// The pickup node VehicleB has arrived at.
        /// After the swap VehicleB will execute MissionA's drop.
        /// </summary>
        public int PickupNodeIdB { get; init; }

        /// <summary>
        /// The reason this swap was triggered.
        /// </summary>
        public SwapReason Reason { get; init; }

        /// <summary>
        /// Optional reference to the external system context that
        /// drove the pre-assigned drop destinations.
        /// Carried forward for audit and reconciliation.
        /// Examples: SAP order batch ID, WMS wave ID.
        /// </summary>
        public string? SourceSystemReference { get; init; }

        /// <summary>UTC timestamp when the swap was detected.</summary>
        public DateTime DetectedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Reasons a mission swap may be triggered at pickup.
    /// </summary>
    public enum SwapReason
    {
        /// <summary>
        /// Vehicle arrival order at sibling pickup nodes does not match
        /// the pre-assigned drop destination sequence.
        /// The canonical P&G Tabler Station scenario.
        /// </summary>
        ArrivalOrderMismatch = 1,

        /// <summary>
        /// Manual swap initiated by operator via dashboard.
        /// </summary>
        ManualOperatorSwap = 2,

        /// <summary>
        /// Load identity confirmed at pickup differs from expected —
        /// drop destination must be corrected.
        /// </summary>
        LoadIdentityMismatch = 3
    }
}