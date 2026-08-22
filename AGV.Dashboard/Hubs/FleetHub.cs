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

        public async Task RequestVehicleDetail(int vehicleId)
        {
            _logger.LogInformation("RequestVehicleDetail ENTER: V{VehicleId}", vehicleId);
            _broadcaster.SetSelectedVehicle(vehicleId);
            var detail = _broadcaster.GetVehicleDetail(vehicleId);
            await Clients.Caller.SendAsync("UpdateVehicleDetail", detail);
            _logger.LogInformation("RequestVehicleDetail EXIT: V{VehicleId}", vehicleId);
        }

        public Task ClearVehicleDetail()
        {
            _broadcaster.SetSelectedVehicle(null);
            return Task.CompletedTask;
        }
    }
}
