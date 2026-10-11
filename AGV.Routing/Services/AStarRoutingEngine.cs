using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;
using AGV.Core.Interfaces;
using AGV.Topology.Services;
using Microsoft.Extensions.Logging;

namespace AGV.Routing.Services
{
    /// <summary>
    /// Phase 1 IRoutingEngine implementation — A* with pose-space
    /// heading expansion and dynamic edge weights.
    ///
    /// Algorithm:
    ///   State space: (NodeId, HeadingBucket) pairs
    ///   Cost function: f(n) = g(n) + h(n)
    ///     g(n) = accumulated travel time + turn costs + dynamic weights
    ///     h(n) = Euclidean distance to goal / max speed (admissible)
    ///
    ///   The heuristic is admissible because Euclidean distance / max speed
    ///   is always ≤ actual travel time — it never overestimates.
    ///   This guarantees A* finds the optimal time path.
    ///
    /// Thread safety:
    ///   FindRouteAsync is stateless per call — safe for concurrent use.
    ///   The PoseExpandedGraph is immutable and shared across calls.
    ///   A new graph is atomically swapped in on topology version change.
    ///
    /// Phase upgrade path:
    ///   Phase 2 (SIPP) replaces this class's internal search loop
    ///   with safe-interval expansion — the IRoutingEngine interface
    ///   and all callers remain unchanged.
    /// </summary>
    public sealed class AStarRoutingEngine : IRoutingEngine
    {
        private volatile PoseExpandedGraph? _graph;
        private readonly TurnCostTable _turnCosts;
        private readonly ILogger<AStarRoutingEngine> _logger;
        private readonly TopologyVersionManager _versionManager;

        // Maximum number of nodes to expand before giving up
        // Prevents runaway search on degenerate topologies
        private const int MaxExpansions = 50_000;

        public AStarRoutingEngine(
            TurnCostTable turnCosts,
            TopologyVersionManager versionManager,
            ILogger<AStarRoutingEngine> logger)
        {
            _turnCosts = turnCosts
                ?? throw new ArgumentNullException(nameof(turnCosts));
            _versionManager = versionManager
                ?? throw new ArgumentNullException(nameof(versionManager));
            _logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }
        // ----------------------------------------------------------------
        // IRoutingEngine implementation
        // ----------------------------------------------------------------

        public async Task<RouteResult?> FindRouteAsync(
            int originNodeId,
            int destinationNodeId,
            int vehicleId,
            IReadOnlySet<int> blockedNodeIds,
            IReadOnlySet<int> blockedMoveIds,
            CancellationToken cancellationToken = default)
        {
            var graph = _graph;
            if (graph is null)
            {
                if (_versionManager.IsLoaded)
                {
                    _logger.LogWarning(
                        "Route requested but no topology loaded in routing engine. " +
                        "VehicleId={VehicleId}", vehicleId);
                }
                else
                {
                    _logger.LogDebug(
                        "Route requested before topology loaded. VehicleId={VehicleId}",
                        vehicleId);
                }
                return null;
            }

            if (originNodeId == destinationNodeId)
                return BuildSingleNodeResult(originNodeId, graph);

            if (!graph.IsRouteable(originNodeId))
            {
                _logger.LogWarning(
                    "Origin node {NodeId} is not routeable",
                    originNodeId);
                return null;
            }

            if (!graph.IsStoppable(destinationNodeId))
            {
                _logger.LogWarning(
                    "Destination node {NodeId} is not a stop node",
                    destinationNodeId);
                return null;
            }

            // Run A* on a thread pool thread — keeps the calling
            // BackgroundService loop responsive
            return await Task.Run(() => RunAStar(
                graph,
                originNodeId,
                destinationNodeId,
                vehicleId,
                blockedNodeIds,
                blockedMoveIds,
                cancellationToken), cancellationToken);
        }

