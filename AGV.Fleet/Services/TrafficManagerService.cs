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

        // A deadlock that persists is re-submitted for resolution this often
        private static readonly TimeSpan DeadlockRetryInterval =
            TimeSpan.FromSeconds(5);

        // Cycles already reported: signature (sorted vehicle ids) → last
        // request time. Touched only by the deadlock evaluation loop.
        private readonly Dictionary<string, DateTime>
            _reportedDeadlocks = new();

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

        public Task HoldNodeAsync(
            int vehicleId,
            int nodeId,
            CancellationToken cancellationToken = default)
        {
            _blockingState.BlockNode(nodeId, new NodeBlockRecord
            {
                LockedByVehicleId = vehicleId,
                Reason = BlockReason.Unknown,
            });

            _lockLogger.LogDebug(
                "Vehicle {VehicleId} holds node {NodeId}",
                vehicleId, nodeId);

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
            // Only the holder may release: a stale release must never
            // free a resource another vehicle (or an engineer block)
            // now holds.
            if (_blockingState.GetNodeBlock(nodeId)?.LockedByVehicleId
                == vehicleId)
                _blockingState.UnblockNode(nodeId);
            if (_blockingState.GetMoveBlock(moveId)?.LockedByVehicleId
                == vehicleId)
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

        public IReadOnlySet<int> GetVehicleLockedMoveIds(int vehicleId)
            => _blockingState.GetVehicleLockedMoveIds(vehicleId);

        public void ClearContentions(int vehicleId)
        {
            lock (_trafficLock)
            {
                foreach (var key in _contentionTicks.Keys
                    .Where(k => k.Item1 == vehicleId).ToList())
                    _contentionTicks.Remove(key);
            }
        }

        /// <summary>
        /// The vehicle currently holding the lock on a node, or null if
        /// the node is unlocked or blocked by an engineer block.
        /// </summary>
        public int? GetNodeHolderVehicleId(int nodeId)
            => _blockingState.GetNodeBlock(nodeId)?.LockedByVehicleId;

        /// <summary>
        /// Nodes this vehicle is currently waiting on (contention
        /// recorded and not yet granted). Used by the dashboard popup.
        /// </summary>
        public IReadOnlyList<int> GetVehicleWaitedNodeIds(int vehicleId)
        {
            lock (_trafficLock)
            {
                return _contentionTicks.Keys
                    .Where(k => k.Item1 == vehicleId)
                    .Select(k => k.Item2)
                    .ToList()
                    .AsReadOnly();
            }
        }

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

            // 9/20 diagnostic session: released-node logging moved
            // outside the lock (was inside a loop inside the lock —
            // worse than a single call, since it could fire multiple
            // times per lock acquisition). See MissionQueueService.cs
            // for the full rationale — a stuck/slow log write of any
            // kind can no longer hold this application lock hostage.
            var releasedNodeIds = new List<int>();

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
                                releasedNodeIds.Add(nodeId);
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

            foreach (var nodeId in releasedNodeIds)
            {
                _lockLogger.LogDebug(
                    "TruncateAndAppend: released " +
                    "node {NodeId} for vehicle {VehicleId}",
                    nodeId, vehicleId);
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

        public IReadOnlyList<int> GetBlockedVehicleIds()
        {
            lock (_trafficLock)
            {
                return _contentionTicks.Keys
                    .Select(k => k.Item1)
                    .Distinct()
                    .ToList()
                    .AsReadOnly();
            }
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

            var cycles = sustained.Count >= 2
                ? DetectCircularDependencies(sustained)
                : new List<List<int>>();

            var now = DateTime.UtcNow;
            var current = new HashSet<string>();

            foreach (var cycle in cycles)
            {
                var signature = string.Join(",", cycle.OrderBy(v => v));
                current.Add(signature);

                var chain = string.Join(" → ", cycle.Select(v => $"V{v}"));

                if (!_reportedDeadlocks.TryGetValue(signature, out var last))
                {
                    // First sighting: report once
                    _deadlockLogger.LogError(
                        "DEADLOCK CONFIRMED: {VehicleCount} vehicles " +
                        "in circular lock dependency: {Chain}",
                        cycle.Count, chain);

                    _ = _channels.ForensicFlushRequests.Writer.TryWrite(
                        new ForensicFlushRequest
                        {
                            TriggerEvent = "DeadlockConfirmed",
                            PrimaryVehicleId = cycle[0],
                            InvolvedVehicleIds = cycle,
                        });

                    _ = _channels.Alerts.Writer.TryWrite(
                        new AlertUpdate(
                            AlertType.Deadlock,
                            cycle[0],
                            $"Deadlock detected: {chain}",
                            now));

                    _reportedDeadlocks[signature] = now;
                    DeadlockMetrics.RecordDetected();
                    RequestResolution(cycle);
                }
                else if (now - last >= DeadlockRetryInterval)
                {
                    // Still deadlocked: ask again (quietly)
                    _deadlockLogger.LogWarning(
                        "Deadlock persists: {Chain} — retrying resolution",
                        chain);
                    _reportedDeadlocks[signature] = now;
                    RequestResolution(cycle);
                }
            }

            // Forget cycles that no longer exist
            foreach (var signature in _reportedDeadlocks.Keys
                .Where(k => !current.Contains(k)).ToList())
            {
                _reportedDeadlocks.Remove(signature);
                _deadlockLogger.LogInformation(
                    "Deadlock cleared: vehicles {Vehicles}", signature);
            }
        }

        /// <summary>
        /// Finds every circular wait. Each vehicle waits on at most one
        /// holder, so the wait-for graph has out-degree 1 and a cycle is
        /// found by following the chain until a vehicle repeats. Only the
        /// vehicles in the cycle are returned, not those queued behind it.
        /// </summary>
        private List<List<int>> DetectCircularDependencies(
            List<(int waiting, int wanted, int ticks)> contentions)
        {
            // Wait-for graph: vehicleA waits for a node held by vehicleB
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

            var cycles = new List<List<int>>();
            var done = new HashSet<int>();

            foreach (var start in waitFor.Keys)
            {
                if (done.Contains(start)) continue;

                var path = new List<int>();
                var position = new Dictionary<int, int>();
                var current = start;

                while (!done.Contains(current))
                {
                    if (position.TryGetValue(current, out var at))
                    {
                        cycles.Add(path.GetRange(at, path.Count - at));
                        break;
                    }

                    position[current] = path.Count;
                    path.Add(current);

                    if (!waitFor.TryGetValue(current, out var next)) break;
                    current = next;
                }

                foreach (var v in path) done.Add(v);
            }

            return cycles;
        }

        private void RequestResolution(List<int> cycle)
        {
            // The fleet manager owns routing: it re-routes one vehicle
            // around the nodes the others hold.
            _ = _channels.DeadlockRequests.Writer.TryWrite(
                new DeadlockResolutionRequest { VehicleIds = cycle });
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
