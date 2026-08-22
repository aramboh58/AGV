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

namespace AGV.Dashboard.Services
{
    /// <summary>
    /// Hosted service that bridges the ChannelRegistry to SignalR.
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
    /// Positions and states are each sent as a single batched list per
    /// flush cycle (not one message per vehicle) — see UpdateVehicleStates
    /// / UpdateVehiclePositions below. Sending N individual messages per
    /// flush cycle (one per changed vehicle) was found to cause a tight
    /// burst of SignalR deliveries — each triggering its own client-side
    /// JS interop call and, for states, its own full-component
    /// StateHasChanged render — that could back up the browser's single
    /// main-thread message queue badly enough to noticeably delay
    /// unrelated messages (e.g. vehicle popup detail) queued behind them.
    /// </summary>
    public sealed class DashboardBroadcaster : BackgroundService
    {
        private readonly ChannelRegistry _channels;
        private readonly IHubContext<FleetHub> _hub;
        private readonly ILogger _logger;
        private readonly VehicleRegistry _registry;
        private readonly TrafficManagerService _traffic;

        private readonly Dictionary<int, Queue<(DateTime Time, decimal Soc)>> _socHistory = new();
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
        // Set via SetSelectedVehicle (called from FleetHub.RequestVehicleDetail
        // and cleared via FleetHub.ClearVehicleDetail on popup close).
        private int? _selectedVehicleId;
        private readonly object _selectionLock = new();

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
        // Batched: one "UpdateVehiclePositions" message per flush cycle
        // carrying a list, not one "UpdateVehiclePosition" message per
        // vehicle.
        // ----------------------------------------------------------------

        private async Task BroadcastPositionsAsync(CancellationToken ct)
        {
            // Throttle: batch position updates, broadcast at 4Hz max
            var latest = new Dictionary<int, VehiclePositionDto>();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(250, ct); // 4 times per second

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
                        await _hub.Clients.All.SendAsync(
                            "UpdateVehiclePositions", latest.Values.ToList(), ct);
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
        // Batched: one "UpdateVehicleStates" message per flush cycle
        // carrying a list, not one "UpdateVehicleState" message per
        // vehicle. A background drain task collapses incoming updates
        // to latest-per-vehicle; a separate loop flushes that at a
        // bounded 4Hz as a single message.
        // ----------------------------------------------------------------

        private async Task BroadcastStatesAsync(
            CancellationToken ct)
        {
            var latest = new Dictionary<int, VehicleStateDto>();

            var drainTask = Task.Run(async () =>
            {
                await foreach (var update in
                    _channels.DashboardStateUpdates.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        if (!_socHistory.ContainsKey(update.VehicleId))
                            _socHistory[update.VehicleId] = new Queue<(DateTime, decimal)>();

                        var history = _socHistory[update.VehicleId];
                        history.Enqueue((DateTime.UtcNow, update.BatteryStateOfCharge));
                        if (history.Count > SocHistoryMaxPoints)
                            history.Dequeue();

                        var dto = new VehicleStateDto(
                            update.VehicleId,
                            update.SerialNumber,
                            update.ActivityState.ToString(),
                            update.BatteryStateOfCharge,
                            update.IsCharging,
                            update.IsLoaded,
                            update.CurrentOrderId ?? string.Empty,
                            update.VehicleType);

                        lock (latest) { latest[update.VehicleId] = dto; }
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
                    await Task.Delay(250, ct); // 4 times per second — matches positions

                    List<VehicleStateDto> toSend;
                    lock (latest)
                    {
                        if (latest.Count == 0) continue;
                        toSend = latest.Values.ToList();
                        latest.Clear();
                    }

                    await _hub.Clients.All.SendAsync(
                        "UpdateVehicleStates", toSend, ct);
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
        // Selected-vehicle tracking — called by FleetHub on popup
        // open (RequestVehicleDetail) and close (ClearVehicleDetail).
        // ----------------------------------------------------------------

        public void SetSelectedVehicle(int? vehicleId)
        {
            lock (_selectionLock) { _selectedVehicleId = vehicleId; }
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
                }
            }

            await drainTask;
        }
        public VehicleDetailDto GetVehicleDetail(int vehicleId)
        {
            var vehicle = _registry.GetById(vehicleId);
            if (vehicle is null) return null!;

            _socHistory.TryGetValue(vehicleId, out var history);
            var socPoints = history?.Select(h => h.Soc).ToList()
                            ?? new List<decimal>();

            _logger.LogInformation(
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

                    int? vehicleId;
                    lock (_selectionLock) { vehicleId = _selectedVehicleId; }

                    if (vehicleId.HasValue)
                    {
                        var detail = GetVehicleDetail(vehicleId.Value);
                        if (detail is not null)
                            await _hub.Clients.All.SendAsync(
                                "UpdateVehicleDetail", detail, ct);
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
                await _hub.Clients.All.SendAsync(
                    "UpdateVehicleMission",
                    new
                    {
                        update.VehicleId,
                        update.MissionId,
                        RouteNodeIds = update.RouteNodeIds.ToList()
                    },
                    ct);
            }
        }
    }
}
