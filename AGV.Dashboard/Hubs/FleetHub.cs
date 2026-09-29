using AGV.Dashboard.Services;
using Microsoft.AspNetCore.SignalR;

namespace AGV.Dashboard.Hubs
{
    /// <summary>
    /// SignalR hub for real-time fleet dashboard updates.
    /// Browsers connect here to receive vehicle position,
    /// state, and mission counter pushes.
    /// </summary>
    public sealed class FleetHub : Hub
    {
        private readonly DashboardBroadcaster _broadcaster;
        private readonly ILogger<FleetHub> _logger;

        public FleetHub(DashboardBroadcaster broadcaster, ILogger<FleetHub> logger)
        {
            _broadcaster = broadcaster;
            _logger = logger;
        }

        // 9/28 (#11b): the shared, process-wide "selected vehicle" was
        // removed from DashboardBroadcaster (one client's selection
        // changed every client's popup). The Blazor dashboard no longer
        // uses this hub for popups at all (per-circuit refresh lives in
        // Dashboard.razor). This method is now a one-shot snapshot to the
        // calling connection only — no selection state, no streaming.
        public async Task RequestVehicleDetail(int vehicleId)
        {
            _logger.LogInformation("RequestVehicleDetail ENTER: V{VehicleId}", vehicleId);
            var detail = _broadcaster.GetVehicleDetail(vehicleId);
            await Clients.Caller.SendAsync("UpdateVehicleDetail", detail);
            _logger.LogInformation("RequestVehicleDetail EXIT: V{VehicleId}", vehicleId);
        }

        // Kept as a no-op so any client that still calls it doesn't get
        // an error. There is no server-side selection to clear anymore.
        public Task ClearVehicleDetail()
        {
            return Task.CompletedTask;
        }
    }
}
