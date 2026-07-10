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
    /// dashboard counters panel.
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
               BroadcastAlertsAsync(stoppingToken));
        }

        // ----------------------------------------------------------------
        // Position updates — high frequency, direct from sim tick
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
                            update.NodeId.ToString());
                    }

                    // Broadcast latest position for each vehicle
                    foreach (var dto in latest.Values)
                    {
                        await _hub.Clients.All.SendAsync(
                            "UpdateVehiclePosition", dto, ct);
                    }

                    latest.Clear();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error broadcasting position update");
                }
            }
        }

        // ----------------------------------------------------------------
        // State updates — activity, SOC, order state
        // ----------------------------------------------------------------

        private async Task BroadcastStatesAsync(
            CancellationToken ct)
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
                    
                    _logger.LogInformation("OrderState: {OrderState}", update.OrderState);

                    var dto = new VehicleStateDto(
                        update.VehicleId,
                        update.SerialNumber,
                        update.ActivityState.ToString(),
                        update.BatteryStateOfCharge,
                        update.IsCharging,
                        update.IsLoaded,
                        update.CurrentOrderId ?? string.Empty);

                    await _hub.Clients.All.SendAsync(
                        "UpdateVehicleState", dto, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Error broadcasting state update");
                }
            }
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
    }
}
