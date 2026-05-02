using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Entities;
using AGV.Core.Messages;

namespace AGV.Core.Interfaces
{
    /// <summary>
    /// Contract for the fleet manager service.
    ///
    /// The fleet manager is the central coordinator of the host system.
    /// It owns the vehicle registry and mission queue, and orchestrates
    /// dispatch, charging, order stealing, and dead mission detection.
    ///
    /// Single ownership principle:
    ///   — FleetManagerService is the sole writer of vehicle state
    ///   — All other services read vehicle state via this interface
    ///   — Cross-service coordination happens via Channel<T> messages,
    ///     not direct method calls between services
    ///
    /// The fleet manager does NOT perform routing — it delegates to
    /// IRoutingEngine and receives results asynchronously.
    /// </summary>
    public interface IFleetManager
    {
        /// <summary>
        /// Returns a snapshot of the current state of all vehicles.
        /// Read-only — callers must not mutate the returned vehicles.
        /// </summary>
        IReadOnlyCollection<Vehicle> GetAllVehicles();

        /// <summary>
        /// Returns the current state of a specific vehicle.
        /// Returns null if the vehicle ID is not registered.
        /// </summary>
        Vehicle? GetVehicle(int vehicleId);

        /// <summary>
        /// Returns the vehicle registered with the given VDA 5050
        /// serial number. Used by the MQTT listener to correlate
        /// incoming state messages to host vehicle records.
        /// Returns null if not found.
        /// </summary>
        Vehicle? GetVehicleBySerialNumber(string serialNumber);

        /// <summary>
        /// Returns all vehicles currently available for dispatch —
        /// in service, online, idle, and with sufficient SOC.
        /// </summary>
        IReadOnlyCollection<Vehicle> GetAvailableVehicles();

        /// <summary>
        /// Enqueues a new mission for dispatch.
        /// The fleet manager will select the best available vehicle
        /// and dispatch on the next evaluation cycle.
        /// </summary>
        Task EnqueueMissionAsync(
            MissionContext missionContext,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the current mission queue depth.
        /// Used by the dashboard and metrics reporting.
        /// </summary>
        int PendingMissionCount { get; }

        /// <summary>
        /// Returns current fleet utilization metrics.
        /// </summary>
        FleetMetrics GetMetrics();

        /// <summary>
        /// Manually takes a vehicle out of service.
        /// Any active mission is transferred via MissionTransfer channel.
        /// </summary>
        Task RemoveVehicleFromServiceAsync(
            int vehicleId,
            string reason,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns a vehicle to service after maintenance or charge.
        /// </summary>
        Task ReturnVehicleToServiceAsync(
            int vehicleId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Point-in-time fleet utilization metrics.
    /// Published to the dashboard and metrics MQTT topic.
    /// </summary>
    public sealed class FleetMetrics
    {
        public int TotalVehicles { get; init; }
        public int VehiclesInService { get; init; }
        public int VehiclesOnline { get; init; }
        public int VehiclesIdle { get; init; }
        public int VehiclesCharging { get; init; }
        public int VehiclesOnMission { get; init; }
        public int VehiclesOutOfService { get; init; }
        public int PendingMissions { get; init; }
        public int CompletedMissionsTotal { get; init; }
        public int TransferredMissionsTotal { get; init; }
        public int SwappedMissionsTotal { get; init; }
        public decimal AverageBatterySoc { get; init; }
        public double ThroughputPerHour { get; init; }
        public DateTime SnapshotAt { get; init; } = DateTime.UtcNow;
    }
}
