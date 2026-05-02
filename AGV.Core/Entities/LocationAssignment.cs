using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Entities
{
    /// <summary>
    /// Binds a specific Node to a specific OperationType and LocationType
    /// within a Location.
    ///
    /// LocationAssignment is the atomic unit of specificity in the
    /// location layer. It answers the question: "At this node, what
    /// operation is performed, and for what payload context?"
    ///
    /// Key design points:
    ///
    ///   OperationType and LocationType are orthogonal axes:
    ///     — OperationType: the physical task (Pick, Drop, Charge, etc.)
    ///     — LocationType:  the payload/mission context (CleanLinen,
    ///                      Supplies, SinglePallet, etc.)
    ///
    ///   Decision loci:
    ///     — A Decision assignment is usually at a node separate from
    ///       its associated operation nodes.
    ///     — GuardedAssignmentId optionally links a Decision assignment
    ///       to the primary operation assignment it guards — used when
    ///       the Decision node guards an approach from the broader road
    ///       network before the vehicle commits to a specific location.
    ///     — When null, the Decision's association is implicit via
    ///       shared LocationId.
    ///
    ///   Runtime LocationType override:
    ///     — LocationType is generally static but may be overridden at
    ///       runtime without a version increment.
    ///     — The override is tracked in LocationAssignmentRuntimeTypeOverride
    ///       (not here). The fleet manager resolves the effective
    ///       LocationType at dispatch time.
    /// </summary>
    public class LocationAssignment
    {
        /// <summary>
        /// Surrogate primary key for the physical database row.
        /// </summary>
        public int AssignmentRecordId { get; private set; }

        /// <summary>
        /// Logical stable identity of this assignment across versions.
        /// </summary>
        public int AssignmentId { get; private set; }

        /// <summary>
        /// The location version from which this assignment is effective.
        /// </summary>
        public int EffectiveFromLocationVersionId { get; private set; }

        /// <summary>
        /// True if this assignment has been logically deleted in this version.
        /// </summary>
        public bool IsDeleted { get; private set; }

        /// <summary>
        /// The logical LocationId this assignment belongs to.
        /// </summary>
        public int LocationId { get; private set; }

        /// <summary>
        /// The logical NodeId at which this operation is performed.
        /// One node may appear in multiple assignments if it serves
        /// multiple OperationType + LocationType combinations.
        /// </summary>
        public int NodeId { get; private set; }

        /// <summary>
        /// The type of operation performed at this node within
        /// this location context.
        /// Examples: Pick, Drop, PickRack, Charge, Decision, Park
        /// </summary>
        public int OperationTypeId { get; private set; }

        /// <summary>
        /// The payload/mission context for this assignment.
        /// Drives mission segregation — e.g. CleanLinen vs. Supplies
        /// at the same physical location.
        /// May be overridden at runtime via
        /// LocationAssignmentRuntimeTypeOverride.
        /// </summary>
        public int LocationTypeId { get; private set; }

        /// <summary>
        /// Optional link from a Decision assignment to the primary
        /// operation assignment it guards.
        ///
        /// Used when a Decision node guards an approach from the broader
        /// road network — before the vehicle is committed to a specific
        /// LocationId. When null, the Decision's association to its
        /// primary operation is implicit via shared LocationId.
        /// </summary>
        public int? GuardedAssignmentId { get; private set; }

        // Private constructor for EF Core
        private LocationAssignment() { }

        public LocationAssignment(
            int assignmentId,
            int effectiveFromLocationVersionId,
            int locationId,
            int nodeId,
            int operationTypeId,
            int locationTypeId,
            int? guardedAssignmentId = null)
        {
            if (assignmentId <= 0)
                throw new ArgumentOutOfRangeException(nameof(assignmentId),
                    "AssignmentId must be a positive integer.");

            if (effectiveFromLocationVersionId <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(effectiveFromLocationVersionId),
                    "EffectiveFromLocationVersionId must be a positive integer.");

            if (locationId <= 0)
                throw new ArgumentOutOfRangeException(nameof(locationId),
                    "LocationId must be a positive integer.");

            if (nodeId <= 0)
                throw new ArgumentOutOfRangeException(nameof(nodeId),
                    "NodeId must be a positive integer.");

            if (operationTypeId <= 0)
                throw new ArgumentOutOfRangeException(nameof(operationTypeId),
                    "OperationTypeId must be a positive integer.");

            if (locationTypeId <= 0)
                throw new ArgumentOutOfRangeException(nameof(locationTypeId),
                    "LocationTypeId must be a positive integer.");

            if (guardedAssignmentId.HasValue && guardedAssignmentId.Value <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(guardedAssignmentId),
                    "GuardedAssignmentId must be a positive integer if specified.");

            if (guardedAssignmentId.HasValue &&
                guardedAssignmentId.Value == assignmentId)
                throw new ArgumentException(
                    "GuardedAssignmentId cannot reference the assignment itself.",
                    nameof(guardedAssignmentId));

            AssignmentId = assignmentId;
            EffectiveFromLocationVersionId = effectiveFromLocationVersionId;
            LocationId = locationId;
            NodeId = nodeId;
            OperationTypeId = operationTypeId;
            LocationTypeId = locationTypeId;
            GuardedAssignmentId = guardedAssignmentId;
            IsDeleted = false;
        }

        /// <summary>
        /// Marks this assignment as deleted in the current version.
        /// </summary>
        public void MarkDeleted() => IsDeleted = true;

        /// <summary>
        /// True if this assignment is a Decision locus that explicitly
        /// guards another assignment via GuardedAssignmentId.
        /// </summary>
        public bool IsExplicitDecisionGuard => GuardedAssignmentId.HasValue;

        public override string ToString()
            => $"Assignment[{AssignmentId}] " +
               $"Location={LocationId} Node={NodeId} " +
               $"OpType={OperationTypeId} LocType={LocationTypeId}" +
               (GuardedAssignmentId.HasValue
                   ? $" Guards={GuardedAssignmentId}"
                   : "") +
               (IsDeleted ? " [DELETED]" : "");
    }
}