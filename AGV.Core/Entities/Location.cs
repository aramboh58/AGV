using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Entities
{
    /// <summary>
    /// Represents a logical named place in the facility.
    ///
    /// A Location is the human-meaningful grouping that operations staff
    /// and application logic refer to — "Central Supply Pickup",
    /// "Press Stand 3", "Elevator Lobby B", etc.
    ///
    /// A Location is NOT a single point on the map. It is a container
    /// for one or more LocationAssignments, each of which binds a specific
    /// Node to a specific OperationType + LocationType combination.
    ///
    /// Examples:
    ///   Location: "Central Supply Pickup"
    ///     └── Assignment: Node 101, OperationType=Pick, LocationType=CleanLinen
    ///     └── Assignment: Node 102, OperationType=Pick, LocationType=Supplies
    ///     └── Assignment: Node 103, OperationType=Decision, LocationType=CleanLinen
    ///     └── Assignment: Node 104, OperationType=Decision, LocationType=Supplies
    ///
    /// Locations are delta versioned on LocationVersion, which is
    /// independent of but tied to a RoadmapVersion.
    /// </summary>
    public class Location
    {
        /// <summary>
        /// Surrogate primary key for the physical database row.
        /// </summary>
        public int LocationRecordId { get; private set; }

        /// <summary>
        /// Logical stable identity of this location across versions.
        /// Referenced by missions, dispatch rules, and application logic.
        /// </summary>
        public int LocationId { get; private set; }

        /// <summary>
        /// The location version from which this record is effective.
        /// Location versioning is independent of roadmap versioning —
        /// a location can change without a roadmap version increment
        /// and vice versa.
        /// </summary>
        public int EffectiveFromLocationVersionId { get; private set; }

        /// <summary>
        /// True if this location has been logically deleted in this version.
        /// </summary>
        public bool IsDeleted { get; private set; }

        /// <summary>
        /// Human-readable name for this location.
        /// </summary>
        public string LocationName { get; private set; }

        /// <summary>
        /// Optional description of this location's operational purpose.
        /// </summary>
        public string? Description { get; private set; }

        // Private constructor for EF Core
        private Location()
        {
            LocationName = null!;
        }

        public Location(
            int locationId,
            int effectiveFromLocationVersionId,
            string locationName,
            string? description = null)
        {
            if (locationId <= 0)
                throw new ArgumentOutOfRangeException(nameof(locationId),
                    "LocationId must be a positive integer.");

            if (effectiveFromLocationVersionId <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(effectiveFromLocationVersionId),
                    "EffectiveFromLocationVersionId must be a positive integer.");

            if (string.IsNullOrWhiteSpace(locationName))
                throw new ArgumentException(
                    "LocationName cannot be null or empty.",
                    nameof(locationName));

            LocationId = locationId;
            EffectiveFromLocationVersionId = effectiveFromLocationVersionId;
            LocationName = locationName;
            Description = description;
            IsDeleted = false;
        }

        /// <summary>
        /// Marks this location as deleted in the current version.
        /// </summary>
        public void MarkDeleted() => IsDeleted = true;

        /// <summary>
        /// Updates the location name.
        /// Used when a location is renamed without a structural change
        /// that would warrant a new version.
        /// </summary>
        public void Rename(string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException(
                    "LocationName cannot be null or empty.", nameof(newName));
            LocationName = newName;
        }

        public override string ToString()
            => $"Location[{LocationId}] {LocationName}" +
               (IsDeleted ? " [DELETED]" : "");
    }
}