using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Entities;
using AGV.Core.Enums;
using System.Collections.Concurrent;

namespace AGV.Fleet.Infrastructure
{
    /// <summary>
    /// Thread-safe in-memory registry of all vehicles in the fleet.
    ///
    /// The VehicleRegistry is the live operational state of the fleet —
    /// it reflects the current position, activity, battery, and mission
    /// assignment of every vehicle as reported via VDA 5050 State messages.
    ///
    /// Ownership:
    ///   FleetManagerService is the sole writer of vehicle state.
    ///   All other services read via the query methods here.
    ///   This single-ownership pattern eliminates the lock contention
    ///   that caused deadlocks in the JBT MFC dispatcher/traffic manager.
    ///
    /// Thread safety:
    ///   ConcurrentDictionary provides thread-safe reads and writes
    ///   without explicit locking for the common case.
    ///   Per-vehicle updates are keyed by VehicleId — two threads
    ///   updating different vehicles never contend.
    ///
    /// Relationship to database:
    ///   The registry is the operational hot path — never touches SQL.
    ///   Vehicle state is persisted to VehicleStatusHistory by the
    ///   HistoryWriterService asynchronously, not by the registry.
    /// </summary>
    public sealed class VehicleRegistry
    {
        private readonly ConcurrentDictionary<int, Vehicle>
            _byId = new();

        private readonly ConcurrentDictionary<string, int>
            _serialToId = new();

        // ----------------------------------------------------------------
        // Registration
        // ----------------------------------------------------------------

        /// <summary>
        /// Registers a vehicle in the registry.
        /// Called at startup when the fleet is loaded from the database.
        /// </summary>
        public void Register(Vehicle vehicle)
        {
            _byId[vehicle.VehicleId] = vehicle;
            _serialToId[vehicle.SerialNumber] = vehicle.VehicleId;
        }

        /// <summary>
        /// Registers multiple vehicles at startup.
        /// </summary>
        public void RegisterAll(IEnumerable<Vehicle> vehicles)
        {
            foreach (var v in vehicles)
                Register(v);
        }

        // ----------------------------------------------------------------
        // Queries
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns the vehicle with the specified ID,
        /// or null if not registered.
        /// </summary>
        public Vehicle? GetById(int vehicleId)
            => _byId.TryGetValue(vehicleId, out var v) ? v : null;

        /// <summary>
        /// Returns the vehicle with the specified VDA 5050 serial number,
        /// or null if not registered.
        /// Used by the MQTT listener to correlate incoming state messages.
        /// </summary>
        public Vehicle? GetBySerialNumber(string serialNumber)
        {
            if (_serialToId.TryGetValue(serialNumber, out var id))
                return GetById(id);
            return null;
        }

        /// <summary>
        /// Returns all registered vehicles.
        /// </summary>
        public IReadOnlyCollection<Vehicle> GetAll()
            => _byId.Values.ToList().AsReadOnly();

        /// <summary>
        /// Returns all vehicles currently in service.
        /// </summary>
        public IReadOnlyCollection<Vehicle> GetInService()
            => _byId.Values
                .Where(v => v.IsInService)
                .ToList()
                .AsReadOnly();

        /// <summary>
        /// Returns all vehicles available for mission dispatch.
        /// </summary>
        public IReadOnlyCollection<Vehicle> GetAvailableForDispatch()
            => _byId.Values
                .Where(v => v.IsAvailableForDispatch)
                .ToList()
                .AsReadOnly();

        /// <summary>
        /// Returns all vehicles of the specified type.
        /// </summary>
        public IReadOnlyCollection<Vehicle> GetByType(VehicleType type)
            => _byId.Values
                .Where(v => v.VehicleType == type)
                .ToList()
                .AsReadOnly();

        /// <summary>
        /// Returns all vehicles currently assigned to the specified zone.
        /// </summary>
        public IReadOnlyCollection<Vehicle> GetByZone(int zoneId)
            => _byId.Values
                .Where(v => v.CurrentZoneId == zoneId)
                .ToList()
                .AsReadOnly();

        /// <summary>
        /// Returns all online vehicles (MQTT connected).
        /// </summary>
        public IReadOnlyCollection<Vehicle> GetOnline()
            => _byId.Values
                .Where(v => v.IsOnline)
                .ToList()
                .AsReadOnly();

        /// <summary>
        /// Returns vehicles whose SOC is below the specified threshold.
        /// Used by ChargeQueueManagerService to identify charge candidates.
        /// </summary>
        public IReadOnlyCollection<Vehicle> GetBelowSOC(decimal threshold)
            => _byId.Values
                .Where(v => v.IsInService
                         && v.IsOnline
                         && v.BatteryStateOfCharge < threshold)
                .ToList()
                .AsReadOnly();

        /// <summary>
        /// Returns the total number of registered vehicles.
        /// </summary>
        public int Count => _byId.Count;

        /// <summary>
        /// Returns true if the specified vehicle ID is registered.
        /// </summary>
        public bool Contains(int vehicleId)
            => _byId.ContainsKey(vehicleId);

        // ----------------------------------------------------------------
        // Fleet metrics snapshot
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns a point-in-time snapshot of fleet composition counts.
        /// Used by FleetManagerService to build FleetMetrics.
        /// </summary>
        public FleetCounts GetCounts()
        {
            var all = _byId.Values.ToList();
            return new FleetCounts
            {
                Total = all.Count,
                InService = all.Count(v => v.IsInService),
                Online = all.Count(v => v.IsOnline),
                Idle = all.Count(v =>
                    v.ActivityState == ActivityState.Idle
                    && v.IsInService),
                Charging = all.Count(v =>
                    v.ActivityState == ActivityState.OpportunityCharging
                    || v.ActivityState == ActivityState.MandatoryCharging
                    || v.ActivityState == ActivityState.MaintenanceCharge),
                OnMission = all.Count(v =>
                    v.CurrentMissionId.HasValue
                    && v.IsInService),
                OutOfService = all.Count(v => !v.IsInService),
                AverageSoc = all.Where(v => v.IsInService).Any()
                    ? all.Where(v => v.IsInService)
                         .Average(v => v.BatteryStateOfCharge)
                    : 0m,
            };
        }
    }

    /// <summary>
    /// Point-in-time fleet composition counts.
    /// </summary>
    public sealed class FleetCounts
    {
        public int Total { get; init; }
        public int InService { get; init; }
        public int Online { get; init; }
        public int Idle { get; init; }
        public int Charging { get; init; }
        public int OnMission { get; init; }
        public int OutOfService { get; init; }
        public decimal AverageSoc { get; init; }
    }
}