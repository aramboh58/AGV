using AGV.Core.Enums;
using AGV.Core.Logging;
using AGV.Core.Messages;
using AGV.Dashboard.Hubs;
using AGV.Fleet.Infrastructure;
using AGV.Fleet.Services;
using AGV.Topology.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Collections.Concurrent;

namespace AGV.Dashboard.Services
{
    /// <summary>
    /// Hosted service that bridges the ChannelRegistry to SignalR, and
    /// also to in-process C# events for consumers running in the same
    /// process — see the "In-process event API" region below.
    ///
    /// Reads VehiclePositionUpdates and VehicleStateUpdates from the
    /// ChannelRegistry (written by SimulatedVehicleAdapter or
    /// MqttVehicleAdapter) and broadcasts them to all connected
    /// browser clients via FleetHub.
    ///
    /// Also tracks mission counters and simulation clock for the
    /// dashboard counters panel, and streams live detail for whichever
    /// vehicle currently has its popup open.
    ///
    /// ARCHITECTURE NOTE (9/1): this class deliberately avoids blocking
    /// synchronization (`lock`/Monitor) on any state shared between its
    /// several concurrent background loops. Diagnosed via dotnet-counters
    /// (8/31 session, see ToDo10.md #11): under sustained load, Monitor
    /// Lock Contention Count stayed nonzero almost continuously, and
    /// ThreadPool Thread Count stalled at 8-9 while Queue Length grew
    /// into the hundreds — several async loops (positions, states,
    /// selected-vehicle detail, etc.) "lapping" each other and backing
    /// up on shared locked resources. Raising the ThreadPool floor and
    /// enabling Server GC were tried and reverted — this hardware is a
    /// 2-core/2-logical-processor machine, where more threads just means
    /// more contention for the same 2 cores, not more real parallelism;
    /// Server GC's per-core-heap model is also a poor fit below a
    /// handful of cores. The actual fix is eliminating the blocking
    /// synchronization itself:
    ///   - `latest` (state batching): was `Dictionary` + `lock` — now a
    ///     ConcurrentDictionary reference swapped atomically via
    ///     Interlocked.Exchange (lock-free "grab everything and reset"
    ///     pattern — no Monitor involved at all).
    ///   - `_selectedVehicleId`: REMOVED 9/28 (#11b). It was a single
    ///     process-wide value, so one browser's click changed the popup
    ///     for every connected browser. Selection is now per-circuit,
    ///     owned by each Dashboard.razor instance, which refreshes its
    ///     own popup via GetVehicleDetail while the popup is open.
    ///   - `_socHistory`: was a plain, UNSYNCHRONIZED Dictionary<int,
    ///     Queue<...>> — written by one loop, read from at least two
    ///     other call sites (BroadcastSelectedVehicleDetailAsync's loop
    ///     AND GetVehicleDetail called directly from Dashboard.razor's
    ///     OnVehicleClicked) with NO protection at all. This was a real
    ///     thread-safety bug, not just a performance issue — concurrent
    ///     unprotected Dictionary access can throw or corrupt state,
    ///     and such failures would be silently swallowed by this
    ///     class's broad catch(Exception) handlers. Now a
    ///     ConcurrentDictionary<int, ConcurrentQueue<...>>.
    /// </summary>
    public sealed class DashboardBroadcaster : BackgroundService
    {
        private readonly ChannelRegistry _channels;
        private readonly IHubContext<FleetHub> _hub;
        private readonly ILogger _logger;
        private readonly VehicleRegistry _registry;
        private readonly TrafficManagerService _traffic;
        private readonly ActiveMissionRegistry _activeMissions;
        private readonly RoadMapGraphHolder _graphHolder;
        private readonly LocationCatalog _locations;

        // Was Dictionary<int, Queue<(DateTime,decimal)>> with NO lock —
        // genuinely unsynchronized concurrent access. See class-level
        // ARCHITECTURE NOTE above.
        private readonly ConcurrentDictionary<int, ConcurrentQueue<(DateTime Time, decimal Soc)>> _socHistory = new();
        private const int SocHistoryMaxPoints = 15;

        // Mission counters — incremented by reading the dispatch channel
        private int _enqueued;
        private int _dispatched;
        private int _completed;

        // Sim clock — updated via SimulationEngineService event if available
        private TimeSpan _simTime = TimeSpan.Zero;
        private decimal _speedFactor = 60m;
        private int _tickCount;
        private DateTime _simStartTime = DateTime.UtcNow;

