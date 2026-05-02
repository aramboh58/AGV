using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;

namespace AGV.Topology.Services
{
    /// <summary>
    /// Manages the runtime blocking state of nodes and moves.
    ///
    /// RuntimeBlockingState is the in-memory mirror of the NodeBlock
    /// and MoveBlock database tables, plus the AreaOccupancy table.
    /// It is loaded at startup and kept current by the traffic manager
    /// as vehicles move through the facility.
    ///
    /// This is a presence-based model:
    ///   — A node or move is blocked if a record exists for it here.
    ///   — There is no history or audit trail in this class.
    ///   — Removing a block record restores the resource to available.
    ///
    /// Thread safety:
    ///   All collections use ConcurrentDictionary for thread-safe
    ///   access without explicit locking. The traffic manager is the
    ///   sole writer — reads come from routing, fleet manager, and
    ///   dashboard without coordination overhead.
    ///
    /// Relationship to the check table:
    ///   RuntimeBlockingState tracks VEHICLE-HELD locks and
    ///   ENGINEER-SPECIFIED blocks (NodeBlock/MoveBlock records).
    ///   The check table (NYT_Checks) is a separate in-memory cache
    ///   managed by the traffic manager — it defines WHAT to check
    ///   before locking, not the lock state itself.
    /// </summary>
    public sealed class RuntimeBlockingState
    {
        // ----------------------------------------------------------------
        // Node blocking
        // ----------------------------------------------------------------

        /// <summary>
        /// Nodes currently blocked.
        /// Key: logical NodeId
        /// Value: NodeBlockRecord describing why and by whom
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, NodeBlockRecord>
            _blockedNodes = new();

        /// <summary>
        /// Returns true if the specified node is currently blocked
        /// for any reason (vehicle lock or engineer block).
        /// </summary>
        public bool IsNodeBlocked(int nodeId)
            => _blockedNodes.ContainsKey(nodeId);

        /// <summary>
        /// Returns the block record for the specified node,
        /// or null if the node is not blocked.
        /// </summary>
        public NodeBlockRecord? GetNodeBlock(int nodeId)
            => _blockedNodes.TryGetValue(nodeId, out var record)
                ? record : null;

        /// <summary>
        /// Blocks a node. Called by the traffic manager when a vehicle
        /// acquires a lock or an engineer block is applied.
        /// </summary>
        public void BlockNode(int nodeId, NodeBlockRecord record)
            => _blockedNodes[nodeId] = record;

        /// <summary>
        /// Releases a node block. Called by the traffic manager when
        /// a vehicle clears a node (unlock phase) or an engineer
        /// block is removed.
        /// </summary>
        public bool UnblockNode(int nodeId)
            => _blockedNodes.TryRemove(nodeId, out _);

        /// <summary>
        /// Returns the set of all currently blocked NodeIds.
        /// Used by the routing engine to exclude blocked nodes
        /// from path calculation.
        /// </summary>
        public IReadOnlySet<int> GetBlockedNodeIds()
            => _blockedNodes.Keys.ToHashSet();

        /// <summary>
        /// Returns all nodes currently locked by a specific vehicle.
        /// Used during fault recovery to identify orphaned locks.
        /// </summary>
        public IReadOnlySet<int> GetVehicleLockedNodeIds(int vehicleId)
            => _blockedNodes
                .Where(kvp => kvp.Value.LockedByVehicleId == vehicleId)
                .Select(kvp => kvp.Key)
                .ToHashSet();

        /// <summary>
        /// Releases all locks held by a specific vehicle.
        /// CRITICAL — must be called immediately on vehicle fault
        /// before any other transfer logic runs.
        /// Returns the set of NodeIds that were released.
        /// </summary>
        public IReadOnlySet<int> ReleaseAllVehicleLocks(int vehicleId)
        {
            var released = new HashSet<int>();
            foreach (var kvp in _blockedNodes)
            {
                if (kvp.Value.LockedByVehicleId == vehicleId)
                {
                    if (_blockedNodes.TryRemove(kvp.Key, out _))
                        released.Add(kvp.Key);
                }
            }
            return released;
        }

        // ----------------------------------------------------------------
        // Move blocking
        // ----------------------------------------------------------------

        /// <summary>
        /// Moves currently blocked.
        /// Key: logical MoveId
        /// Value: MoveBlockRecord describing why and by whom
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, MoveBlockRecord>
            _blockedMoves = new();

        /// <summary>
        /// Returns true if the specified move is currently blocked.
        /// </summary>
        public bool IsMoveBlocked(int moveId)
            => _blockedMoves.ContainsKey(moveId);

        /// <summary>
        /// Returns the block record for the specified move,
        /// or null if the move is not blocked.
        /// </summary>
        public MoveBlockRecord? GetMoveBlock(int moveId)
            => _blockedMoves.TryGetValue(moveId, out var record)
                ? record : null;

        /// <summary>
        /// Blocks a move.
        /// </summary>
        public void BlockMove(int moveId, MoveBlockRecord record)
            => _blockedMoves[moveId] = record;

        /// <summary>
        /// Releases a move block.
        /// </summary>
        public bool UnblockMove(int moveId)
            => _blockedMoves.TryRemove(moveId, out _);

