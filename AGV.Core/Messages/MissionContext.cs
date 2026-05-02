using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;

namespace AGV.Core.Messages
{
    /// <summary>
    /// Carries the complete identity and history of a mission throughout
    /// its lifecycle — including across vehicle transfers.
    ///
    /// MissionContext is the stable identity carrier that travels with a
    /// mission regardless of which vehicle is executing it. It preserves
    /// the original intent (pickup, drop, load identity, source system
    /// reference) across order stealing, mission swap, and mission transfer
    /// events.
    ///
    /// Key design points:
    ///
    ///   Identity persistence:
    ///     The MissionId never changes — even if the mission is transferred
    ///     three times across three vehicles, the MissionId remains the
    ///     original. This is critical for external system reconciliation
    ///     (e.g. SAP confirming their storage strategy was honored).
    ///
    ///   Priority:
    ///     Travels with the mission through assignment, transfer, and swap.
    ///     Governs both dispatch queue ordering and lock contention
    ///     resolution — highest priority waiter wins when a contested
    ///     node becomes free. Always available to traffic management
    ///     without requiring a separate lookup.
    ///
    ///   TransferHistory:
    ///     An ordered audit trail of every vehicle that has touched this
    ///     mission. Essential for post-mortem analysis and external system
    ///     reporting.
    ///
    ///   PickupDeadline:
    ///     Optional time-sensitivity flag. When set, the mission escalates
    ///     priority in the queue as the deadline approaches. Used when
    ///     conveyor queues are backing up and a delayed pickup has
    ///     downstream consequences.
    ///
    ///   SourceSystemReference:
    ///     Opaque string carrying the external system's order identifier
    ///     (e.g. SAP order number, WMS task ID). Never interpreted by
    ///     the host — passed through to audit trail and completion
    ///     callbacks only.
    /// </summary>
    public sealed record MissionContext
    {
        /// <summary>
        /// The original mission identifier — never changes across transfers.
        /// </summary>
        public int MissionId { get; init; }

        /// <summary>
        /// The VDA 5050 orderId for the current vehicle executing this mission.
        /// Changes on each transfer — the new vehicle gets a new Order.
        /// </summary>
        public string CurrentOrderId { get; init; } = string.Empty;

        /// <summary>
        /// Logical NodeId of the pickup location.
        /// Preserved across transfers — the new vehicle goes to the
        /// same pickup.
        /// </summary>
        public int PickupNodeId { get; init; }

        /// <summary>
        /// Logical NodeId of the drop-off location.
        /// Pre-assigned and preserved — critical for systems like P&G
        /// where the drop destination is determined by the source system
        /// before pickup and must not change regardless of which vehicle
        /// ultimately executes the delivery.
        /// </summary>
        public int DropNodeId { get; init; }

        /// <summary>
        /// Optional load identity — SKU, pallet ID, roll ID, etc.
        /// Opaque to the host — passed through to audit and callbacks.
        /// </summary>
        public string? LoadIdentity { get; init; }

        /// <summary>
        /// Optional reference to the originating external system order.
        /// Examples: SAP order number, WMS task ID, ERP reference.
        /// Opaque to the host — preserved for reconciliation.
        /// </summary>
        public string? SourceSystemReference { get; init; }

        /// <summary>
        /// Mission dispatch and traffic management priority.
        /// Governs both dispatch queue ordering and lock contention
        /// resolution — highest priority waiter wins when a contested
        /// node becomes free.
        /// May be escalated by the dead mission detector when a mission
        /// is returned to the pending queue after a vehicle fault.
        /// </summary>
        public MissionPriority Priority { get; init; }

        /// <summary>
        /// UTC timestamp when this mission was originally created.
        /// Never changes across transfers.
        /// </summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>
        /// Optional deadline by which the pickup must be initiated.
        /// When set and approaching, the fleet manager escalates this
        /// mission's priority in the dispatch queue.
        /// Null means no time constraint.
        /// </summary>
        public DateTime? PickupDeadline { get; init; }

        /// <summary>
        /// Ordered audit trail of every vehicle that has held this mission.
        /// Populated on initial dispatch and appended on every transfer.
        /// </summary>
        public IReadOnlyList<MissionTransferRecord> TransferHistory { get; init; }
            = Array.Empty<MissionTransferRecord>();

        /// <summary>
        /// True if this mission has been transferred at least once.
        /// </summary>
        public bool HasBeenTransferred => TransferHistory.Count > 0;

        /// <summary>
        /// The vehicle currently assigned to this mission.
        /// Null if the mission is in the pending queue awaiting assignment.
        /// </summary>
        public int? CurrentVehicleId { get; init; }

        /// <summary>
        /// Creates a new MissionContext with an additional transfer record
        /// appended to the history.
        /// Returns a new instance — MissionContext is immutable.
        /// </summary>
        public MissionContext WithTransfer(
            int fromVehicleId,
            int? toVehicleId,
            string newOrderId,
            TransferReason reason)
        {
            var newHistory = TransferHistory
                .Append(new MissionTransferRecord
                {
                    FromVehicleId = fromVehicleId,
                    ToVehicleId = toVehicleId,
                    Reason = reason,
                    TransferredAt = DateTime.UtcNow
                })
                .ToList();

            return this with
            {
                CurrentVehicleId = toVehicleId,
                CurrentOrderId = newOrderId,
                TransferHistory = newHistory
            };
        }

        /// <summary>
        /// Creates a new MissionContext with escalated priority.
        /// Used when a mission is returned to the queue after a fault
        /// and needs to jump ahead of newly created missions.
        /// </summary>
        public MissionContext WithEscalatedPriority(MissionPriority newPriority)
            => this with { Priority = newPriority };
    }

    /// <summary>
    /// A single record in a mission's transfer audit trail.
    /// </summary>
    public sealed record MissionTransferRecord
    {
        /// <summary>The vehicle that held the mission before transfer.</summary>
        public int FromVehicleId { get; init; }

        /// <summary>
        /// The vehicle the mission was transferred to.
        /// Null if the mission was returned to the pending queue
        /// (no vehicle was immediately available).
        /// </summary>
        public int? ToVehicleId { get; init; }

        /// <summary>Why the transfer occurred.</summary>
        public TransferReason Reason { get; init; }

        /// <summary>UTC timestamp of the transfer.</summary>
        public DateTime TransferredAt { get; init; }
    }

    /// <summary>
    /// Reasons a mission may be transferred from one vehicle to another.
    /// </summary>
    public enum TransferReason
    {
        /// <summary>
        /// Vehicle faulted or became disabled mid-mission.
        /// </summary>
        VehicleFault = 1,

        /// <summary>
        /// Mission was swapped at pickup due to arrival order mismatch
        /// against pre-assigned drop destination sequence.
        /// </summary>
        PickupArrivalSwap = 2,

        /// <summary>
        /// Manual override by operator.
        /// </summary>
        ManualOverride = 3,

        /// <summary>
        /// Vehicle required emergency charging — mission reassigned
        /// to prevent convoy formation.
        /// </summary>
        EmergencyCharge = 4,

        /// <summary>
        /// Vehicle taken out of service during mission execution.
        /// </summary>
        VehicleRemovedFromService = 5
    }
}