using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Entities;
using AGV.Topology.Services;

namespace AGV.Routing.Services
{
    /// <summary>
    /// Pre-computed pose-expanded adjacency graph for A* routing.
    ///
    /// Standard graph routing finds the shortest path between two nodes.
    /// Pose-space routing finds the shortest path between two POSES —
    /// where a pose is (NodeId, HeadingBucket). This means the router
    /// considers not just which nodes to visit but which heading the
    /// vehicle arrives at each node with.
    ///
    /// Why pose-space matters for AGVs:
    ///   A vehicle arriving at node N from the west faces east.
    ///   A vehicle arriving at node N from the north faces south.
    ///   These are different states — each has different turn costs
    ///   for subsequent moves. A route that ignores heading may
    ///   recommend a path that requires an expensive 180° turn
    ///   when a slightly longer path avoids it entirely.
    ///
    /// Construction:
    ///   Built once when the RoadMapGraph is loaded or version changes.
    ///   For each node and each heading bucket, all valid outbound
    ///   moves are expanded into pose-to-pose transitions with their
    ///   combined travel time + turn cost.
    ///
    /// Thread safety:
    ///   Immutable after construction — no locking needed for reads.
    ///   A new instance is built when the topology version changes
    ///   and atomically swapped into the routing engine.
    /// </summary>
    public sealed class PoseExpandedGraph
    {
        /// <summary>
        /// The topology version this graph was built from.
        /// </summary>
        public int RoadmapVersionId { get; }

        /// <summary>
        /// UTC timestamp when this graph was constructed.
        /// </summary>
        public DateTime BuiltAt { get; }

        /// <summary>
        /// All pose-to-pose transitions in the expanded graph.
        ///
        /// Key:   (NodeId, HeadingBucket) — the origin pose
        /// Value: list of reachable transitions from that pose
        /// </summary>
        private readonly IReadOnlyDictionary<(int NodeId, int HeadingBucket),
            IReadOnlyList<PoseTransition>> _adjacency;

        /// <summary>
        /// The underlying road map graph this pose graph was built from.
        /// Retained for spatial queries (node positions for heuristic).
        /// </summary>
        private readonly RoadMapGraph _roadMap;

        /// <summary>
        /// The turn cost table used during construction.
        /// </summary>
        private readonly TurnCostTable _turnCosts;

        // ----------------------------------------------------------------
        // Construction
        // ----------------------------------------------------------------

        public PoseExpandedGraph(
            RoadMapGraph roadMap,
            TurnCostTable turnCosts,
            IReadOnlySet<int> blockedNodeIds,
            IReadOnlySet<int> blockedMoveIds)
        {
            _roadMap = roadMap
                ?? throw new ArgumentNullException(nameof(roadMap));
            _turnCosts = turnCosts
                ?? throw new ArgumentNullException(nameof(turnCosts));

            RoadmapVersionId = roadMap.RoadmapVersionId;
            BuiltAt = DateTime.UtcNow;

            _adjacency = BuildAdjacency(roadMap, turnCosts,
                blockedNodeIds, blockedMoveIds);
        }

        // ----------------------------------------------------------------
        // Graph queries
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns all pose transitions reachable from the given pose.
        /// Returns an empty list if the pose has no outbound transitions
        /// (dead end, blocked, or invalid heading bucket).
        /// </summary>
        public IReadOnlyList<PoseTransition> GetTransitions(
            int nodeId, int headingBucket)
        {
            return _adjacency.TryGetValue((nodeId, headingBucket),
                out var transitions)
                ? transitions
                : Array.Empty<PoseTransition>();
        }

        /// <summary>
        /// Returns the X coordinate of a node — used by the A*
        /// heuristic to compute Euclidean distance to the goal.
        /// </summary>
        public (decimal X, decimal Y) GetNodePosition(int nodeId)
        {
            var node = _roadMap.GetNode(nodeId);
            return node is null ? (0m, 0m) : (node.Position.X, node.Position.Y);
        }

        /// <summary>
        /// Returns true if the specified node exists and is routeable
        /// (not DestinationOnly, not deleted).
        /// </summary>
        public bool IsRouteable(int nodeId) => _roadMap.IsRouteable(nodeId);

        /// <summary>
        /// Returns true if the specified node is a valid stopping point.
        /// </summary>
        public bool IsStoppable(int nodeId) => _roadMap.IsStoppable(nodeId);

        /// <summary>
        /// Total number of poses in the expanded graph.
        /// Used for diagnostics and startup logging.
        /// </summary>
        public int PoseCount => _adjacency.Count;