        // ----------------------------------------------------------------
        // In-process event API — see ARCHITECTURE NOTE above.
        // Each event mirrors one of the SignalR broadcasts below and is
        // raised on whichever background thread is running the relevant
        // broadcast loop (NOT the Blazor render thread) — subscribers
        // must marshal back via InvokeAsync/StateHasChanged themselves.
        // ----------------------------------------------------------------

        public event Action<List<VehicleStateDto>>? VehicleStatesUpdated;
        public event Action<List<VehiclePositionDto>>? VehiclePositionsUpdated;
        public event Action<MissionCounterUpdate>? MissionCountersUpdated;
        public event Action<SimClockUpdate>? SimClockUpdated;
        public event Action<List<AlertUpdate>>? AlertsUpdated;
        public event Action<int, int?, IReadOnlyList<int>>? VehicleMissionUpdated;

        public DashboardBroadcaster(
            IHubContext<FleetHub> hub,
            ChannelRegistry channels,
            VehicleRegistry registry,
            TrafficManagerService traffic,
            ActiveMissionRegistry activeMissions,
            RoadMapGraphHolder graphHolder,
            LocationCatalog locations,
            ILoggerFactory loggerFactory)
        {
            _hub = hub;
            _channels = channels;
            _registry = registry;
            _traffic = traffic;
            _activeMissions = activeMissions;
            _graphHolder = graphHolder;
            _locations = locations;
            _logger = loggerFactory.CreateLogger(LogDomains.Dashboard);
        }

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "DashboardBroadcaster starting");

