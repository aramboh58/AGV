using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using AGV.Core.Messages;

namespace AGV.Fleet.Infrastructure
{
    /// <summary>
    /// Thread-safe registry of the MissionContext each vehicle is
    /// currently executing, keyed by VehicleId.
    ///
    /// MissionQueueService holds only PENDING missions; once a mission
    /// is dispatched it leaves the queue and Vehicle holds only the
    /// MissionId. This registry keeps the full context (pickup/drop
    /// nodes, priority, etc.) available to readers such as the
    /// dashboard for as long as the vehicle owns the mission.
    ///
    /// Ownership:
    ///   FleetManagerService is the sole writer (Set at dispatch,
    ///   Remove on completion or transfer). All other services read.
    /// </summary>
    public sealed class ActiveMissionRegistry
    {
        private readonly ConcurrentDictionary<int, MissionContext>
            _byVehicleId = new();

        /// <summary>Records the mission a vehicle is now executing.</summary>
        public void Set(int vehicleId, MissionContext mission)
            => _byVehicleId[vehicleId] = mission;

        /// <summary>Clears the vehicle's active mission, if any.</summary>
        public bool Remove(int vehicleId)
            => _byVehicleId.TryRemove(vehicleId, out _);

        /// <summary>
        /// Returns the vehicle's active mission, or null if none.
        /// </summary>
        public MissionContext? GetByVehicle(int vehicleId)
            => _byVehicleId.TryGetValue(vehicleId, out var m) ? m : null;

        /// <summary>Returns a snapshot of all active missions.</summary>
        public IReadOnlyCollection<MissionContext> GetAll()
            => _byVehicleId.Values.ToList().AsReadOnly();
    }
}
