using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;
using AGV.Core.ValueObjects;

namespace AGV.Core.Entities
{
    /// <summary>
    /// Represents a directed clothoid move between two nodes on the
    /// road network.
    ///
    /// Key design decisions:
    ///   — Bidirectional paths are represented as two distinct Move records
    ///     (A→B and B→A), each with independent speed and direction.
    ///   — Speed is always a positive magnitude. Direction (forward/reverse)
    ///     is expressed via TravelDirection.
    ///   — Multiple moves may exist between the same node pair, each
    ///     serving a different RoutingType (vehicle class).
    ///   — Arrival heading at the destination node is Move.Clothoid.EndHeading
    ///     — it is NOT stored on the destination Node.
    ///   — Runtime blocking is tracked separately in MoveBlock (not here).
    /// </summary>
    public class Move
    {
        /// <summary>
        /// Surrogate primary key for the physical database row.
        /// </summary>
        public int MoveRecordId { get; private set; }

        /// <summary>
        /// Logical stable identity of this move across topology versions.
        /// Used in VDA 5050 order edge references.
        /// </summary>
        public int MoveId { get; private set; }

        /// <summary>
        /// The roadmap version from which this move record is effective.
        /// </summary>
        public int EffectiveFromVersionId { get; private set; }

        /// <summary>
        /// True if this move has been logically deleted in this version.
        /// </summary>
        public bool IsDeleted { get; private set; }

        /// <summary>
        /// Logical NodeId of the move origin.
        /// </summary>
        public int FromNodeId { get; private set; }

        /// <summary>
        /// Logical NodeId of the move destination.
        /// Move.Clothoid.EndHeading is the canonical arrival heading
        /// at this node.
        /// </summary>
        public int ToNodeId { get; private set; }

        /// <summary>
        /// Routing type governing which vehicle classes may traverse
        /// this move. A RoutingType may serve multiple VehicleTypes.
        /// </summary>
        public int RoutingTypeId { get; private set; }

        /// <summary>
        /// Physical travel direction of the vehicle on this move.
        /// Forward or Reverse — always paired with a positive speed magnitude.
        /// </summary>
        public TravelDirection TravelDirection { get; private set; }

        /// <summary>
        /// Clothoid geometric parameters defining the path shape,
        /// headings, and arc length of this move.
        /// </summary>
        public ClothoidParameters Clothoid { get; private set; }

        /// <summary>
        /// Speed constraints for this move.
        /// DefaultSpeed is normal operating speed.
        /// MaxSpeed is the absolute ceiling.
        /// </summary>
        public SpeedConstraint Speed { get; private set; }

        /// <summary>
        /// Optional maximum weight capacity for vehicles on this move.
        /// Null means no weight restriction defined.
        /// Unit: kilograms.
        /// Future use — e.g. skybridge vs. main travel floor.
        /// </summary>
        public decimal? MaxWeightCapacityKg { get; private set; }

        // Private constructor for EF Core
        private Move()
        {
            Clothoid = null!;
            Speed = null!;
        }

        public Move(
            int moveId,
            int effectiveFromVersionId,
            int fromNodeId,
            int toNodeId,
            int routingTypeId,
            TravelDirection travelDirection,
            ClothoidParameters clothoid,
            SpeedConstraint speed,
            decimal? maxWeightCapacityKg = null)
        {
            if (moveId <= 0)
                throw new ArgumentOutOfRangeException(nameof(moveId),
                    "MoveId must be a positive integer.");

            if (effectiveFromVersionId <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(effectiveFromVersionId),
                    "EffectiveFromVersionId must be a positive integer.");

            if (fromNodeId <= 0)
                throw new ArgumentOutOfRangeException(nameof(fromNodeId),
                    "FromNodeId must be a positive integer.");

            if (toNodeId <= 0)
                throw new ArgumentOutOfRangeException(nameof(toNodeId),
                    "ToNodeId must be a positive integer.");

            if (fromNodeId == toNodeId)
                throw new ArgumentException(
                    "FromNodeId and ToNodeId cannot be the same node. " +
                    "A move must connect two distinct nodes.");

            if (routingTypeId <= 0)
                throw new ArgumentOutOfRangeException(nameof(routingTypeId),
                    "RoutingTypeId must be a positive integer.");

            if (maxWeightCapacityKg.HasValue && maxWeightCapacityKg.Value <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxWeightCapacityKg),
                    "MaxWeightCapacityKg must be greater than zero if specified.");

            MoveId = moveId;
            EffectiveFromVersionId = effectiveFromVersionId;
            FromNodeId = fromNodeId;
            ToNodeId = toNodeId;
            RoutingTypeId = routingTypeId;
            TravelDirection = travelDirection;
            Clothoid = clothoid
                ?? throw new ArgumentNullException(nameof(clothoid));
            Speed = speed
                ?? throw new ArgumentNullException(nameof(speed));
            MaxWeightCapacityKg = maxWeightCapacityKg;
            IsDeleted = false;
        }

        /// <summary>
        /// Marks this move as deleted in the current topology version.
        /// </summary>
        public void MarkDeleted() => IsDeleted = true;

        /// <summary>
        /// True if this move is traversable — not deleted.
        /// Runtime blocking (MoveBlock) is checked separately by the
        /// routing engine at query time, not stored here.
        /// </summary>
        public bool IsTraversable => !IsDeleted;

        /// <summary>
        /// Arc length of this move in centimeters.
        /// Convenience accessor into Clothoid parameters.
        /// </summary>
        public decimal ArcLengthCm => Clothoid.ArcLength;

        /// <summary>
        /// Arrival heading at the destination node in signed degrees.
        /// This is the canonical heading — not stored on the Node itself.
        /// </summary>
        public decimal ArrivalHeading => Clothoid.EndHeading;

        /// <summary>
        /// True if this move travels in reverse.
        /// Reverse moves typically have lower max speeds due to
        /// reduced ultrasonic bumper coverage amplitude.
        /// </summary>
        public bool IsReverse
            => TravelDirection == TravelDirection.Reverse;

        public override string ToString()
            => $"Move[{MoveId}] {FromNodeId}→{ToNodeId} " +
               $"{TravelDirection} " +
               $"Speed={Speed.DefaultSpeed:F4}m/s " +
               $"L={ArcLengthCm:F2}cm";
    }
}