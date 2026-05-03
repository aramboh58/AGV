using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;
using AGV.Core.Interfaces;
using AGV.Core.Logging;
using AGV.Core.Messages;
using AGV.Fleet.Infrastructure;
using AGV.Topology.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AGV.Fleet.Services
{
    /// <summary>
    /// Implements ITrafficManager — the five-phase traffic control cycle.
    ///
    /// Five-phase cycle (per itinerary move, to-node centric):
    ///   Phase 1 — CHECK + LOCK (atomic):
    ///     All check table checks must pass before ANY lock acquired.
    ///     Highest priority waiter wins on contention clearance.
    ///   Phase 2 — DETOUR:
    ///     Evaluated before locking past detour boundary.
    ///   Phase 3 — COMMAND:
    ///     Locked window sent to vehicle via MQTT order burst.
    ///   Phase 4 — UNLOCK:
    ///     From-node released when vehicle reports to-node arrival.
    ///
    /// Lock scope (three resource types):
    ///   Nodes (primary)
    ///   Moves/edges (implicit with node lock)
    ///   Explicit lateral moves + cascading endpoint nodes
    ///
    /// Critical invariant:
    ///   On ANY vehicle fault, ReleaseAllLocksAsync is called FIRST
    ///   before any other transfer or recovery logic runs.
    ///   Orphaned locks silently block traffic indefinitely.
    ///
    /// Deadlock detection:
    ///   One-tick ghost filter — defer one tick before confirming.
    ///   Escape node detour of lowest-cost vehicle to resolve.
    /// </summary>
    public sealed class TrafficManagerService
        : BackgroundService, ITrafficManager
    {
        private readonly RuntimeBlockingState _blockingState;
        private readonly CheckTableCache _checkTable;
        private readonly VehicleRegistry _registry;
        private readonly ChannelRegistry _channels;
        private readonly ICustomizationApi _customization;
        private readonly ILogger _logger;
        private readonly ILogger _lockLogger;
        private readonly ILogger _deadlockLogger;

        // Deadlock detection state
        // Key: (waitingVehicleId, wantedNodeId)
        // Value: tick count this contention has been observed
        private readonly Dictionary<(int, int), int>
            _contentionTicks = new();

        // Ghost filter threshold — confirm deadlock after N ticks
        private const int DeadlockConfirmTicks = 1;

        // Vehicle itinerary tracking
        // Key: vehicleId, Value: ordered list of planned node IDs
        private readonly Dictionary<int, List<int>>
            _vehicleItineraries = new();

        // Area transit event callbacks
        private Func<AreaTransitEvent, CancellationToken, Task>?
            _onAreaEntered;
        private Func<AreaTransitEvent, CancellationToken, Task>?
            _onAreaExited;

        private readonly object _trafficLock = new();

        public TrafficManagerService(
            RuntimeBlockingState blockingState,
            CheckTableCache checkTable,
            VehicleRegistry registry,
            ChannelRegistry channels,
            ICustomizationApi customization,
            ILoggerFactory loggerFactory)
        {
            _blockingState = blockingState;
            _checkTable = checkTable;
            _registry = registry;
            _channels = channels;
            _customization = customization;
            _logger = loggerFactory.CreateLogger(LogDomains.Traffic);
            _lockLogger = loggerFactory.CreateLogger(LogDomains.LockManager);
            _deadlockLogger = loggerFactory.CreateLogger(LogDomains.Deadlock);
        }

        // ----------------------------------------------------------------
        // BackgroundService
        // ----------------------------------------------------------------

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            _logger.LogInformation("TrafficManagerService starting");

            // Process deadlock detection on a timer
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500),
                    stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;

                EvaluateDeadlocks(stoppingToken);
            }

            _logger.LogInformation("TrafficManagerService stopped");
        }

        // ----------------------------------------------------------------
        // ITrafficManager — Phase 1: CHECK + LOCK
        // ----------------------------------------------------------------

        public async Task<TrafficClearance> RequestClearanceAsync(
            int vehicleId,
            int fromNodeId,
            int toNodeId,
            int moveId,
            CancellationToken cancellationToken = default)
        {
            // Check for permanent engineer blocks first
            if (_blockingState.IsNodeBlocked(toNodeId))
            {
                var block = _blockingState.GetNodeBlock(toNodeId);
                if (block is not null && !block.IsVehicleLock)
                {
                    _lockLogger.LogWarning(
                        "Node {NodeId} permanently blocked " +
                        "(reason={Reason}) — denying vehicle {VehicleId}",
                        toNodeId, block.Reason, vehicleId);
                    return TrafficClearance.Denied;
                }

                // Vehicle lock — another vehicle holds this node
                RecordContention(vehicleId, toNodeId);
                _lockLogger.LogDebug(
                    "Vehicle {VehicleId} waiting for node {NodeId} " +
                    "(held by vehicle {Holder})",
                    vehicleId, toNodeId,
                    _blockingState.GetNodeBlock(toNodeId)?.LockedByVehicleId);
                return TrafficClearance.Hold;
            }

            if (_blockingState.IsMoveBlocked(moveId))
            {
                var block = _blockingState.GetMoveBlock(moveId);
                if (block is not null && !block.IsVehicleLock)
                    return TrafficClearance.Denied;
                return TrafficClearance.Hold;
            }

            // Run check table checks
            var clearance = await RunCheckTableAsync(
                vehicleId, fromNodeId, toNodeId, moveId,
                cancellationToken);

            if (clearance == TrafficClearance.Granted)
            {
                // Clear any contention record for this vehicle/node
                ClearContention(vehicleId, toNodeId);
            }
            else if (clearance == TrafficClearance.Hold)
            {
                RecordContention(vehicleId, toNodeId);
            }

            return clearance;
        }

        // ----------------------------------------------------------------
        // ITrafficManager — Lock acquisition
        // ----------------------------------------------------------------

        public Task AcquireLocksAsync(
            int vehicleId,
            int nodeId,
            int moveId,
            CancellationToken cancellationToken = default)
        {
            var vehicle = _registry.GetById(vehicleId);
            var reason = BlockReason.Unknown;

            _blockingState.BlockNode(nodeId, new NodeBlockRecord
            {
                LockedByVehicleId = vehicleId,
                Reason = reason,
            });

            _blockingState.BlockMove(moveId, new MoveBlockRecord
            {
                LockedByVehicleId = vehicleId,
                Reason = reason,
            });

            _lockLogger.LogDebug(
                "Vehicle {VehicleId} acquired lock on " +
                "node {NodeId} / move {MoveId}",
                vehicleId, nodeId, moveId);

            return Task.CompletedTask;
        }

        // ----------------------------------------------------------------
        // ITrafficManager — Lock release (Phase 4: UNLOCK)
        // ----------------------------------------------------------------

        public Task ReleaseLocksAsync(
            int vehicleId,
            int nodeId,
            int moveId,
            CancellationToken cancellationToken = default)
        {
            _blockingState.UnblockNode(nodeId);
            _blockingState.UnblockMove(moveId);

            _lockLogger.LogDebug(
                "Vehicle {VehicleId} released lock on " +
                "node {NodeId} / move {MoveId}",
                vehicleId, nodeId, moveId);

            return Task.CompletedTask;
        }

        // ----------------------------------------------------------------
        // ITrafficManager — Orphaned lock release (CRITICAL on fault)
        // ----------------------------------------------------------------

        public Task ReleaseAllLocksAsync(
            int vehicleId,
            CancellationToken cancellationToken = default)
        {
            var releasedNodes = _blockingState
                .ReleaseAllVehicleLocks(vehicleId);
            var releasedMoves = _blockingState
                .ReleaseAllVehicleMoveLocks(vehicleId);

            // Clear any contention records for this vehicle
            lock (_trafficLock)
            {
                var keys = _contentionTicks.Keys
                    .Where(k => k.Item1 == vehicleId)
                    .ToList();
                foreach (var key in keys)
                    _contentionTicks.Remove(key);

                _vehicleItineraries.Remove(vehicleId);
            }

            _logger.LogInformation(
                "Vehicle {VehicleId} fault: released {NodeCount} node " +
                "locks and {MoveCount} move locks",
                vehicleId,
                releasedNodes.Count,
                releasedMoves.Count);

            // Signal routing engine to rebuild with updated blocking state
            _ = _channels.RoutingRebuildRequests.Writer
                .TryWrite(true);

            return Task.CompletedTask;
        }

        // ----------------------------------------------------------------
        // ITrafficManager — State queries
        // ----------------------------------------------------------------

        public IReadOnlySet<int> GetLockedNodeIds()
            => _blockingState.GetBlockedNodeIds();

        public IReadOnlySet<int> GetLockedMoveIds()
            => _blockingState.GetBlockedMoveIds();

        public IReadOnlySet<int> GetVehicleLockedNodeIds(int vehicleId)
            => _blockingState.GetVehicleLockedNodeIds(vehicleId);

        // ----------------------------------------------------------------
        // ITrafficManager — Area occupancy
        // ----------------------------------------------------------------

        public async Task UpdateAreaOccupancyAsync(
            int vehicleId,
            int nodeId,
            OccupancyUpdateType updateType,
            CancellationToken cancellationToken = default)
        {
            // Area updates happen via the road map's node-area membership
            // For now log the transition — full implementation in next phase
            _logger.LogDebug(
                "Vehicle {VehicleId} {UpdateType} node {NodeId}",
                vehicleId, updateType, nodeId);

            await Task.CompletedTask;
        }

        public int GetAreaOccupancy(int areaId)
            => _blockingState.GetAreaOccupancy(areaId);

        public void OnAreaEntered(
            Func<AreaTransitEvent, CancellationToken, Task> handler)
            => _onAreaEntered = handler;

        public void OnAreaExited(
            Func<AreaTransitEvent, CancellationToken, Task> handler)
            => _onAreaExited = handler;

        // ----------------------------------------------------------------
        // Truncate-and-append (redirect + detour)
        // ----------------------------------------------------------------

        /// <summary>
        /// Truncates a vehicle's planned itinerary at the commanded-to
        /// node and appends a new path. Used for both redirects and
        /// detours — single unified mechanism.
        ///
        /// Truncation point is the commanded-to node (NOT the locked-to
        /// node). All locks beyond the commanded-to node are released.
        /// </summary>
        public async Task TruncateAndAppendAsync(
            int vehicleId,
            int truncationNodeId,
            IReadOnlyList<int> newItinerary,
            string reason,
            CancellationToken cancellationToken = default)
        {
            // Release all locks beyond truncation point
            var lockedNodes = _blockingState
                .GetVehicleLockedNodeIds(vehicleId);

            lock (_trafficLock)
            {
                if (_vehicleItineraries.TryGetValue(vehicleId,
                    out var itinerary))
                {
                    // Find truncation point in current itinerary
                    var truncIdx = itinerary.IndexOf(truncationNodeId);
                    if (truncIdx >= 0)
                    {
                        // Release locks for nodes beyond truncation
                        for (int i = truncIdx + 1; i < itinerary.Count; i++)
                        {
                            var nodeId = itinerary[i];
                            if (lockedNodes.Contains(nodeId))
                            {
                                _blockingState.UnblockNode(nodeId);
                                _lockLogger.LogDebug(
                                    "TruncateAndAppend: released " +
                                    "node {NodeId} for vehicle {VehicleId}",
                                    nodeId, vehicleId);
                            }
                        }
                    }

                    // Replace itinerary from truncation point
                    var newFull = itinerary
                        .Take(truncIdx + 1)
                        .Concat(newItinerary)
                        .ToList();
                    _vehicleItineraries[vehicleId] = newFull;
                }
            }

            _logger.LogInformation(
                "TruncateAndAppend: vehicle {VehicleId} " +
                "truncated at node {TruncationNode} " +
                "(reason={Reason}, new path length={Length})",
                vehicleId, truncationNodeId,
                reason, newItinerary.Count);

            // Signal routing rebuild
            await _channels.RoutingRebuildRequests.Writer
                .WriteAsync(true, cancellationToken);
        }

        // ----------------------------------------------------------------
        // Deadlock detection
        // ----------------------------------------------------------------

        private void EvaluateDeadlocks(CancellationToken cancellationToken)
        {
            List<(int waitingVehicle, int wantedNode, int ticks)>
                sustained;

            lock (_trafficLock)
            {
                // Increment tick counters for all current contentions
                var keys = _contentionTicks.Keys.ToList();
                foreach (var key in keys)
                    _contentionTicks[key]++;

                // Find contentions that have persisted beyond ghost threshold
                sustained = _contentionTicks
                    .Where(kvp => kvp.Value > DeadlockConfirmTicks)
                    .Select(kvp => (kvp.Key.Item1, kvp.Key.Item2,
                                    kvp.Value))
                    .ToList();
            }

            if (sustained.Count < 2) return;

            // Check for circular dependency
            var circular = DetectCircularDependency(sustained);
            if (circular.Count > 0)
            {
                _deadlockLogger.LogError(
                    "DEADLOCK CONFIRMED: {VehicleCount} vehicles " +
                    "in circular lock dependency: {Chain}",
                    circular.Count,
                    string.Join(" → ",
                        circular.Select(v => $"V{v}")));

                // Trigger forensic flush
                _ = _channels.ForensicFlushRequests.Writer.TryWrite(
                    new ForensicFlushRequest
                    {
                        TriggerEvent = "DeadlockConfirmed",
                        PrimaryVehicleId = circular[0],
                        InvolvedVehicleIds = circular,
                    });

                // Resolve via escape node detour
                ResolveDeadlock(circular, cancellationToken);
            }
        }

        private List<int> DetectCircularDependency(
            List<(int waiting, int wanted, int ticks)> contentions)
        {
            // Build wait-for graph: vehicleA waits for node held by vehicleB
            var waitFor = new Dictionary<int, int>();

            foreach (var (waiting, wanted, _) in contentions)
            {
                var block = _blockingState.GetNodeBlock(wanted);
                if (block?.LockedByVehicleId is int holder
                    && holder != waiting)
                {
                    waitFor[waiting] = holder;
                }
            }

            // Detect cycles using DFS
            var visited = new HashSet<int>();
            var inStack = new HashSet<int>();
            var cycle = new List<int>();

            foreach (var start in waitFor.Keys)
            {
                if (FindCycle(start, waitFor, visited, inStack, cycle))
                    return cycle;
            }

            return new List<int>();
        }

        private static bool FindCycle(
            int node,
            Dictionary<int, int> graph,
            HashSet<int> visited,
            HashSet<int> inStack,
            List<int> cycle)
        {
            if (inStack.Contains(node))
            {
                cycle.Add(node);
                return true;
            }
            if (visited.Contains(node)) return false;

            visited.Add(node);
            inStack.Add(node);

            if (graph.TryGetValue(node, out var next))
            {
                if (FindCycle(next, graph, visited, inStack, cycle))
                {
                    if (!cycle.Contains(node))
                        cycle.Add(node);
                    return true;
                }
            }

            inStack.Remove(node);
            return false;
        }

        private void ResolveDeadlock(
            List<int> involvedVehicles,
            CancellationToken cancellationToken)
        {
            // Select vehicle with lowest detour cost (most opportune)
            // For Phase 1: select the vehicle with fewest locked nodes
            var candidate = involvedVehicles
                .OrderBy(v => _blockingState
                    .GetVehicleLockedNodeIds(v).Count)
                .FirstOrDefault();

            if (candidate == 0) return;

            _deadlockLogger.LogWarning(
                "Deadlock resolution: directing vehicle {VehicleId} " +
                "to escape node (Phase 1 stub — escape node " +
                "selection implemented in Phase 2)",
                candidate);

            // Phase 1 stub — escape node routing implemented
            // when escape node table is populated at map load
            // For now: trigger a mission transfer to re-route
            _ = _channels.MissionTransfers.Writer.TryWrite(
                new MissionTransfer
                {
                    FromVehicleId = candidate,
                    ToVehicleId = null,
                    Reason = TransferReason.VehicleFault,
                    ReturnToQueue = true,
                    EscalatedPriority = 0,
                    FaultedAtNodeId = _blockingState
                        .GetVehicleLockedNodeIds(candidate)
                        .FirstOrDefault(),
                    FaultedVehicleIsOnline = _registry
                        .GetById(candidate)?.IsOnline ?? false,
                    MissionContext = new MissionContext
                    {
                        MissionId = 0,
                        CurrentOrderId = string.Empty,
                        Priority = MissionPriority.Emergency,
                    }
                });
        }

        // ----------------------------------------------------------------
        // Check table evaluation
        // ----------------------------------------------------------------

        private async Task<TrafficClearance> RunCheckTableAsync(
            int vehicleId,
            int fromNodeId,
            int toNodeId,
            int moveId,
            CancellationToken cancellationToken)
        {
            var nodeChecks = _checkTable.GetNodeChecks(toNodeId);
            if (nodeChecks.Count == 0)
                return TrafficClearance.Granted;

            foreach (var check in nodeChecks)
            {
                var result = await EvaluateCheckAsync(
                    check, vehicleId, fromNodeId, toNodeId,
                    cancellationToken);

                if (result != TrafficClearance.Granted)
                {
                    _lockLogger.LogDebug(
                        "Check {CheckId} ({CheckType}) failed for " +
                        "vehicle {VehicleId} at node {NodeId}: {Result}",
                        check.CheckId, check.CheckType,
                        vehicleId, toNodeId, result);
                    return result;
                }
            }

            return TrafficClearance.Granted;
        }

        private async Task<TrafficClearance> EvaluateCheckAsync(
            CheckRecord check,
            int vehicleId,
            int fromNodeId,
            int toNodeId,
            CancellationToken cancellationToken)
        {
            return check.CheckType switch
            {
                CheckType.Node =>
                    _blockingState.IsNodeBlocked(check.GuardedResourceId)
                        ? TrafficClearance.Hold
                        : TrafficClearance.Granted,

                CheckType.Move =>
                    _blockingState.IsMoveBlocked(check.GuardedResourceId)
                        ? TrafficClearance.Hold
                        : TrafficClearance.Granted,

                CheckType.Aplus when check.AplusMacroName is not null =>
                    await _customization.EvaluateScriptCheckAsync(
                        check.AplusMacroName,
                        vehicleId,
                        fromNodeId,
                        toNodeId,
                        cancellationToken),

                // NodeWithItinerary, Distance, Itinerary:
                // Phase 1 stub — pass through for now
                _ => TrafficClearance.Granted,
            };
        }

        // ----------------------------------------------------------------
        // Contention tracking helpers
        // ----------------------------------------------------------------

        private void RecordContention(int vehicleId, int wantedNodeId)
        {
            lock (_trafficLock)
            {
                var key = (vehicleId, wantedNodeId);
                if (!_contentionTicks.ContainsKey(key))
                    _contentionTicks[key] = 0;
            }
        }

        private void ClearContention(int vehicleId, int wantedNodeId)
        {
            lock (_trafficLock)
            {
                _contentionTicks.Remove((vehicleId, wantedNodeId));
            }
        }
    }
}