            // Run position, state, and mission broadcast loops concurrently
            await Task.WhenAll(
                BroadcastPositionsAsync(stoppingToken),
                BroadcastStatesAsync(stoppingToken),
                BroadcastMissionCountersAsync(stoppingToken),
                BroadcastSimClockAsync(stoppingToken),
                BroadcastAlertsAsync(stoppingToken),
                BroadcastMissionUpdatesAsync(stoppingToken));
        }

        // ----------------------------------------------------------------
        // Position updates — high frequency, direct from sim tick.
        // `latest` here is used entirely within this single loop/thread
        // (drained via TryRead then sent, all in one method, no other
        // method touches it) — no lock was ever needed here, and none
        // is added now. Left unchanged from prior sessions.
        // ----------------------------------------------------------------

        private async Task BroadcastPositionsAsync(CancellationToken ct)
        {
            // Throttle: batch position updates, broadcast at 2Hz max.
            // NOTE (9/7): re-testing this reduction (previously tried
            // 8/24-25 with no apparent benefit, then reverted) now that
            // Console.Out lock contention has been eliminated (was
            // 82.6% of all contention, now 0%). That fix may have been
            // masking any real effect a rate reduction has on the
            // remaining ~95% contention, which is genuine .NET
            // Task/ThreadPool scheduling overhead — reducing concurrent
            // Task volume is a well-motivated lever against that
            // specific mechanism. See ToDo13.md #11.
            var latest = new Dictionary<int, VehiclePositionDto>();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, ct); // 2 times per second

                    // Drain all pending position updates
                    while (_channels.VehiclePositionUpdates.Reader.TryRead(out var update))
                    {
                        latest[update.VehicleId] = new VehiclePositionDto(
                            update.VehicleId,
                            update.SerialNumber,
                            update.X,
                            update.Y,
                            update.NodeId.ToString(),
                            update.Heading);
                    }

                    if (latest.Count > 0)
                    {
                        var batch = latest.Values.ToList();
                        await _hub.Clients.All.SendAsync(
                            "UpdateVehiclePositions", batch, ct);
                        VehiclePositionsUpdated?.Invoke(batch);
                        latest.Clear();
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error broadcasting position update");
                }
            }
        }

        // ----------------------------------------------------------------
        // State updates — activity, SOC, order state.
        //
        // `latest` was a Dictionary guarded by `lock (latest) { ... }`,
        // contended between the drain task (writer) and the flush loop
        // (reader/clearer) below. Now a ConcurrentDictionary reference,
        // swapped out atomically via Interlocked.Exchange each flush —
        // the writer never blocks on the flush, and the flush never
        // blocks on the writer. No Monitor/lock involved at all.
        // ----------------------------------------------------------------

        private async Task BroadcastStatesAsync(
            CancellationToken ct)
        {
            var latest = new ConcurrentDictionary<int, VehicleStateDto>();

            var drainTask = Task.Run(async () =>
            {
                await foreach (var update in
                    _channels.DashboardStateUpdates.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        var history = _socHistory.GetOrAdd(
                            update.VehicleId,
                            _ => new ConcurrentQueue<(DateTime, decimal)>());

                        history.Enqueue((DateTime.UtcNow, update.BatteryStateOfCharge));
                        while (history.Count > SocHistoryMaxPoints)
                            history.TryDequeue(out _);

                        var dto = new VehicleStateDto(
                            update.VehicleId,
                            update.SerialNumber,
                            update.ActivityState.ToString(),
                            update.BatteryStateOfCharge,
                            update.IsCharging,
                            update.IsLoaded,
                            update.CurrentOrderId ?? string.Empty,
                            update.VehicleType);

                        // Lock-free: ConcurrentDictionary indexer set is
                        // thread-safe on its own, no external lock needed.
                        latest[update.VehicleId] = dto;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Error processing state update");
                    }
                }
            }, ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, ct); // 2 times per second — matches positions (9/7 re-test, see note above)

                    if (latest.IsEmpty) continue;

                    // Atomically swap in a fresh empty dictionary and take
                    // ownership of whatever was accumulated — no lock,
                    // no race with the writer above (it simply starts
                    // filling the new instance from this point on).
                    var toSendDict = Interlocked.Exchange(
                        ref latest, new ConcurrentDictionary<int, VehicleStateDto>());

                    var toSend = toSendDict.Values.ToList();
                    if (toSend.Count == 0) continue;

                    await _hub.Clients.All.SendAsync(
                        "UpdateVehicleStates", toSend, ct);
                    VehicleStatesUpdated?.Invoke(toSend);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error broadcasting state update");
                }
            }

            await drainTask;
        }

        // ----------------------------------------------------------------
        // Mission counters — throttled to 1/sec to avoid flooding
        // ----------------------------------------------------------------

        private async Task BroadcastMissionCountersAsync(CancellationToken ct)
        {
            await foreach (var update in
                _channels.MissionCounters.Reader.ReadAllAsync(ct))
            {
                await _hub.Clients.All.SendAsync(
                    "UpdateMissionCounters",
                    new { update.Enqueued, update.Dispatched, update.Completed },
                    ct);
                MissionCountersUpdated?.Invoke(update);
            }
        }

        // ----------------------------------------------------------------
        // Called by SimulationEngineService tick (wired in Program.cs)
        // ----------------------------------------------------------------

        public void OnSimTick(TimeSpan simTime, int tickCount,
            decimal speedFactor, int enqueuedDelta)
        {
            _simTime = simTime;
            _tickCount = tickCount;
            _speedFactor = speedFactor;
            Interlocked.Add(ref _enqueued, enqueuedDelta);
        }

        private async Task BroadcastSimClockAsync(CancellationToken ct)
        {
            await foreach (var update in
                _channels.SimClock.Reader.ReadAllAsync(ct))
            {
                await _hub.Clients.All.SendAsync(
                    "UpdateSimClock",
                    new { update.SimTime, update.SpeedFactor, update.TickCount },
                    ct);
                SimClockUpdated?.Invoke(update);
            }
        }
        private async Task BroadcastAlertsAsync(CancellationToken ct)
        {
            var drainTask = Task.Run(async () =>
            {
                await foreach (var alert in
                    _channels.Alerts.Reader.ReadAllAsync(ct))
                {
                    await _hub.Clients.All.SendAsync(
                        "UpdateAlerts",
                        new
                        {
                            alert.Type,
                            alert.VehicleId,
                            alert.Message,
                            alert.Timestamp
                        },
                        ct);
                    // Intentionally not raising AlertsUpdated here — see
                    // note in the periodic branch below.
                }
            }, ct);

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);

                var alerts = new List<AlertUpdate>();

                foreach (var v in _registry.GetAll())
                {
                    if (v.BatteryStateOfCharge < 30m)
                        alerts.Add(new AlertUpdate(
                            AlertType.LowBattery,
                            v.VehicleId,
                            $"F{v.VehicleId:D2} low battery: {v.BatteryStateOfCharge:F0}%",
                            DateTime.UtcNow));
                }

                foreach (var vid in _traffic.GetBlockedVehicleIds())
                {
                    alerts.Add(new AlertUpdate(
                        AlertType.VehicleBlocked,
                        vid,
                        $"F{vid:D2} blocked waiting for node",
                        DateTime.UtcNow));
                }

                if (alerts.Count > 0)
                {
                    await _hub.Clients.All.SendAsync(
                        "UpdateAlerts",
                        new { Alerts = alerts },
                        ct);
                    // This branch's payload shape ({Alerts:[...]}) is the
                    // one Dashboard.razor's event consumers expect — see
                    // prior session notes. Event raised only here.
                    AlertsUpdated?.Invoke(alerts);
                }
            }

            await drainTask;
        }

        public VehicleDetailDto GetVehicleDetail(int vehicleId)
        {
            var vehicle = _registry.GetById(vehicleId);
            if (vehicle is null) return null!;

            var socPoints = _socHistory.TryGetValue(vehicleId, out var history)
                ? history.Select(h => h.Soc).ToList()
                : new List<decimal>();

            // Downgraded to Debug (9/3): was Information, firing on
            // every click AND every 2Hz popup stream tick while a
            // popup is open — a real contributor to Console sink
            // contention. See ToDo10.md #11.
            _logger.LogDebug(
                "GetVehicleDetail: V{Id} SOC history points={Count}",
                vehicleId, socPoints.Count);

            var graph = _graphHolder.Graph;
            var (currentPart, nextPart) = GetMissionParts(vehicle, graph);

            var lockedNodes = _traffic.GetVehicleLockedNodeIds(vehicleId)
                .OrderBy(id => id)
                .Select(id => NodeRef(graph, id))
                .ToList();

            var lockedMoves = _traffic.GetVehicleLockedMoveIds(vehicleId)
                .OrderBy(id => id)
                .Select(id => graph?.GetMoveById(id))
                .Where(m => m is not null)
                .Select(m => new MoveRefDto(
                    m!.MoveId,
                    NodeRef(graph, m.FromNodeId),
                    NodeRef(graph, m.ToNodeId)))
                .ToList();

            var waitedNodes = _traffic.GetVehicleWaitedNodeIds(vehicleId)
                .Select(id => NodeRef(graph, id) with
                {
                    HeldByVehicleId = _traffic.GetNodeHolderVehicleId(id)
                })
                .ToList();

            return new VehicleDetailDto(
                vehicle.VehicleId,
                vehicle.SerialNumber,
                vehicle.ActivityState.ToString(),
                vehicle.BatteryStateOfCharge,
                vehicle.IsLoaded,
                vehicle.CurrentMissionId,
                vehicle.PlannedRouteNodeIds.ToList(),
                vehicle.CurrentNodeId,
                socPoints,
                currentPart,
                nextPart,
                lockedNodes,
                lockedMoves,
                waitedNodes);
        }

        // ----------------------------------------------------------------
        // Popup detail helpers (#5)
        // ----------------------------------------------------------------

        /// <summary>
        /// Builds a node reference for the popup. The display name is the
        /// node's location name when it has one (preferring the location
        /// whose operation matches), otherwise the node name.
        /// </summary>
        private NodeRefDto NodeRef(
            RoadMapGraph? graph, int nodeId, OperationType? operation = null)
            => new(nodeId,
                _locations.GetLabel(nodeId, operation)
                    ?? graph?.GetNode(nodeId)?.NodeName);

        /// <summary>
        /// Derives the current and next mission part from the vehicle's
        /// active MissionContext. A mission currently has two parts,
        /// Pick (at PickupNodeId) then Drop (at DropNodeId); an unloaded
        /// vehicle is on Pick, a loaded one is on Drop. Kept in one place
        /// so it can later become an n-part list.
        /// </summary>
        private (MissionPartDto? Current, MissionPartDto? Next) GetMissionParts(
            AGV.Core.Entities.Vehicle vehicle, RoadMapGraph? graph)
        {
            var mission = _activeMissions.GetByVehicle(vehicle.VehicleId);
            if (mission is null) return (null, null);

            var pick = new MissionPartDto(
                "Pick", NodeRef(graph, mission.PickupNodeId, OperationType.Pick));
            var drop = new MissionPartDto(
                "Drop", NodeRef(graph, mission.DropNodeId, OperationType.Drop));

            return vehicle.IsLoaded ? (drop, null) : (pick, drop);
        }

        private async Task BroadcastMissionUpdatesAsync(CancellationToken ct)
        {
            await foreach (var update in
                _channels.MissionUpdates.Reader.ReadAllAsync(ct))
            {
                var routeNodeIds = update.RouteNodeIds.ToList();
                await _hub.Clients.All.SendAsync(
                    "UpdateVehicleMission",
                    new
                    {
                        update.VehicleId,
                        update.MissionId,
                        RouteNodeIds = routeNodeIds
                    },
                    ct);
                VehicleMissionUpdated?.Invoke(
                    update.VehicleId, update.MissionId, routeNodeIds);
            }
        }
    }
}
