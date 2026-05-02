using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Messages
{
    /// <summary>
    /// Published by the dead mission detector when a vehicle fault or
    /// removal from service requires a mission to be handed off to
    /// another vehicle or returned to the pending queue.
    ///
    /// Unlike MissionSwap (which exchanges two missions between two
    /// vehicles at pickup), MissionTransfer moves one mission from a
    /// failed vehicle to either a replacement vehicle or the pending queue.
    ///
    /// Critical responsibilities triggered by this message:
    ///
    ///   1. TrafficManagerService MUST release all resource locks held
    ///      by the FromVehicle before the replacement vehicle can be
    ///      routed. Orphaned locks are the single most dangerous
    ///      consequence of a vehicle fault — they silently block
    ///      traffic indefinitely.
    ///
    ///   2. MQTT publisher sends cancelOrder InstantAction to the
    ///      faulted vehicle (if still online).
    ///
    ///   3. FleetManagerService routes the faulted vehicle to the
    ///      nearest maintenance node via a diagnostic mission.
    ///
    ///   4. MissionContext is carried forward intact — the original
    ///      MissionId, drop destination, load identity, and source
    ///      system reference are preserved for the replacement vehicle.
    ///
    /// Consumed by:
    ///   — TrafficManagerService (orphaned lock release — FIRST)
    ///   — MQTT publisher (cancelOrder to faulted vehicle)
    ///   — FleetManagerService (diagnostic mission + replacement dispatch)
    ///   — MissionDispatchService (re-queue or reassign)
    /// </summary>
    public sealed record MissionTransfer
    {
        /// <summary>
        /// The vehicle that faulted or was removed from service.
        /// Its resource locks must be released immediately.
        /// </summary>
        public int FromVehicleId { get; init; }

        /// <summary>
        /// The vehicle the mission is being transferred to.
        /// Null if no vehicle was immediately available —
        /// mission returns to pending queue.
        /// </summary>
        public int? ToVehicleId { get; init; }

        /// <summary>
        /// The complete mission context being transferred.
        /// Contains the original MissionId, pickup node, drop node,
        /// load identity, source system reference, and the full
        /// transfer history up to this point.
        /// The TransferHistory will be appended with this transfer
        /// by MissionSwapExecutor before the context is reissued.
        /// </summary>
        public MissionContext MissionContext { get; init; } = null!;

        /// <summary>
        /// Why the transfer was triggered.
        /// </summary>
        public TransferReason Reason { get; init; }

        /// <summary>
        /// True if the mission should be returned to the pending queue
        /// rather than immediately assigned to ToVehicleId.
        /// Always true when ToVehicleId is null.
        /// May be true even when ToVehicleId is set — for example when
        /// the replacement vehicle needs to complete a charge cycle
        /// before it can accept a new mission.
        /// </summary>
        public bool ReturnToQueue { get; init; }

        /// <summary>
        /// Priority to assign when returning to the queue.
        /// Typically escalated above the original priority to compensate
        /// for time already lost to the fault.
        /// Only relevant when ReturnToQueue is true.
        /// </summary>
        public int EscalatedPriority { get; init; }

        /// <summary>
        /// The last confirmed node of the faulted vehicle at the time
        /// of transfer. Used by TrafficManagerService to identify
        /// which resource locks to release.
        /// </summary>
        public int? FaultedAtNodeId { get; init; }

        /// <summary>
        /// True if the faulted vehicle was still online (MQTT connected)
        /// at the time of transfer.
        /// Determines whether cancelOrder InstantAction can be sent.
        /// </summary>
        public bool FaultedVehicleIsOnline { get; init; }

        /// <summary>UTC timestamp when the transfer was initiated.</summary>
        public DateTime TransferredAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// True if this transfer has a receiving vehicle assigned.
        /// False if the mission is returning to the pending queue.
        /// </summary>
        public bool HasReceivingVehicle
            => ToVehicleId.HasValue && !ReturnToQueue;
    }
}