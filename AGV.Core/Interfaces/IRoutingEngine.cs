using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Entities;

namespace AGV.Core.Interfaces
{
    /// <summary>
    /// Contract for the AGV routing engine.
    ///
    /// The routing engine is the sole owner of path calculation.
    /// All other services request routes through this interface —
    /// they never perform graph traversal themselves.
    ///
    /// Phase implementation plan:
    ///   Phase 1: A* with pose-space heading expansion
    ///            + dynamic edge weights
    ///   Phase 2: SIPP (Safe Interval Path Planning)
    ///            layered on Phase 1
    ///   Phase 3: CBS (Conflict-Based Search)
    ///            using Phase 2 SIPP as low-level planner
    ///
    /// The interface is stable across all three phases —
    /// only the implementation inside AGV.Routing changes.
    /// The Order Builder and MQTT layer never need to know
    /// which algorithm produced the route.
    /// </summary>
    public interface IRoutingEngine
    {
        /// <summary>
        /// Calculates the optimal route from origin to destination.
        ///
        /// Returns an ordered list of (NodeId, HeadingDegrees) pairs
        /// representing the pose-expanded path. The heading at each
        /// step is the arrival heading from the incoming move —
        /// Move.Clothoid.EndHeading.
        ///
        /// Returns null if no route exists (destination unreachable,
        /// all paths blocked, or origin == destination with no
        /// self-loop defined).
        /// </summary>
        /// <param name="originNodeId">
        /// Logical NodeId of the vehicle's current position.
        /// </param>
        /// <param name="destinationNodeId">
        /// Logical NodeId of the target position.
        /// </param>
        /// <param name="vehicleId">
        /// The vehicle requesting the route. Used to apply
        /// vehicle-type specific RoutingType filtering and
        /// capability constraints from the VehicleFactSheet.
        /// </param>
        /// <param name="blockedNodeIds">
        /// Set of NodeIds currently blocked at runtime —
        /// occupied by other vehicles, NodeBlock records,
        /// or area capacity limits reached.
        /// The router will not include these in any returned path.
        /// </param>
        /// <param name="blockedMoveIds">
        /// Set of MoveIds currently blocked at runtime —
        /// MoveBlock records or maintenance closures.
        /// </param>
        /// <param name="cancellationToken">
        /// Allows the fleet manager to cancel a route request
        /// if the dispatch decision is superseded before the
        /// route calculation completes.
        /// </param>
        /// <returns>
        /// RouteResult containing the ordered node/move sequence
        /// and estimated travel time, or null if no route found.
        /// </returns>
        Task<RouteResult?> FindRouteAsync(
            int originNodeId,
            int destinationNodeId,
            int vehicleId,
            IReadOnlySet<int> blockedNodeIds,
            IReadOnlySet<int> blockedMoveIds,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Estimates travel time between two nodes without computing
        /// the full route. Used by the order stealing evaluator to
        /// quickly compare thief vs victim proximity without the
        /// overhead of full pose-space A* expansion.
        /// </summary>
        Task<double?> EstimateTravelTimeAsync(
            int originNodeId,
            int destinationNodeId,
            int vehicleId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Notifies the routing engine that the topology has changed
        /// (new roadmap version loaded, runtime block added/removed).
        /// The engine invalidates any cached graph structures and
        /// rebuilds the pose-expanded adjacency graph.
        /// </summary>
        Task InvalidateAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// The result of a successful route calculation.
    /// </summary>
    public sealed class RouteResult
    {
        /// <summary>
        /// Ordered sequence of nodes comprising the route.
        /// First entry is the origin, last is the destination.
        /// </summary>
        public IReadOnlyList<RouteNode> Nodes { get; init; }
            = Array.Empty<RouteNode>();

        /// <summary>
        /// Ordered sequence of moves connecting the route nodes.
        /// Moves[i] connects Nodes[i] to Nodes[i+1].
        /// Count is always Nodes.Count - 1.
        /// </summary>
        public IReadOnlyList<int> MoveIds { get; init; }
            = Array.Empty<int>();

        /// <summary>
        /// Estimated total travel time in seconds.
        /// Calculated from move arc lengths and default speeds,
        /// adjusted for turn costs and dynamic edge weights.
        /// </summary>
        public double EstimatedTravelTimeSeconds { get; init; }

        /// <summary>
        /// Total route distance in centimeters.
        /// Sum of all move arc lengths.
        /// </summary>
        public decimal TotalDistanceCm { get; init; }

        /// <summary>
        /// The algorithm that produced this route.
        /// Used for diagnostics and metrics.
        /// </summary>
        public string AlgorithmUsed { get; init; } = string.Empty;
    }

    /// <summary>
    /// A single node in a computed route, including the
    /// arrival heading from the pose-expanded search.
    /// </summary>
    public sealed class RouteNode
    {
        /// <summary>Logical NodeId.</summary>
        public int NodeId { get; init; }

        /// <summary>
        /// Arrival heading at this node in signed degrees.
        /// For the origin node this is the vehicle's current heading.
        /// For all subsequent nodes this is Move.Clothoid.EndHeading
        /// of the incoming move.
        /// </summary>
        public decimal ArrivalHeadingDegrees { get; init; }
    }
}