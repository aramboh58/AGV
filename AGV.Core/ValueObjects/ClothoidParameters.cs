using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.ValueObjects
{
    /// <summary>
    /// Defines the geometric parameters of a clothoid (Euler spiral) move
    /// between two nodes on the road network.
    /// 
    /// A clothoid provides smooth, physically realizable transitions between
    /// straight and curved path segments by varying curvature linearly with
    /// arc length — giving the vehicle's steering a constant rate of change.
    /// 
    /// Headings are in signed degrees: -180.0000 to +180.0000.
    /// Arc length is in centimeters.
    /// Clothoid parameter A is dimensionless (A² = R × L where R=radius, L=arc length).
    /// </summary>
    public sealed class ClothoidParameters : IEquatable<ClothoidParameters>
    {
        /// <summary>
        /// Heading at the start of the move in signed degrees.
        /// Range: -180.0000 to +180.0000
        /// </summary>
        public decimal StartHeading { get; }

        /// <summary>
        /// Heading at the end of the move in signed degrees.
        /// This is the canonical arrival heading at the destination node.
        /// Range: -180.0000 to +180.0000
        /// </summary>
        public decimal EndHeading { get; }

        /// <summary>
        /// The clothoid parameter A (Euler spiral parameter).
        /// A² = R × L where R is the radius of curvature and L is arc length.
        /// A value of 0 indicates a straight-line move.
        /// </summary>
        public decimal ParameterA { get; }

        /// <summary>
        /// Total arc length of the move in centimeters.
        /// </summary>
        public decimal ArcLength { get; }

        public ClothoidParameters(
            decimal startHeading,
            decimal endHeading,
            decimal parameterA,
            decimal arcLength)
        {
            ValidateHeading(startHeading, nameof(startHeading));
            ValidateHeading(endHeading, nameof(endHeading));

            if (parameterA < 0)
                throw new ArgumentOutOfRangeException(nameof(parameterA),
                    "Clothoid parameter A must be non-negative.");

            if (arcLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(arcLength),
                    "Arc length must be greater than zero.");

            StartHeading = startHeading;
            EndHeading = endHeading;
            ParameterA = parameterA;
            ArcLength = arcLength;
        }

        /// <summary>
        /// Returns true if this move is a straight line
        /// (clothoid parameter A is zero).
        /// </summary>
        public bool IsStraightLine => ParameterA == 0m;

        /// <summary>
        /// Total heading change across the move in degrees.
        /// </summary>
        public decimal HeadingChange => EndHeading - StartHeading;

        /// <summary>
        /// Arc length converted to meters.
        /// Used when constructing VDA 5050 order messages.
        /// </summary>
        public double ArcLengthMeters => (double)ArcLength / 100.0;

        private static void ValidateHeading(decimal heading, string paramName)
        {
            if (heading < -180.0000m || heading > 180.0000m)
                throw new ArgumentOutOfRangeException(paramName,
                    $"Heading must be between -180.0000 and 180.0000 degrees. " +
                    $"Value was: {heading}");
        }

        public bool Equals(ClothoidParameters? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return StartHeading == other.StartHeading
                && EndHeading == other.EndHeading
                && ParameterA == other.ParameterA
                && ArcLength == other.ArcLength;
        }

        public override bool Equals(object? obj)
            => obj is ClothoidParameters other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(StartHeading, EndHeading, ParameterA, ArcLength);

        public static bool operator ==(ClothoidParameters? left,
                                        ClothoidParameters? right)
            => left?.Equals(right) ?? right is null;

        public static bool operator !=(ClothoidParameters? left,
                                        ClothoidParameters? right)
            => !(left == right);

        public override string ToString()
            => $"Heading {StartHeading:F4}°→{EndHeading:F4}° " +
               $"A={ParameterA:F6} L={ArcLength:F2}cm";
    }
}