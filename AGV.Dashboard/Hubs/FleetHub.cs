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

        public FleetHub(DashboardBroadcaster broadcaster)
        {
            _broadcaster = broadcaster;
        }

        public async Task RequestVehicleDetail(int vehicleId)
        {
            var detail = _broadcaster.GetVehicleDetail(vehicleId);
            await Clients.Caller.SendAsync("UpdateVehicleDetail", detail);
        }
    }
}