        public async Task<double?> EstimateTravelTimeAsync(
            int originNodeId,
            int destinationNodeId,
            int vehicleId,
            CancellationToken cancellationToken = default)
        {
            var graph = _graph;
            if (graph is null) return null;

            // Fast heuristic estimate — Euclidean distance / max speed
            // Used by order stealing evaluator for quick comparisons
            var (ox, oy) = graph.GetNodePosition(originNodeId);
            var (dx, dy) = graph.GetNodePosition(destinationNodeId);

            var distanceCm = Math.Sqrt(
                Math.Pow((double)(dx - ox), 2) +
                Math.Pow((double)(dy - oy), 2));

            // Default max speed 1.5 m/s = 150 cm/s
            const double maxSpeedCmPerSec = 150.0;
            return await Task.FromResult(distanceCm / maxSpeedCmPerSec);
        }

        public async Task InvalidateAsync(
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "Routing engine invalidated — graph will be " +
                "rebuilt on next topology activation");
            _graph = null;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Called by RoutingBackgroundService when a new topology
        /// version is activated. Builds and atomically swaps in
        /// the new pose-expanded graph.
        /// </summary>
        public void ActivateGraph(PoseExpandedGraph graph)
        {
            _graph = graph;
            _logger.LogInformation(
                "Routing engine activated: {Summary}",
                graph.GetSummary());
        }

        // ----------------------------------------------------------------
        // A* implementation
        // ----------------------------------------------------------------

        private RouteResult? RunAStar(
            PoseExpandedGraph graph,
            int originNodeId,
            int destinationNodeId,
            int vehicleId,
            IReadOnlySet<int> blockedNodeIds,
            IReadOnlySet<int> blockedMoveIds,
            CancellationToken cancellationToken)
        {
            var (destX, destY) = graph.GetNodePosition(destinationNodeId);

            // Open set: min-heap ordered by f = g + h
            var openSet = new PriorityQueue<AStarNode, double>();

            // Best known g-cost per pose
            var gScore = new Dictionary<(int, int), double>();

            // For path reconstruction
            var cameFrom = new Dictionary<(int, int), AStarNode?>();

            // Initialize — try all heading buckets at origin
            // (vehicle could be facing any direction at start)
            for (int bucket = 0; bucket < _turnCosts.BucketCount; bucket++)
            {
                var startPose = (originNodeId, bucket);
                var h = Heuristic(graph, originNodeId, destX, destY);
                var startNode = new AStarNode(
                    originNodeId, bucket, 0.0,
                    h, null, -1, 0m);

                gScore[startPose] = 0.0;
                cameFrom[startPose] = null;
                openSet.Enqueue(startNode, h);
            }

            var expansions = 0;

            while (openSet.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;

                if (++expansions > MaxExpansions)
                {
                    _logger.LogWarning(
                        "A* exceeded max expansions ({Max}) " +
                        "for vehicle {VehicleId} " +
                        "route {Origin}→{Dest}",
                        MaxExpansions, vehicleId,
                        originNodeId, destinationNodeId);
                    return null;
                }

                var current = openSet.Dequeue();
                var currentPose = (current.NodeId, current.HeadingBucket);

                // Goal check
                if (current.NodeId == destinationNodeId)
                {
                    return ReconstructPath(
                        current, cameFrom,
                        graph, originNodeId);
                }

                // Skip if we've already found a better path to this pose
                if (gScore.TryGetValue(currentPose, out var bestG)
                    && current.GCost > bestG + 1e-9)
                    continue;

                // Expand neighbors
                foreach (var transition in graph.GetTransitions(
                    current.NodeId, current.HeadingBucket))
                {
                    if (blockedNodeIds.Contains(transition.ToNodeId))
                        continue;
                    if (blockedMoveIds.Contains(transition.MoveId))
                        continue;

                    var neighborPose = (transition.ToNodeId,
                                        transition.ToHeadingBucket);
                    var tentativeG = current.GCost +
                                       transition.TotalCostSeconds;

                    if (gScore.TryGetValue(neighborPose, out var existingG)
                        && tentativeG >= existingG - 1e-9)
                        continue;

                    gScore[neighborPose] = tentativeG;
                    var h = Heuristic(graph, transition.ToNodeId,
                                      destX, destY);
                    var neighbor = new AStarNode(
                        transition.ToNodeId,
                        transition.ToHeadingBucket,
                        tentativeG,
                        h,
                        currentPose,
                        transition.MoveId,
                        transition.ArrivalHeadingDegrees);

                    cameFrom[neighborPose] = current;
                    openSet.Enqueue(neighbor, tentativeG + h);
                }
            }

            // No path found
            _logger.LogDebug(
                "No route found: vehicle {VehicleId} " +
                "{Origin}→{Dest} after {Expansions} expansions",
                vehicleId, originNodeId, destinationNodeId, expansions);
            return null;
        }

