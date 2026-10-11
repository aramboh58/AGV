using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Entities;
using AGV.Core.Enums;

namespace AGV.Topology.Services
{
    /// <summary>
    /// The in-memory road map graph.
    ///
    /// RoadMapGraph is the host's authoritative representation of the
    /// facility topology at a specific roadmap version. It is built
    /// once at startup (or on version change) from the database and
    /// held in memory for the lifetime of that version.
    ///
    /// All routing, traffic management, and fleet dispatch operations
    /// query this graph — never the database directly for real-time
    /// path operations.
    ///
    /// Thread safety:
    ///   RoadMapGraph is immutable after construction. All collections
    ///   are read-only. No locks are needed for concurrent reads.
    ///   A new instance is constructed when the topology version changes
    ///   and atomically swapped into the topology service.
    ///
    /// Coordinate system:
    ///   All positions are in centimeters to 0.01cm precision,
    ///   consistent with the Node.Position (Coordinate) value object.
    /// </summary>
    public sealed class RoadMapGraph
    {
        // ----------------------------------------------------------------
        // Core graph data
        // ----------------------------------------------------------------

        /// <summary>
        /// The roadmap version this graph represents.
        /// </summary>
        public int RoadmapVersionId { get; }

        /// <summary>
        /// All nodes in this topology, keyed by logical NodeId.
        /// Includes only non-deleted nodes effective at this version.
        /// </summary>
        public IReadOnlyDictionary<int, Node> Nodes { get; }

        /// <summary>
        /// All moves in this topology, keyed by logical MoveId.
        /// Includes only non-deleted moves effective at this version.
        /// </summary>
        public IReadOnlyDictionary<int, Move> Moves { get; }

        /// <summary>
        /// Adjacency list: NodeId → list of outbound moves from that node.
        /// Pre-computed at construction for O(1) neighbor lookup.
        /// </summary>
        public IReadOnlyDictionary<int, IReadOnlyList<Move>> Adjacency { get; }

        /// <summary>
        /// Reverse adjacency: NodeId → list of inbound moves to that node.
        /// Used by deadlock detection to traverse lock dependency chains.
        /// </summary>
        public IReadOnlyDictionary<int, IReadOnlyList<Move>> ReverseAdjacency { get; }

        /// <summary>
        /// All areas in this topology, keyed by logical AreaId.
        /// </summary>
        public IReadOnlyDictionary<int, Area> Areas { get; }

        /// <summary>
        /// Area membership: NodeId → list of AreaIds this node belongs to.
        /// A node may belong to multiple areas (fragmented area groupings).
        /// Pre-computed at construction for O(1) area lookup on position update.
        /// </summary>
        public IReadOnlyDictionary<int, IReadOnlyList<int>> NodeAreaMembership { get; }

        /// <summary>
        /// UTC timestamp when this graph was loaded from the database.
        /// </summary>
        public DateTime LoadedAt { get; }

        // ----------------------------------------------------------------
        // Construction
        // ----------------------------------------------------------------

        public RoadMapGraph(
            int roadmapVersionId,
            IEnumerable<Node> nodes,
            IEnumerable<Move> moves,
            IEnumerable<Area> areas,
            IEnumerable<(int NodeId, int AreaId)> areaMemberships)
        {
            RoadmapVersionId = roadmapVersionId;
            LoadedAt = DateTime.UtcNow;

            // Build node dictionary
            Nodes = nodes
                .Where(n => !n.IsDeleted)
                .ToDictionary(n => n.NodeId)
                as IReadOnlyDictionary<int, Node>
                ?? new Dictionary<int, Node>();

            // Build move dictionary
            var moveDict = moves
                .Where(m => !m.IsDeleted)
                .ToDictionary(m => m.MoveId);
            Moves = moveDict;

            // Build adjacency list (outbound)
            var adj = new Dictionary<int, List<Move>>();
            var revAdj = new Dictionary<int, List<Move>>();

            foreach (var nodeId in Nodes.Keys)
            {
                adj[nodeId] = new List<Move>();
                revAdj[nodeId] = new List<Move>();
            }

            foreach (var move in moveDict.Values)
            {
                if (adj.ContainsKey(move.FromNodeId))
                    adj[move.FromNodeId].Add(move);

                if (revAdj.ContainsKey(move.ToNodeId))
                    revAdj[move.ToNodeId].Add(move);
            }

            Adjacency = adj.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<Move>)kvp.Value.AsReadOnly());

