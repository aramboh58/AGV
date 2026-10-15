using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AGV.Core.Entities;
using AGV.Core.Enums;
using AGV.Core.Interfaces;
using AGV.Core.Logging;
using AGV.Core.Messages;
using AGV.Topology.Services;
using Microsoft.Extensions.Logging;

namespace AGV.Fleet.Services
{
    /// <summary>
    /// Traffic settings. Loaded from appsettings.json section "Traffic".
    /// </summary>
    public sealed class TrafficOptions
    {
        public const string SectionName = "Traffic";

        /// <summary>
        /// How many nodes AHEAD of the vehicle's current node the host
        /// locks and releases to the vehicle at any time (the sliding
        /// lock window). Default 4. Set to 0 to disable windowing: the
        /// whole route is then sent at once with no traffic locking.
        /// </summary>
        public int LockWindowNodes { get; set; } = 4;
    }

    /// <summary>
    /// Drives the sliding lock window ("drip feed") for each vehicle
    /// executing an order.
    ///
    ///   Check + lock : for each to-node up to LockWindowNodes ahead of
    ///                  the vehicle, request clearance and, if granted,
    ///                  lock the node and the move into it. A Hold stops
    ///                  extension; it is retried on the vehicle's next
    ///                  state update.
    ///   Command      : the locked window is released to the vehicle as
    ///                  an order (same orderId, increasing orderUpdateId);
    ///                  nodes beyond the window are sent unreleased.
    ///   Unlock       : when the vehicle reports arrival at a to-node, the
    ///                  from-node and move behind it are unlocked and the
    ///                  window slides forward.
    ///
    /// A vehicle holds locks only while executing an order: on arrival at
    /// the final node of the order all remaining locks are released.
    ///
    /// All operations are serialized through one gate so that
    /// check + lock is atomic across vehicles.
    /// </summary>
    public sealed class RouteWindowManager
    {
        private readonly ITrafficManager _traffic;
        private readonly IVehicleAdapter _adapter;
        private readonly RoadMapGraphHolder _graphHolder;
        private readonly TrafficOptions _options;
        private readonly ILogger _logger;

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConcurrentDictionary<int, Itinerary>
            _itineraries = new();

        public RouteWindowManager(
            ITrafficManager traffic,
            IVehicleAdapter adapter,
            RoadMapGraphHolder graphHolder,
            TrafficOptions options,
            ILoggerFactory loggerFactory)
        {
            _traffic = traffic;
            _adapter = adapter;
            _graphHolder = graphHolder;
            _options = options;
            _logger = loggerFactory.CreateLogger(LogDomains.Traffic);
        }

        public bool Enabled => _options.LockWindowNodes > 0;

