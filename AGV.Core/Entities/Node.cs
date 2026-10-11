using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;
using AGV.Core.ValueObjects;

namespace AGV.Core.Entities
{
    /// <summary>
    /// Represents a node on the AGV road network.
    /// 
    /// Nodes are the atomic waypoints of the topology. A node's type
    /// determines how the routing engine and vehicle may use it:
    ///   StopNode      — vehicle may stop here during route execution
    ///   NoStopNode    — on the transit path but vehicle may not stop
    ///   DestinationOnly — has moves in/out but cannot be routed through
    /// 
    /// Arrival heading is NOT stored on the node — it is the EndHeading
    /// of the incoming Move. Multiple incoming moves may each have a
    /// different arrival heading at the same node.
    /// 
    /// Runtime blocking is tracked separately in NodeBlock (not here).
    /// This entity represents the static versioned topology definition.
    /// </summary>
    public class Node
    {
        /// <summary>
        /// Surrogate primary key for the physical database row.
        /// </summary>
        public int NodeRecordId { get; private set; }

        /// <summary>
        /// Logical stable identity of this node across topology versions.
        /// This is the ID used throughout the application and in VDA 5050
        /// order messages.
        /// </summary>
        public int NodeId { get; private set; }

        /// <summary>
        /// The roadmap version from which this node record is effective.
        /// Delta versioning — a new record is created only when this
        /// node's definition changes.
        /// </summary>
        public int EffectiveFromVersionId { get; private set; }

        /// <summary>
        /// True if this node has been logically deleted in this version.
        /// </summary>
        public bool IsDeleted { get; private set; }

        /// <summary>
        /// Optional human-readable name for this node.
        /// Used in tooling, dashboards, and diagnostics.
        /// </summary>
        public string? NodeName { get; private set; }

        /// <summary>
        /// Physical position of this node in the facility coordinate system.
        /// Centimeters to 0.01cm precision. Z is true elevation.
        /// </summary>
        public Coordinate Position { get; private set; }

        /// <summary>
        /// Determines how this node may be used during route calculation
        /// and vehicle execution.
        /// </summary>
        public NodeType NodeType { get; private set; }

        /// <summary>
        /// The map identifier for the coordinate space this node belongs to.
        /// For single-floor facilities this is a constant value (e.g. "FLOOR_1").
        /// Changes at elevator transition nodes in multi-floor applications.
        /// Corresponds to VDA 5050 nodePosition.mapId.
        /// </summary>
        public string MapId { get; private set; }

        // Private constructor for EF Core
        private Node()
        {
            Position = null!;
            MapId = null!;
        }

        public Node(
            int nodeId,
            int effectiveFromVersionId,
            Coordinate position,
            NodeType nodeType,
            string mapId,
            string? nodeName = null)
        {
            if (nodeId <= 0)
                throw new ArgumentOutOfRangeException(nameof(nodeId),
                    "NodeId must be a positive integer.");

            if (effectiveFromVersionId <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(effectiveFromVersionId),
                    "EffectiveFromVersionId must be a positive integer.");

            if (string.IsNullOrWhiteSpace(mapId))
                throw new ArgumentException(
                    "MapId cannot be null or empty.", nameof(mapId));

            NodeId = nodeId;
            EffectiveFromVersionId = effectiveFromVersionId;
            Position = position
                ?? throw new ArgumentNullException(nameof(position));
            NodeType = nodeType;
            MapId = mapId;
            NodeName = nodeName;
            IsDeleted = false;
        }

        /// <summary>
        /// Marks this node as deleted in the current topology version.
        /// </summary>
        public void MarkDeleted() => IsDeleted = true;

        /// <summary>
        /// Returns true if vehicles may stop on this node.
        /// </summary>
        public bool IsStoppable
            => NodeType == NodeType.StopNode || NodeType == NodeType.DestinationOnly;

        /// <summary>
        /// Returns true if the routing engine may use this node as a
        /// through-node when calculating paths (not just as a destination).
        /// </summary>
        public bool IsRouteable => !IsDeleted;

        public override string ToString()
            => $"Node[{NodeId}] {NodeName ?? "(unnamed)"} " +
               $"@ {Position} Type={NodeType} Map={MapId}";
    }
}