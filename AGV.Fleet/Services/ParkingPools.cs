using System;
using System.Collections.Generic;
using System.Linq;

using AGV.Core.Enums;
using AGV.Topology.Services;

namespace AGV.Fleet.Services
{
    /// <summary>
    /// The sets of locations a vehicle can be parked or charged at,
    /// derived from the location layer (Location + LocationAssignment):
    ///
    ///   Chargers     : nodes of Charge / ParkCharge locations whose name
    ///                  ends in _AUTO (automatic contact).
    ///   ManualChargers: the _MANUAL ones - not used for automatic orders.
    ///   QueueNode    : the single head node of the charge/park rail queue
    ///                  (location name starts with Park_Queue).
    ///   ParkOnly     : every other routable Park node that is not a
    ///                  charger and not the queue node.
    ///
    /// A node the router cannot route to (NoStopNode) is never listed.
    /// Loaded at startup after the location catalog.
    /// </summary>
    public sealed class ParkingPools
    {
        private const string QueuePrefix = "Park_Queue";

        private volatile Snapshot _s = new(
            Array.Empty<int>(), Array.Empty<int>(),
            Array.Empty<int>(), null);

        private sealed record Snapshot(
            IReadOnlyList<int> Chargers,
            IReadOnlyList<int> ManualChargers,
            IReadOnlyList<int> ParkOnly,
            int? QueueNode);

        public IReadOnlyList<int> ChargerNodes => _s.Chargers;
        public IReadOnlyList<int> ManualChargerNodes => _s.ManualChargers;
        public IReadOnlyList<int> ParkOnlyNodes => _s.ParkOnly;
        public int? QueueNodeId => _s.QueueNode;

        public void Load(
            LocationCatalog catalog, ISet<int> routableNodeIds)
        {
            var chargeEntries = catalog.GetEntries(OperationType.Charge)
                .Concat(catalog.GetEntries(OperationType.ParkCharge))
                .ToList();

            static bool Named(LocationEntry e, string suffix)
                => e.LocationName.EndsWith(
                    suffix, StringComparison.OrdinalIgnoreCase);

            var chargers = chargeEntries
                .Where(e => Named(e, "_AUTO")
                            && routableNodeIds.Contains(e.NodeId))
                .Select(e => e.NodeId).Distinct().OrderBy(n => n).ToList();
            var manual = chargeEntries
                .Where(e => Named(e, "_MANUAL")
                            && routableNodeIds.Contains(e.NodeId))
                .Select(e => e.NodeId).Distinct().OrderBy(n => n).ToList();

            var parkEntries = catalog.GetEntries(OperationType.Park);

            var queueNodes = parkEntries
                .Where(e => e.LocationName.StartsWith(
                    QueuePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.NodeId).Distinct().ToList();
            int? queueNode = queueNodes.Count == 1 ? queueNodes[0] : null;

            var taken = new HashSet<int>(chargers);
            taken.UnionWith(manual);
            taken.UnionWith(queueNodes);
            // Any node that has a charge operation is not park-only,
            // routable or not.
            taken.UnionWith(chargeEntries.Select(e => e.NodeId));

            var parkOnly = parkEntries
                .Where(e => !e.LocationName.StartsWith(
                                QueuePrefix, StringComparison.OrdinalIgnoreCase)
                            && routableNodeIds.Contains(e.NodeId)
                            && !taken.Contains(e.NodeId))
                .Select(e => e.NodeId).Distinct().OrderBy(n => n).ToList();

            _s = new Snapshot(chargers, manual, parkOnly, queueNode);
        }

        public string Describe()
        {
            var s = _s;
            var queue = s.QueueNode.HasValue
                ? $"node {s.QueueNode.Value}"
                : "NOT FOUND (needs exactly one Park_Queue node)";
            return
                $"Parking pools: {s.Chargers.Count} auto chargers " +
                $"[{string.Join(", ", s.Chargers)}], " +
                $"{s.ManualChargers.Count} manual chargers, " +
                $"{s.ParkOnly.Count} park-only [{string.Join(", ", s.ParkOnly)}], " +
                $"queue {queue}";
        }
    }
}
