using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.ValueObjects;

namespace AGV.Core.Entities
{
    /// <summary>
    /// Represents a geographic/topological grouping of nodes on the
    /// road network.
    ///
    /// Areas serve two primary purposes:
    ///   1. Vehicle count limiting — MaxVehicleCount caps how many
    ///      vehicles may occupy the area simultaneously. The runtime
    ///      AreaOccupancy table tracks the live count.
    ///   2. Entry/exit detection — when a vehicle transitions into or
    ///      out of an area, downstream application logic is triggered
    ///      (e.g. zone rezoning, throughput counting, alarm conditions).
    ///
    /// Areas are usually contiguous but may be fragmented — a node
    /// does not need to be physically adjacent to other area members.
    /// A node may belong to multiple areas simultaneously.
    ///
    /// Runtime occupancy is tracked in AreaOccupancy (not here).
    /// This entity represents the static versioned topology definition.
    /// </summary>
    public class Area
    {
        /// <summary>
        /// Surrogate primary key for the physical database row.
        /// </summary>
        public int AreaRecordId { get; private set; }

        /// <summary>
        /// Logical stable identity of this area across topology versions.
        /// </summary>
        public int AreaId { get; private set; }

        /// <summary>
        /// The roadmap version from which this area record is effective.
        /// </summary>
        public int EffectiveFromVersionId { get; private set; }

        /// <summary>
        /// True if this area has been logically deleted in this version.
        /// </summary>
        public bool IsDeleted { get; private set; }

        /// <summary>
        /// Human-readable name for this area.
        /// Used in dashboards, alerts, and diagnostics.
        /// </summary>
        public string AreaName { get; private set; }

        /// <summary>
        /// Optional description of this area's purpose or boundaries.
        /// </summary>
        public string? Description { get; private set; }

        /// <summary>
        /// Maximum number of vehicles permitted in this area simultaneously.
        /// Null means no vehicle count restriction is enforced.
        /// </summary>
        public int? MaxVehicleCount { get; private set; }

        // Private constructor for EF Core
        private Area()
        {
            AreaName = null!;
        }

        public Area(
            int areaId,
            int effectiveFromVersionId,
            string areaName,
            int? maxVehicleCount = null,
            string? description = null)
        {
            if (areaId <= 0)
                throw new ArgumentOutOfRangeException(nameof(areaId),
                    "AreaId must be a positive integer.");

            if (effectiveFromVersionId <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(effectiveFromVersionId),
                    "EffectiveFromVersionId must be a positive integer.");

            if (string.IsNullOrWhiteSpace(areaName))
                throw new ArgumentException(
                    "AreaName cannot be null or empty.", nameof(areaName));

            if (maxVehicleCount.HasValue && maxVehicleCount.Value <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxVehicleCount),
                    "MaxVehicleCount must be greater than zero if specified.");

            AreaId = areaId;
            EffectiveFromVersionId = effectiveFromVersionId;
            AreaName = areaName;
            MaxVehicleCount = maxVehicleCount;
            Description = description;
            IsDeleted = false;
        }

        /// <summary>
        /// Marks this area as deleted in the current topology version.
        /// </summary>
        public void MarkDeleted() => IsDeleted = true;

        /// <summary>
        /// True if this area enforces a vehicle count limit.
        /// </summary>
        public bool HasVehicleLimit => MaxVehicleCount.HasValue;

        /// <summary>
        /// Updates the maximum vehicle count for this area.
        /// Used when operational conditions require a limit change
        /// without a full topology version increment.
        /// </summary>
        public void UpdateVehicleLimit(int? newLimit)
        {
            if (newLimit.HasValue && newLimit.Value <= 0)
                throw new ArgumentOutOfRangeException(nameof(newLimit),
                    "MaxVehicleCount must be greater than zero if specified.");
            MaxVehicleCount = newLimit;
        }

        public override string ToString()
            => $"Area[{AreaId}] {AreaName}" +
               (MaxVehicleCount.HasValue
                   ? $" (max {MaxVehicleCount} vehicles)"
                   : " (no vehicle limit)");
    }
}