        // ----------------------------------------------------------------
        // Graph construction
        // ----------------------------------------------------------------

        private static IReadOnlyDictionary<(int, int),
            IReadOnlyList<PoseTransition>> BuildAdjacency(
                RoadMapGraph roadMap,
                TurnCostTable turnCosts,
                IReadOnlySet<int> blockedNodeIds,
                IReadOnlySet<int> blockedMoveIds)
        {
            var adj = new Dictionary<(int, int), List<PoseTransition>>();

            foreach (var (nodeId, node) in roadMap.Nodes)
            {
                if (blockedNodeIds.Contains(nodeId)) continue;
                if (!node.IsRouteable) continue;

                var outboundMoves = roadMap.GetOutboundMoves(nodeId);
                if (outboundMoves.Count == 0) continue;

                // For each heading bucket at this node, compute
                // all reachable pose transitions
                for (int bucket = 0; bucket < turnCosts.BucketCount; bucket++)
                {
                    var incomingHeading = (decimal)
                        turnCosts.GetBucketCenterHeading(bucket);

                    var transitions = new List<PoseTransition>();

                    foreach (var move in outboundMoves)
                    {
                        if (blockedMoveIds.Contains(move.MoveId)) continue;

                        var toNode = roadMap.GetNode(move.ToNodeId);
                        if (toNode is null) continue;
                        if (blockedNodeIds.Contains(move.ToNodeId)) continue;

                        // Compute turn cost from incoming heading
                        // to this move's required start heading
                        var turnCost = turnCosts.GetTurnCost(
                            incomingHeading,
                            move.Clothoid.StartHeading);

                        // Travel time along this move
                        var travelTime = move.Speed.MaxSpeed > 0
                            ? (double)move.Clothoid.ArcLength /
                              100.0 /                              // cm → meters
                              (double)move.Speed.MaxSpeed          // meters/sec
                            : double.MaxValue;

                        // Total edge cost: travel time + turn penalty
                        var totalCost = travelTime + turnCost;

                        // Destination heading bucket
                        var arrivalBucket = turnCosts.GetHeadingBucket(
                            move.Clothoid.EndHeading);

                        transitions.Add(new PoseTransition
                        {
                            MoveId = move.MoveId,
                            FromNodeId = nodeId,
                            FromHeadingBucket = bucket,
                            ToNodeId = move.ToNodeId,
                            ToHeadingBucket = arrivalBucket,
                            ArrivalHeadingDegrees = move.Clothoid.EndHeading,
                            TravelTimeSeconds = travelTime,
                            TurnCostSeconds = turnCost,
                            TotalCostSeconds = totalCost,
                        });
                    }

                    if (transitions.Count > 0)
                    {
                        adj[(nodeId, bucket)] = transitions;
                    }
                }
            }

            return adj.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<PoseTransition>)
                    kvp.Value.AsReadOnly());
        }

        // ----------------------------------------------------------------
        // Summary
        // ----------------------------------------------------------------

        public string GetSummary()
            => $"PoseExpandedGraph v{RoadmapVersionId}: " +
               $"{PoseCount} poses, " +
               $"{_adjacency.Values.Sum(v => v.Count)} transitions, " +
               $"built {BuiltAt:HH:mm:ss} UTC";
    }

    /// <summary>
    /// A single pose-to-pose transition in the expanded graph.
    /// Represents traversing one move from one pose to another,
    /// including the cost of any heading change required.
    /// </summary>
    public sealed class PoseTransition
    {
        /// <summary>The move being traversed.</summary>
        public int MoveId { get; init; }

        /// <summary>Origin node.</summary>
        public int FromNodeId { get; init; }

        /// <summary>Origin heading bucket.</summary>
        public int FromHeadingBucket { get; init; }

        /// <summary>Destination node.</summary>
        public int ToNodeId { get; init; }

        /// <summary>Destination heading bucket after traversal.</summary>
        public int ToHeadingBucket { get; init; }

        /// <summary>
        /// Exact arrival heading in signed degrees.
        /// Carried through to the RouteResult for VDA 5050 order building.
        /// </summary>
        public decimal ArrivalHeadingDegrees { get; init; }

        /// <summary>Move traversal time in seconds at max speed.</summary>
        public double TravelTimeSeconds { get; init; }

        /// <summary>Turn cost penalty in seconds.</summary>
        public double TurnCostSeconds { get; init; }

        /// <summary>Total cost: travel time + turn penalty.</summary>
        public double TotalCostSeconds { get; init; }
    }
}