        /// <summary>
        /// Starts windowed execution of a route and sends the first
        /// order. Returns false if windowing is disabled or the route
        /// cannot be windowed; the caller then sends the whole route.
        /// </summary>
        public async Task<bool> BeginAsync(
            string serialNumber,
            int vehicleId,
            string orderId,
            IEnumerable<int> routeNodeIds,
            IEnumerable<int>? routeMoveIds,
            CancellationToken cancellationToken)
        {
            if (!Enabled) return false;

            var nodes = routeNodeIds.ToList();
            if (nodes.Count < 2) return false;

            var moves = ResolveMoves(nodes, routeMoveIds?.ToList());
            if (moves is null)
            {
                _logger.LogWarning(
                    "Route window: could not resolve moves for vehicle " +
                    "{VehicleId} order {OrderId} - sending full route",
                    vehicleId, orderId);
                return false;
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                // A new order replaces any itinerary still in progress.
                if (_itineraries.TryRemove(vehicleId, out _))
                {
                    await _traffic.ReleaseAllLocksAsync(
                        vehicleId, cancellationToken);
                }

                var itinerary = new Itinerary(
                    serialNumber, vehicleId, orderId, nodes, moves);
                _itineraries[vehicleId] = itinerary;

                _logger.LogInformation(
                    "Route window: vehicle {VehicleId} order {OrderId} " +
                    "- {Nodes} nodes, window {Window} ahead",
                    vehicleId, orderId, nodes.Count,
                    _options.LockWindowNodes);

                await ExtendAsync(itinerary, forceSend: true,
                    cancellationToken);
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Called on every state update from a vehicle. Slides the
        /// window when the vehicle has reached a new node, and retries
        /// extension if it is waiting on a Hold.
        /// </summary>
        public async Task OnVehicleStateAsync(
            int vehicleId,
            int lastNodeId,
            CancellationToken cancellationToken)
        {
            // Fast path - idle vehicles have no itinerary.
            if (!_itineraries.ContainsKey(vehicleId)) return;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!_itineraries.TryGetValue(vehicleId, out var it))
                    return;

                var idx = IndexOfFrom(it.Nodes, lastNodeId, it.ReachedIndex);
                if (idx > it.ReachedIndex)
                {
                    // Unlock phase: free the from-node and move behind
                    // each node the vehicle has now reached.
                    for (var i = it.ReachedIndex; i < idx; i++)
                    {
                        await _traffic.ReleaseLocksAsync(
                            vehicleId, it.Nodes[i], it.Moves[i],
                            cancellationToken);
                    }
                    it.ReachedIndex = idx;
                }

                if (it.ReachedIndex >= it.Nodes.Count - 1)
                {
                    // Arrived at the final node: release what remains.
                    await _traffic.ReleaseLocksAsync(
                        vehicleId, it.Nodes[^1], it.Moves[^1],
                        cancellationToken);
                    _itineraries.TryRemove(vehicleId, out _);
                    _logger.LogDebug(
                        "Route window: vehicle {VehicleId} order " +
                        "{OrderId} complete", vehicleId, it.OrderId);
                    return;
                }

                await ExtendAsync(it, forceSend: false, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Forgets a vehicle's itinerary. The caller is responsible for
        /// releasing its locks (ReleaseAllLocksAsync).
        /// </summary>
        public void Cancel(int vehicleId)
            => _itineraries.TryRemove(vehicleId, out _);

        // ----------------------------------------------------------------
        // Check + lock, then command
        // ----------------------------------------------------------------

        private async Task ExtendAsync(
            Itinerary it,
            bool forceSend,
            CancellationToken cancellationToken)
        {
            var target = Math.Min(
                it.ReachedIndex + _options.LockWindowNodes,
                it.Nodes.Count - 1);

            while (it.LockedIndex < target)
            {
                var i = it.LockedIndex + 1;
                var clearance = await _traffic.RequestClearanceAsync(
                    it.VehicleId, it.Nodes[i - 1], it.Nodes[i],
                    it.Moves[i - 1], cancellationToken);

                if (clearance == TrafficClearance.Granted)
                {
                    await _traffic.AcquireLocksAsync(
                        it.VehicleId, it.Nodes[i], it.Moves[i - 1],
                        cancellationToken);
                    it.LockedIndex = i;
                    continue;
                }

                if (clearance == TrafficClearance.Denied
                    && !it.DeniedLogged)
                {
                    it.DeniedLogged = true;
                    _logger.LogWarning(
                        "Route window: vehicle {VehicleId} denied node " +
                        "{NodeId} (permanent block) - waiting",
                        it.VehicleId, it.Nodes[i]);
                }

                break; // Hold or Denied: retry on next state update
            }

            if (forceSend || it.LockedIndex > it.ReleasedIndex)
            {
                it.ReleasedIndex = it.LockedIndex;
                await SendOrderAsync(it, cancellationToken);
            }
        }

        private async Task SendOrderAsync(
            Itinerary it,
            CancellationToken cancellationToken)
        {
            var order = new VehicleOrder
            {
                OrderId = it.OrderId,
                OrderUpdateId = it.LastOrder is null
                    ? 1
                    : it.LastOrder.OrderUpdateId + 1,
                Nodes = it.Nodes
                    .Select((nodeId, i) => new OrderNode
                    {
                        NodeId = nodeId.ToString(),
                        SequenceId = i,
                        Released = i <= it.ReleasedIndex,
                        X = 0m,
                        Y = 0m,
                        MapId = string.Empty,
                        Actions = Array.Empty<OrderAction>(),
                    })
                    .ToList(),
                Edges = Array.Empty<OrderEdge>().ToList(),
            };

            await _adapter.SendOrderAsync(
                it.Serial, order, cancellationToken);
            it.LastOrder = order;

            _logger.LogDebug(
                "Route window: vehicle {VehicleId} order {OrderId} " +
                "released {Released}/{Total} (reached {Reached})",
                it.VehicleId, it.OrderId, it.ReleasedIndex + 1,
                it.Nodes.Count, it.ReachedIndex);
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private List<int>? ResolveMoves(
            List<int> nodes, List<int>? supplied)
        {
            if (supplied is not null
                && supplied.Count == nodes.Count - 1
                && supplied.All(m => m > 0))
                return supplied;

            var graph = _graphHolder.Graph;
            if (graph is null) return null;

            var result = new List<int>(nodes.Count - 1);
            for (var i = 0; i < nodes.Count - 1; i++)
            {
                var move = graph.GetMove(nodes[i], nodes[i + 1]);
                if (move is null) return null;
                result.Add(move.MoveId);
            }
            return result;
        }

        private static int IndexOfFrom(
            List<int> nodes, int nodeId, int startIndex)
        {
            for (var i = startIndex; i < nodes.Count; i++)
                if (nodes[i] == nodeId) return i;
            return -1;
        }

        private sealed class Itinerary
        {
            public string Serial { get; }
            public int VehicleId { get; }
            public string OrderId { get; }
            public List<int> Nodes { get; }
            // Moves[i] is the move from Nodes[i] to Nodes[i+1]
            public List<int> Moves { get; }

            // Highest node index the vehicle has reported reaching
            public int ReachedIndex { get; set; }
            // Highest node index locked (node + the move into it)
            public int LockedIndex { get; set; }
            // Highest node index released to the vehicle
            public int ReleasedIndex { get; set; }

            public VehicleOrder? LastOrder { get; set; }
            public bool DeniedLogged { get; set; }

            public Itinerary(
                string serial, int vehicleId, string orderId,
                List<int> nodes, List<int> moves)
            {
                Serial = serial;
                VehicleId = vehicleId;
                OrderId = orderId;
                Nodes = nodes;
                Moves = moves;
            }
        }
    }
}
