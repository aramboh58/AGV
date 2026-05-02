using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Entities
{
    /// <summary>
    /// Represents a functional partitioning of the vehicle fleet.
    ///
    /// Zones are fundamentally different from Areas:
    ///   — Areas are geographic/topological (nodes belong to areas)
    ///   — Zones are functional (vehicles belong to zones)
    ///
    /// A zone specifies a required number of vehicles dedicated to a
    /// functional purpose. Examples:
    ///   — Hospital kitchen zone: X vehicles dedicated to hot meal delivery
    ///   — General zone: unassigned vehicles available for any mission
    ///   — Emergency zone: vehicles held in reserve for urgent dispatch
    ///
    /// Zone membership is a vehicle attribute — a vehicle belongs to
    /// exactly one zone at any time. Zone changes are runtime operations
    /// managed by the fleet manager, not topology changes.
    ///
    /// Zones are global and unversioned — they are operational/configuration
    /// entities, not part of the roadmap topology.
    /// </summary>
    public class Zone
    {
        /// <summary>
        /// Primary key.
        /// </summary>
        public int ZoneId { get; private set; }

        /// <summary>
        /// Human-readable name for this zone.
        /// Examples: "General", "KitchenHotMeal", "Emergency", "Maintenance"
        /// </summary>
        public string ZoneName { get; private set; }

        /// <summary>
        /// Optional description of this zone's operational purpose.
        /// </summary>
        public string? Description { get; private set; }

        /// <summary>
        /// The target number of vehicles that should be assigned to
        /// this zone at any given time.
        /// Null means no specific vehicle count is required —
        /// the zone exists for classification purposes only.
        /// </summary>
        public int? RequiredVehicleCount { get; private set; }

        /// <summary>
        /// True if this zone is currently active and accepting
        /// vehicle assignments.
        /// Inactive zones retain their vehicle assignments but the
        /// fleet manager will not enforce their RequiredVehicleCount.
        /// </summary>
        public bool IsActive { get; private set; }

        // Private constructor for EF Core
        private Zone()
        {
            ZoneName = null!;
        }

        public Zone(
            int zoneId,
            string zoneName,
            int? requiredVehicleCount = null,
            string? description = null,
            bool isActive = true)
        {
            if (zoneId <= 0)
                throw new ArgumentOutOfRangeException(nameof(zoneId),
                    "ZoneId must be a positive integer.");

            if (string.IsNullOrWhiteSpace(zoneName))
                throw new ArgumentException(
                    "ZoneName cannot be null or empty.", nameof(zoneName));

            if (requiredVehicleCount.HasValue && requiredVehicleCount.Value <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(requiredVehicleCount),
                    "RequiredVehicleCount must be greater than zero if specified.");

            ZoneId = zoneId;
            ZoneName = zoneName;
            RequiredVehicleCount = requiredVehicleCount;
            Description = description;
            IsActive = isActive;
        }

        /// <summary>
        /// Activates this zone — fleet manager will begin enforcing
        /// RequiredVehicleCount if set.
        /// </summary>
        public void Activate() => IsActive = true;

        /// <summary>
        /// Deactivates this zone — fleet manager stops enforcing
        /// RequiredVehicleCount but vehicle assignments are retained.
        /// </summary>
        public void Deactivate() => IsActive = false;

        /// <summary>
        /// Updates the required vehicle count for this zone.
        /// Called by the fleet manager when operational demand changes —
        /// for example when a kitchen run begins and more vehicles are
        /// needed in the hot meal zone.
        /// </summary>
        public void UpdateRequiredVehicleCount(int? newCount)
        {
            if (newCount.HasValue && newCount.Value <= 0)
                throw new ArgumentOutOfRangeException(nameof(newCount),
                    "RequiredVehicleCount must be greater than zero if specified.");
            RequiredVehicleCount = newCount;
        }

        /// <summary>
        /// True if this zone enforces a vehicle count requirement.
        /// </summary>
        public bool HasVehicleRequirement => RequiredVehicleCount.HasValue;

        public override string ToString()
            => $"Zone[{ZoneId}] {ZoneName}" +
               (RequiredVehicleCount.HasValue
                   ? $" (requires {RequiredVehicleCount} vehicles)"
                   : " (no count requirement)") +
               (IsActive ? "" : " [INACTIVE]");
    }
}