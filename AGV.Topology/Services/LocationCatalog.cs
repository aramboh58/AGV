using System;
using System.Collections.Generic;
using System.Linq;

using AGV.Core.Entities;
using AGV.Core.Enums;

namespace AGV.Topology.Services
{
    /// <summary>
    /// One location-operation-node combination.
    /// </summary>
    public sealed record LocationEntry(
        int LocationId,
        string LocationName,
        OperationType Operation,
        int NodeId);

    /// <summary>
    /// In-memory index of the location layer (Location + LocationAssignment).
    ///
    /// Answers two questions:
    ///   - node to locations: what is this node called operationally?
    ///   - location name + operation to node: where does a mission part go?
    ///
    /// Loaded once at startup from the database and replaced atomically
    /// on reload. Immutable between loads, so reads need no locks.
    /// Load type is not modeled yet (placeholder in the database).
    /// </summary>
    public sealed class LocationCatalog
    {
        private sealed class Snapshot
        {
            public static readonly Snapshot Empty = new();

            public Dictionary<int, List<LocationEntry>> ByNode { get; init; } = new();
            public Dictionary<(string Name, OperationType Op), List<LocationEntry>> ByNameOp { get; init; } = new();
            public Dictionary<OperationType, List<LocationEntry>> ByOperation { get; init; } = new();
            public int LocationCount { get; init; }
            public int AssignmentCount { get; init; }
        }

        private volatile Snapshot _snapshot = Snapshot.Empty;

        public int LocationCount => _snapshot.LocationCount;
        public int AssignmentCount => _snapshot.AssignmentCount;

        /// <summary>
        /// Builds a new index from the database rows. For each logical id the
        /// latest version is used, and logically deleted rows are dropped.
        /// </summary>
        public void Load(
            IEnumerable<Location> locations,
            IEnumerable<LocationAssignment> assignments)
        {
            var currentLocations = locations
                .GroupBy(l => l.LocationId)
                .Select(g => g.OrderByDescending(l => l.EffectiveFromLocationVersionId).First())
                .Where(l => !l.IsDeleted)
                .ToDictionary(l => l.LocationId);

            var currentAssignments = assignments
                .GroupBy(a => a.AssignmentId)
                .Select(g => g.OrderByDescending(a => a.EffectiveFromLocationVersionId).First())
                .Where(a => !a.IsDeleted && currentLocations.ContainsKey(a.LocationId))
                .ToList();

            var byNode = new Dictionary<int, List<LocationEntry>>();
            var byNameOp = new Dictionary<(string, OperationType), List<LocationEntry>>();
            var byOperation = new Dictionary<OperationType, List<LocationEntry>>();

            foreach (var a in currentAssignments)
            {
                var loc = currentLocations[a.LocationId];
                var entry = new LocationEntry(
                    loc.LocationId, loc.LocationName,
                    (OperationType)a.OperationTypeId, a.NodeId);

                if (!byNode.TryGetValue(entry.NodeId, out var nodeList))
                    byNode[entry.NodeId] = nodeList = new List<LocationEntry>();
                nodeList.Add(entry);

                var key = (entry.LocationName, entry.Operation);
                if (!byNameOp.TryGetValue(key, out var opList))
                    byNameOp[key] = opList = new List<LocationEntry>();
                opList.Add(entry);

                if (!byOperation.TryGetValue(entry.Operation, out var allOp))
                    byOperation[entry.Operation] = allOp = new List<LocationEntry>();
                allOp.Add(entry);
            }

            _snapshot = new Snapshot
            {
                ByNode = byNode,
                ByNameOp = byNameOp,
                ByOperation = byOperation,
                LocationCount = currentLocations.Count,
                AssignmentCount = currentAssignments.Count
            };
        }

        /// <summary>
        /// All location entries at the node, ordered by name then operation.
        /// Empty if the node has no location.
        /// </summary>
        public IReadOnlyList<LocationEntry> GetForNode(int nodeId)
            => _snapshot.ByNode.TryGetValue(nodeId, out var list)
                ? list.OrderBy(e => e.LocationName, StringComparer.Ordinal)
                      .ThenBy(e => e.Operation).ToList()
                : Array.Empty<LocationEntry>();

        /// <summary>
        /// A display label for the node, or null if it has no location.
        /// Prefers an entry whose operation matches; otherwise the first
        /// entry by name. A "+N" suffix shows how many others share the node.
        /// </summary>
        public string? GetLabel(int nodeId, OperationType? preferredOperation = null)
        {
            var entries = GetForNode(nodeId);
            if (entries.Count == 0) return null;

            var chosen = preferredOperation.HasValue
                ? entries.FirstOrDefault(e => e.Operation == preferredOperation.Value)
                : null;
            chosen ??= entries[0];

            var others = entries.Select(e => e.LocationName).Distinct().Count() - 1;
            return others > 0 ? $"{chosen.LocationName} +{others}" : chosen.LocationName;
        }

        /// <summary>
        /// Every location entry that performs the operation, ordered by
        /// name. Used to generate missions from locations.
        /// </summary>
        public IReadOnlyList<LocationEntry> GetEntries(OperationType operation)
            => _snapshot.ByOperation.TryGetValue(operation, out var list)
                ? list.OrderBy(e => e.LocationName, StringComparer.Ordinal).ToList()
                : Array.Empty<LocationEntry>();

        /// <summary>
        /// Every node bound to the location name and operation.
        /// More than one node appears only when load types differ
        /// (not modeled yet).
        /// </summary>
        public IReadOnlyList<int> ResolveNodes(string locationName, OperationType operation)
            => _snapshot.ByNameOp.TryGetValue((locationName, operation), out var list)
                ? list.Select(e => e.NodeId).Distinct().ToList()
                : Array.Empty<int>();
    }
}