        private static double Heuristic(
            PoseExpandedGraph graph,
            int nodeId,
            decimal destX, decimal destY)
        {
            var (nx, ny) = graph.GetNodePosition(nodeId);
            var distCm = Math.Sqrt(
                Math.Pow((double)(destX - nx), 2) +
                Math.Pow((double)(destY - ny), 2));
            // Max speed 1.5 m/s = 150 cm/s — admissible lower bound
            return distCm / 150.0;
        }

        private static RouteResult ReconstructPath(
            AStarNode goal,
            Dictionary<(int, int), AStarNode?> cameFrom,
            PoseExpandedGraph graph,
            int originNodeId)
        {
            var nodes = new List<RouteNode>();
            var moveIds = new List<int>();
            var totalDistanceCm = 0m;

            var current = (AStarNode?)goal;
            while (current is not null)
            {
                var pose = (current.NodeId, current.HeadingBucket);
                nodes.Add(new RouteNode
                {
                    NodeId = current.NodeId,
                    ArrivalHeadingDegrees = current.ArrivalHeading,
                });
                if (current.MoveId >= 0)
                    moveIds.Add(current.MoveId);

                cameFrom.TryGetValue(pose, out current);
            }

            nodes.Reverse();
            moveIds.Reverse();

            return new RouteResult
            {
                Nodes = nodes.AsReadOnly(),
                MoveIds = moveIds.AsReadOnly(),
                EstimatedTravelTimeSeconds = goal.GCost,
                TotalDistanceCm = totalDistanceCm,
                AlgorithmUsed = "AStar-PoseSpace-v1",
            };
        }

        private static RouteResult BuildSingleNodeResult(
            int nodeId, PoseExpandedGraph graph)
            => new()
            {
                Nodes = new List<RouteNode>
                {
                    new() { NodeId = nodeId,
                            ArrivalHeadingDegrees = 0m }
                }.AsReadOnly(),
                MoveIds = Array.Empty<int>(),
                EstimatedTravelTimeSeconds = 0.0,
                TotalDistanceCm = 0m,
                AlgorithmUsed = "AStar-PoseSpace-v1",
            };

        // ----------------------------------------------------------------
        // Internal A* node
        // ----------------------------------------------------------------

        private sealed class AStarNode
        {
            public int NodeId { get; }
            public int HeadingBucket { get; }
            public double GCost { get; }
            public double HCost { get; }
            public (int NodeId, int HeadingBucket)? CameFrom { get; }
            public int MoveId { get; }
            public decimal ArrivalHeading { get; }

            public AStarNode(
                int nodeId, int headingBucket,
                double gCost, double hCost,
                (int, int)? cameFrom,
                int moveId,
                decimal arrivalHeading)
            {
                NodeId = nodeId;
                HeadingBucket = headingBucket;
                GCost = gCost;
                HCost = hCost;
                CameFrom = cameFrom;
                MoveId = moveId;
                ArrivalHeading = arrivalHeading;
            }
        }
    }
}