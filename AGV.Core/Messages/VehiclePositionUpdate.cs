using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Messages
{
    /// <summary>
    /// Published by the MQTT listener (or simulation engine) when a
    /// vehicle reports a new confirmed node position via VDA 5050
    /// State message lastNodeId field.
    ///
    /// Consumed by:
    ///   — TrafficManagerService (updates zone occupancy, area counts)
    ///   — FleetManagerService (triggers base/horizon extension evaluation)
    ///   — DashboardHub (forwards to SignalR clients for map update)
    /// </summary>
    public sealed class  VehiclePositionUpdate
    {
        /// <summary>Vehicle that reported the position.</summary>
        public int VehicleId { get; init; }

        /// <summary>
        /// VDA 5050 serialNumber — used for MQTT topic routing.
        /// </summary>
        public string SerialNumber { get; init; } = string.Empty;

        /// <summary>
        /// Logical NodeId of the vehicle's last confirmed position.
        /// Corresponds to VDA 5050 State.lastNodeId.
        /// </summary>
        public int NodeId { get; init; }

        /// <summary>
        /// Map identifier of the coordinate space the vehicle is on.
        /// Corresponds to VDA 5050 nodePosition.mapId.
        /// </summary>
        public string MapId { get; init; } = string.Empty;

        /// <summary>
        /// Interpolated X position for dashboard visualization.
        /// In facility centimeters.
        /// </summary>
        public decimal X { get; init; }

        /// <summary>
        /// Interpolated Y position for dashboard visualization.
        /// In facility centimeters.
        /// </summary>
        public decimal Y { get; init; }

        /// <summary>UTC timestamp when this update was received.</summary>
        public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;
    }

    public record MissionCounterUpdate(int Enqueued, int Dispatched, int Completed);
    public record SimClockUpdate(string SimTime, decimal SpeedFactor, long TickCount);
}