            ReverseAdjacency = revAdj.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<Move>)kvp.Value.AsReadOnly());

            // Build area dictionary
            Areas = areas
                .Where(a => !a.IsDeleted)
                .ToDictionary(a => a.AreaId)
                as IReadOnlyDictionary<int, Area>
                ?? new Dictionary<int, Area>();

            // Build node → area membership lookup
            var membership = new Dictionary<int, List<int>>();
            foreach (var (nodeId, areaId) in areaMemberships)
            {
                if (!membership.ContainsKey(nodeId))
                    membership[nodeId] = new List<int>();
                membership[nodeId].Add(areaId);
            }

            NodeAreaMembership = membership.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<int>)kvp.Value.AsReadOnly());
        }

        // ----------------------------------------------------------------
        // Graph queries
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns all outbound moves from the specified node.
        /// Returns an empty list if the node has no outbound moves
        /// or does not exist.
        /// </summary>
        public IReadOnlyList<Move> GetOutboundMoves(int nodeId)
            => Adjacency.TryGetValue(nodeId, out var moves)
                ? moves
                : Array.Empty<Move>();

        /// <summary>
        /// Returns all inbound moves to the specified node.
        /// Used by deadlock detection to traverse dependency chains.
        /// </summary>
        public IReadOnlyList<Move> GetInboundMoves(int nodeId)
            => ReverseAdjacency.TryGetValue(nodeId, out var moves)
                ? moves
                : Array.Empty<Move>();

        /// <summary>
        /// Returns the move connecting fromNodeId to toNodeId,
        /// or null if no direct move exists between them.
        /// </summary>
        public Move? GetMove(int fromNodeId, int toNodeId)
            => GetOutboundMoves(fromNodeId)
                .FirstOrDefault(m => m.ToNodeId == toNodeId);

        /// <summary>
        /// Returns the move with the specified MoveId,
        /// or null if not found.
        /// </summary>
        public Move? GetMoveById(int moveId)
            => Moves.TryGetValue(moveId, out var move) ? move : null;

        /// <summary>
        /// Returns the node with the specified NodeId,
        /// or null if not found.
        /// </summary>
        public Node? GetNode(int nodeId)
            => Nodes.TryGetValue(nodeId, out var node) ? node : null;

        /// <summary>
        /// Returns true if the specified node exists and is a
        /// StopNode (vehicle may stop there during routing).
        /// </summary>
        public bool IsStoppable(int nodeId)
            => Nodes.TryGetValue(nodeId, out var node) && node.IsStoppable;

        /// <summary>
        /// Returns true if the specified node exists and may be used
        /// as a through-node in route calculation.
        /// DestinationOnly nodes are excluded from through-routing.
        /// </summary>
        public bool IsRouteable(int nodeId)
            => Nodes.TryGetValue(nodeId, out var node) && node.IsRouteable;

        public bool IsDestinationOnly(int nodeId)
            => Nodes.TryGetValue(nodeId, out var node)
               && node.NodeType == NodeType.DestinationOnly;

        /// <summary>
        /// Returns all AreaIds that the specified node belongs to.
        /// Returns an empty list if the node is not a member of any area.
        /// </summary>
        public IReadOnlyList<int> GetNodeAreas(int nodeId)
            => NodeAreaMembership.TryGetValue(nodeId, out var areas)
                ? areas
                : Array.Empty<int>();

        /// <summary>
        /// Returns all nodes that belong to the specified area.
        /// </summary>
        public IReadOnlyList<Node> GetAreaNodes(int areaId)
            => NodeAreaMembership
                .Where(kvp => kvp.Value.Contains(areaId))
                .Select(kvp => Nodes.TryGetValue(kvp.Key, out var n) ? n : null)
                .Where(n => n is not null)
                .Select(n => n!)
                .ToList();

        // ----------------------------------------------------------------
        // Summary
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns a human-readable summary of this graph.
        /// Used in startup logging and diagnostics.
        /// </summary>
        public string GetSummary()
            => $"RoadMapGraph v{RoadmapVersionId}: " +
               $"{Nodes.Count} nodes, " +
               $"{Moves.Count} moves, " +
               $"{Areas.Count} areas, " +
               $"loaded {LoadedAt:HH:mm:ss} UTC";
    }
}