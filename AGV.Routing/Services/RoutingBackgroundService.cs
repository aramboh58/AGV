using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Logging;
using AGV.Topology.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AGV.Routing.Services
{
    /// <summary>
    /// Hosted background service that manages the routing engine lifecycle.
    ///
    /// Responsibilities:
    ///   1. Subscribes to topology version change events
    ///   2. Builds a new PoseExpandedGraph when topology changes
    ///   3. Atomically activates the new graph in the routing engine
    ///   4. Rebuilds the graph with updated blocking state when
    ///      runtime blocks are added or removed
    ///
    /// The pose-expanded graph is built on a background thread to avoid
    /// blocking the topology background service. Graph construction is
    /// CPU-bound (iterating all nodes × heading buckets × moves) and
    /// may take a few hundred milliseconds on large topologies.
    ///
    /// During graph construction, the routing engine retains the
    /// previous graph — routing continues uninterrupted. The new
    /// graph is only activated after construction completes.
    /// </summary>
    public sealed class RoutingBackgroundService : BackgroundService
    {
        private readonly AStarRoutingEngine _routingEngine;
        private readonly TopologyVersionManager _versionManager;
        private readonly RuntimeBlockingState _blockingState;
        private readonly TurnCostTable _turnCosts;
        private readonly ILogger _logger;
        private readonly RoadMapGraphHolder _roadMapHolder;

        // Channel for signaling graph rebuild requests
        private readonly System.Threading.Channels.Channel<RebuildRequest>
            _rebuildChannel;

        public RoutingBackgroundService(
            AStarRoutingEngine routingEngine,
            TopologyVersionManager versionManager,
            RuntimeBlockingState blockingState,
            TurnCostTable turnCosts,
            RoadMapGraphHolder roadMapHolder,
            ILoggerFactory loggerFactory)
        {
            _routingEngine = routingEngine
                ?? throw new ArgumentNullException(nameof(routingEngine));
            _versionManager = versionManager
                ?? throw new ArgumentNullException(nameof(versionManager));
            _blockingState = blockingState
                ?? throw new ArgumentNullException(nameof(blockingState));
            _turnCosts = turnCosts
                ?? throw new ArgumentNullException(nameof(turnCosts));
            _roadMapHolder = roadMapHolder;
            _logger = loggerFactory.CreateLogger(LogDomains.Router);

            _rebuildChannel = System.Threading.Channels.Channel
                .CreateBounded<RebuildRequest>(
                    new System.Threading.Channels.BoundedChannelOptions(10)
                    {
                        FullMode = System.Threading.Channels.BoundedChannelFullMode
                            .DropOldest
                    });
        }

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "RoutingBackgroundService starting");

            // Subscribe to topology version changes
            _versionManager.TopologyVersionChanged += OnTopologyVersionChanged;

            try
            {
                // If topology is already loaded, build initial graph
                if (_versionManager.IsLoaded)
                {
                    _activeRoadMap = _roadMapHolder.GetRequired();
                    await RequestRebuildAsync(RebuildReason.InitialLoad);
                }

                // Process rebuild requests
                await foreach (var request in
                    _rebuildChannel.Reader.ReadAllAsync(stoppingToken))
                {
                    await BuildAndActivateGraphAsync(request, stoppingToken);
                }
            }
            finally
            {
                _versionManager.TopologyVersionChanged -= OnTopologyVersionChanged;
                _logger.LogInformation("RoutingBackgroundService stopped");
            }
        }

        /// <summary>
        /// Requests a graph rebuild due to blocking state change.
        /// Called by TrafficManagerService when nodes/moves are
        /// blocked or unblocked — the routing engine needs an
        /// updated graph that excludes newly blocked resources.
        /// </summary>
        public async Task RequestBlockingStateRebuildAsync()
            => await RequestRebuildAsync(RebuildReason.BlockingStateChanged);

        // ----------------------------------------------------------------
        // Private
        // ----------------------------------------------------------------

        private RoadMapGraph? _activeRoadMap;

        private void OnTopologyVersionChanged(
            object? sender,
            TopologyVersionChangedEventArgs e)
        {
            _activeRoadMap = e.Graph;
            _ = RequestRebuildAsync(RebuildReason.TopologyVersionChanged);
            _logger.LogInformation(
                "Topology version changed to v{VersionId} — " +
                "routing graph rebuild queued",
                e.NewVersion.VersionId);
        }

        private async Task BuildAndActivateGraphAsync(
            RebuildRequest request,
            CancellationToken cancellationToken)
        {
            if (!_versionManager.IsLoaded ||
                _versionManager.ActiveVersion is null)
            {
                _logger.LogDebug(
                    "Graph rebuild requested ({Reason}) " +
                    "but no topology loaded yet — skipping",
                    request.Reason);
                return;
            }

            var roadMap = _activeRoadMap;
            if (roadMap is null)
            {
                _logger.LogWarning(
                    "Graph rebuild requested ({Reason}) " +
                    "but no RoadMapGraph available — skipping",
                    request.Reason);
                return;
            }

            _logger.LogInformation(
                "Building pose-expanded graph " +
                "(reason: {Reason}, topology: v{VersionId})",
                request.Reason,
                _versionManager.ActiveVersion.VersionId);

            try
            {
                var blockedNodes = _blockingState.GetBlockedNodeIds();
                var blockedMoves = _blockingState.GetBlockedMoveIds();

                var graph = await Task.Run(() =>
                    new PoseExpandedGraph(
                        roadMap,
                        _turnCosts,
                        blockedNodes,
                        blockedMoves),
                    cancellationToken);

                _routingEngine.ActivateGraph(graph);

                _logger.LogInformation(
                    "Pose-expanded graph rebuild complete " +
                    "(reason: {Reason})",
                    request.Reason);
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex,
                    "Failed to build pose-expanded graph " +
                    "(reason: {Reason})",
                    request.Reason);
            }
        }
        private async Task RequestRebuildAsync(RebuildReason reason)
        {
            await _rebuildChannel.Writer.WriteAsync(
                new RebuildRequest(reason));
        }

        // ----------------------------------------------------------------
        // Supporting types
        // ----------------------------------------------------------------

        private enum RebuildReason
        {
            InitialLoad,
            TopologyVersionChanged,
            BlockingStateChanged
        }

        private sealed record RebuildRequest(RebuildReason Reason);
    }
}