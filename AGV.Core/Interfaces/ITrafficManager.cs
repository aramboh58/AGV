using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;
using AGV.Core.Messages;

namespace AGV.Core.Interfaces
{
    /// <summary>
    /// Contract for the traffic manager service.
    ///
    /// The traffic manager is the sole owner of resource lock state.
    /// It enforces collision avoidance by controlling which vehicles
    /// may traverse which nodes and moves at any given time.
    ///
    /// Phase 1 implementation: Resource reservation + waitForTrigger
    ///   — Node and move resource locks
    ///   — Check table evaluation (NYT check table import)
    ///   — waitForTrigger hold / triggerRelease flow
    ///   — Orphaned lock release on vehicle fault
    ///
    /// Phase 2: Zone-based coarse access control added
    /// Phase 3: Temporal conflict detection added
    ///
    /// Single ownership principle:
    ///   — TrafficManagerService is the sole writer of resource locks
    ///   — All other services request lock operations via this interface
    ///   — Lock state is never written directly by fleet manager or router
    ///
    /// Critical invariant:
    ///   On any vehicle fault or transfer, ReleaseAllLocksAsync MUST
    ///   be called before the replacement vehicle is routed. Orphaned
    ///   locks silently block traffic indefinitely.
    /// </summary>
    public interface ITrafficManager
    {
        /// <summary>
        /// Evaluates whether a vehicle may proceed to the next node
        /// by running all applicable checks from the check table.
        ///
        /// Returns TrafficClearance.Granted if all checks pass.
        /// Returns TrafficClearance.Hold if any check fails —
        /// the host will issue a waitForTrigger to the vehicle.
        /// Returns TrafficClearance.Denied if the move is permanently
        /// blocked (MoveBlock or NodeBlock in effect).
        /// </summary>
        Task<TrafficClearance> RequestClearanceAsync(
            int vehicleId,
            int fromNodeId,
            int toNodeId,
            int moveId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Acquires resource locks for a vehicle advancing to a node.
        /// Called after clearance is granted.
        /// Locks are held until the vehicle clears the resource.
        /// </summary>
        Task AcquireLocksAsync(
            int vehicleId,
            int nodeId,
            int moveId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Releases resource locks held by a vehicle as it clears
        /// a node or move. Called continuously as the vehicle
        /// progresses along its route.
        /// </summary>
        Task ReleaseLocksAsync(
            int vehicleId,
            int nodeId,
            int moveId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Releases ALL resource locks held by a specific vehicle.
        ///
        /// CRITICAL — must be called immediately on vehicle fault,
        /// removal from service, or mission transfer. Orphaned locks
        /// block all other vehicles that need those resources.
        ///
        /// This is the first operation triggered by MissionTransfer
        /// message processing — before any other transfer logic runs.
        /// </summary>
        Task ReleaseAllLocksAsync(
            int vehicleId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the set of NodeIds currently locked by any vehicle.
        /// Used by the routing engine to exclude occupied nodes from
        /// path calculation.
        /// </summary>
        IReadOnlySet<int> GetLockedNodeIds();

        /// <summary>
        /// Returns the set of MoveIds currently locked by any vehicle.
        /// Used by the routing engine to exclude blocked moves from
        /// path calculation.
        /// </summary>
        IReadOnlySet<int> GetLockedMoveIds();

        /// <summary>
        /// Returns the NodeIds currently locked by a specific vehicle.
        /// Used during fault recovery to identify which locks to release.
        /// </summary>
        IReadOnlySet<int> GetVehicleLockedNodeIds(int vehicleId);

        /// <summary>
        /// Updates area occupancy when a vehicle enters or exits an area.
        /// Triggers entry/exit event callbacks for downstream application
        /// logic (zone rezoning, throughput counting, alarms).
        /// </summary>
        Task UpdateAreaOccupancyAsync(
            int vehicleId,
            int nodeId,
            OccupancyUpdateType updateType,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the current vehicle count for a specific area.
        /// Used by the fleet manager to enforce MaxVehicleCount limits.
        /// </summary>
        int GetAreaOccupancy(int areaId);

        /// <summary>
        /// Registers a callback invoked when a vehicle enters an area.
        /// Used by the customization layer (ICustomizationApi) for
        /// site-specific entry logic.
        /// </summary>
        void OnAreaEntered(
            Func<AreaTransitEvent, CancellationToken, Task> handler);

        /// <summary>
        /// Registers a callback invoked when a vehicle exits an area.
        /// </summary>
        void OnAreaExited(
            Func<AreaTransitEvent, CancellationToken, Task> handler);
    }

    /// <summary>
    /// Result of a traffic clearance request.
    /// </summary>
    public enum TrafficClearance
    {
        /// <summary>
        /// All checks passed — vehicle may proceed.
        /// Host acquires locks and continues order execution.
        /// </summary>
        Granted = 0,

        /// <summary>
        /// One or more checks failed temporarily.
        /// Host issues waitForTrigger to vehicle.
        /// Traffic manager will issue triggerRelease when clear.
        /// </summary>
        Hold = 1,

        /// <summary>
        /// Node or move is permanently blocked (NodeBlock/MoveBlock).
        /// Host must reroute the vehicle.
        /// </summary>
        Denied = 2
    }

    /// <summary>
    /// Whether a vehicle is entering or exiting an area.
    /// </summary>
    public enum OccupancyUpdateType
    {
        Entered = 1,
        Exited = 2
    }

    /// <summary>
    /// Published when a vehicle crosses an area boundary.
    /// </summary>
    public sealed class AreaTransitEvent
    {
        public int VehicleId { get; init; }
        public int AreaId { get; init; }
        public string AreaName { get; init; } = string.Empty;
        public OccupancyUpdateType TransitType { get; init; }
        public int CurrentOccupancy { get; init; }
        public int? MaxOccupancy { get; init; }
        public DateTime EventAt { get; init; } = DateTime.UtcNow;
    }
}