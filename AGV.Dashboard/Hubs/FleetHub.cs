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
        // No server-side methods needed for Phase 1 —
        // all communication is server → client push.
        // Client methods called by DashboardBroadcaster:
        //   UpdateVehiclePosition(VehiclePositionDto)
        //   UpdateVehicleState(VehicleStateDto)
        //   UpdateMissionCounters(MissionCounterDto)
        //   UpdateSimClock(SimClockDto)
    }
}
