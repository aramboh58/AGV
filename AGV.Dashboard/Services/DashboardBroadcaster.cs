using AGV.Core.Enums;
using AGV.Core.Messages;
using AGV.Dashboard.Hubs;
using AGV.Fleet.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        private readonly ILogger<DashboardBroadcaster> _logger;

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
            ChannelRegistry channels,
            IHubContext<FleetHub> hub,
            ILogger<DashboardBroadcaster> logger)
        {
            _channels = channels;
            _hub = hub;
            _logger = logger;
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
                BroadcastMissionCountersAsync(stoppingToken));
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
                _channels.VehicleStateUpdates.Reader.ReadAllAsync(ct))
            {
                try
                {
                    // Track mission counters from state transitions
                    if (update.OrderState == OrderState.Waiting)
                        Interlocked.Increment(ref _dispatched);
                    else if (update.OrderState == OrderState.Finished)
                        Interlocked.Increment(ref _completed);

                    if (update.OrderState == OrderState.Waiting)
                    {
                        Interlocked.Increment(ref _dispatched);
                        Interlocked.Increment(ref _enqueued); // ← add this
                    }

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

        private async Task BroadcastMissionCountersAsync(
            CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, ct);

                    var counterDto = new MissionCounterDto(
                        _enqueued, _dispatched, _completed);

                    await _hub.Clients.All.SendAsync(
                        "UpdateMissionCounters", counterDto, ct);

                    // Add this:
                    var elapsed = DateTime.UtcNow - _simStartTime;
                    var simElapsed = TimeSpan.FromSeconds(elapsed.TotalSeconds * (double)_speedFactor);
                    _simTime = simElapsed;

                    await _hub.Clients.All.SendAsync(
                        "UpdateSimClock", new SimClockDto(
                            _simTime.ToString(@"hh\:mm\:ss"),
                            _speedFactor,
                            _tickCount), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Error broadcasting mission counters");
                }
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
    }
}