        /// <summary>
        /// Returns the set of all currently blocked MoveIds.
        /// Used by the routing engine to exclude blocked moves.
        /// </summary>
        public IReadOnlySet<int> GetBlockedMoveIds()
            => _blockedMoves.Keys.ToHashSet();

        /// <summary>
        /// Releases all move locks held by a specific vehicle.
        /// Called alongside ReleaseAllVehicleLocks on vehicle fault.
        /// </summary>
        public IReadOnlySet<int> ReleaseAllVehicleMoveLocks(int vehicleId)
        {
            var released = new HashSet<int>();
            foreach (var kvp in _blockedMoves)
            {
                if (kvp.Value.LockedByVehicleId == vehicleId)
                {
                    if (_blockedMoves.TryRemove(kvp.Key, out _))
                        released.Add(kvp.Key);
                }
            }
            return released;
        }

        // ----------------------------------------------------------------
        // Area occupancy
        // ----------------------------------------------------------------

        /// <summary>
        /// Current vehicle count per area.
        /// Key: logical AreaId
        /// Value: current occupancy count
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int>
            _areaOccupancy = new();

        /// <summary>
        /// Returns the current vehicle count for the specified area.
        /// Returns 0 if no vehicles are in the area.
        /// </summary>
        public int GetAreaOccupancy(int areaId)
            => _areaOccupancy.TryGetValue(areaId, out var count) ? count : 0;

        /// <summary>
        /// Increments the occupancy count for an area when a vehicle
        /// enters. Returns the new count.
        /// </summary>
        public int IncrementAreaOccupancy(int areaId)
            => _areaOccupancy.AddOrUpdate(areaId, 1, (_, count) => count + 1);

        /// <summary>
        /// Decrements the occupancy count for an area when a vehicle
        /// exits. Returns the new count. Never goes below zero.
        /// </summary>
        public int DecrementAreaOccupancy(int areaId)
            => _areaOccupancy.AddOrUpdate(
                areaId,
                0,
                (_, count) => Math.Max(0, count - 1));

        /// <summary>
        /// Returns true if the specified area is at or above its
        /// maximum vehicle count limit.
        /// Returns false if the area has no limit (MaxVehicleCount is null).
        /// </summary>
        public bool IsAreaAtCapacity(int areaId, int? maxVehicleCount)
        {
            if (!maxVehicleCount.HasValue) return false;
            return GetAreaOccupancy(areaId) >= maxVehicleCount.Value;
        }

        // ----------------------------------------------------------------
        // Summary
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns a snapshot summary of current blocking state.
        /// Used in diagnostics and dashboard reporting.
        /// </summary>
        public RuntimeBlockingSummary GetSummary()
            => new()
            {
                BlockedNodeCount = _blockedNodes.Count,
                BlockedMoveCount = _blockedMoves.Count,
                AreaOccupancies = _areaOccupancy
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                SnapshotAt = DateTime.UtcNow
            };
    }

    // ----------------------------------------------------------------
    // Supporting record types
    // ----------------------------------------------------------------

    /// <summary>
    /// Describes why and by whom a node is currently blocked.
    /// </summary>
    public sealed record NodeBlockRecord
    {
        /// <summary>
        /// The vehicle that holds this lock.
        /// Null for engineer-specified blocks (not vehicle-held).
        /// </summary>
        public int? LockedByVehicleId { get; init; }

        /// <summary>
        /// The reason this block was applied.
        /// </summary>
        public BlockReason Reason { get; init; }

        /// <summary>
        /// True if this is a vehicle-held lock (acquired via
        /// the check+lock phase). False for engineer blocks.
        /// </summary>
        public bool IsVehicleLock => LockedByVehicleId.HasValue;

        /// <summary>UTC timestamp when this block was applied.</summary>
        public DateTime BlockedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Optional description for engineer-specified blocks.
        /// </summary>
        public string? Description { get; init; }
    }

    /// <summary>
    /// Describes why and by whom a move is currently blocked.
    /// </summary>
    public sealed record MoveBlockRecord
    {
        /// <summary>
        /// The vehicle that holds this move lock.
        /// Null for engineer-specified blocks.
        /// </summary>
        public int? LockedByVehicleId { get; init; }

        /// <summary>The reason this block was applied.</summary>
        public BlockReason Reason { get; init; }

        /// <summary>
        /// True if this is a vehicle-held lock.
        /// False for engineer blocks (lateral clearance reservations).
        /// </summary>
        public bool IsVehicleLock => LockedByVehicleId.HasValue;

        /// <summary>UTC timestamp when this block was applied.</summary>
        public DateTime BlockedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Optional description for engineer-specified blocks.
        /// </summary>
        public string? Description { get; init; }
    }

    /// <summary>
    /// Point-in-time snapshot of the runtime blocking state.
    /// Used in diagnostics and dashboard reporting.
    /// </summary>
    public sealed class RuntimeBlockingSummary
    {
        public int BlockedNodeCount { get; init; }
        public int BlockedMoveCount { get; init; }
        public IReadOnlyDictionary<int, int> AreaOccupancies { get; init; }
            = new Dictionary<int, int>();
        public DateTime SnapshotAt { get; init; }
    }
}