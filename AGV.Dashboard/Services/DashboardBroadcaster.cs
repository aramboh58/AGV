using AGV.Core.Enums;
using AGV.Core.Logging;
using AGV.Core.Messages;
using AGV.Dashboard.Hubs;
using AGV.Fleet.Infrastructure;
using AGV.Fleet.Services;
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
    ///   - `_selectedVehicleId`: was `int?` + `lock` — now a plain
    ///     `volatile int` (0 = none), read/written without any lock.
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

        // Currently selected vehicle for live popup detail streaming.
        // Was int? behind a lock — now a plain volatile int (0 = none).
        // Set via SetSelectedVehicle (called directly by Dashboard.razor
        // on popup open/close — no hub round-trip involved).
        private volatile int _selectedVehicleId; // 0 = no selection

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
        public event Action<VehicleDetailDto>? VehicleDetailUpdated;
        public event Action<int, int?, IReadOnlyList<int>>? VehicleMissionUpdated;

        public DashboardBroadcaster(
            IHubContext<FleetHub> hub,
            ChannelRegistry channels,
            VehicleRegistry registry,
            TrafficManagerService traffic,
            ILoggerFactory loggerFactory)
        {
            _hub = hub;
            _channels = channels;
            _registry = registry;
            _traffic = traffic;
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
                BroadcastMissionUpdatesAsync(stoppingToken),
                BroadcastSelectedVehicleDetailAsync(stoppingToken));
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

        // ----------------------------------------------------------------
        // Selected-vehicle tracking — called directly by Dashboard.razor
        // on popup open (via OnVehicleClicked) and close (via ClosePopup).
        // Plain volatile field, no lock — 0 represents "no selection"
        // since vehicle IDs are always >= 1.
        // ----------------------------------------------------------------

        public void SetSelectedVehicle(int? vehicleId)
        {
            _selectedVehicleId = vehicleId ?? 0;
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

            return new VehicleDetailDto(
                vehicle.VehicleId,
                vehicle.SerialNumber,
                vehicle.ActivityState.ToString(),
                vehicle.BatteryStateOfCharge,
                vehicle.IsLoaded,
                vehicle.CurrentMissionId,
                vehicle.PlannedRouteNodeIds.ToList(),
                vehicle.CurrentNodeId,
                socPoints);
        }

        // ----------------------------------------------------------------
        // Live popup streaming — pushes UpdateVehicleDetail at 2Hz for
        // whichever single vehicle is currently selected (popup open).
        // No-op when no popup is open, so this costs nothing at rest.
        // ----------------------------------------------------------------

        private async Task BroadcastSelectedVehicleDetailAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, ct); // 2 times per second

                    var vehicleId = _selectedVehicleId; // volatile read, no lock

                    if (vehicleId != 0)
                    {
                        var detail = GetVehicleDetail(vehicleId);
                        if (detail is not null)
                        {
                            await _hub.Clients.All.SendAsync(
                                "UpdateVehicleDetail", detail, ct);
                            VehicleDetailUpdated?.Invoke(detail);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Error broadcasting selected vehicle detail");
                }
            }
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
