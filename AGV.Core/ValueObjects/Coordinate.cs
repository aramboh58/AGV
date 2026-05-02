using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.ValueObjects
{
    /// <summary>
    /// Represents a physical position in the facility coordinate system.
    /// Units are centimeters to 0.01cm precision.
    /// Z is true elevation — relevant for multi-floor applications.
    /// For single-floor facilities Z is always 0.
    /// </summary>
    public sealed class Coordinate : IEquatable<Coordinate>
    {
        /// <summary>X position in centimeters.</summary>
        public decimal X { get; }

        /// <summary>Y position in centimeters.</summary>
        public decimal Y { get; }

        /// <summary>Z position in centimeters (true elevation).</summary>
        public decimal Z { get; }

        public Coordinate(decimal x, decimal y, decimal z = 0m)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>
        /// Euclidean distance to another coordinate in centimeters.
        /// Uses X/Y only — Z is excluded for planar distance calculations.
        /// Call DistanceTo3D for true 3D distance.
        /// </summary>
        public double DistanceTo(Coordinate other)
        {
            if (other is null) throw new ArgumentNullException(nameof(other));
            var dx = (double)(X - other.X);
            var dy = (double)(Y - other.Y);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// True 3D Euclidean distance to another coordinate in centimeters.
        /// </summary>
        public double DistanceTo3D(Coordinate other)
        {
            if (other is null) throw new ArgumentNullException(nameof(other));
            var dx = (double)(X - other.X);
            var dy = (double)(Y - other.Y);
            var dz = (double)(Z - other.Z);
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Converts centimeter coordinates to meters.
        /// Useful when interfacing with VDA 5050 (meters) from
        /// internal representation (centimeters).
        /// </summary>
        public (double X, double Y, double Z) ToMeters()
            => ((double)X / 100.0, (double)Y / 100.0, (double)Z / 100.0);

        public bool Equals(Coordinate? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object? obj)
            => obj is Coordinate other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(X, Y, Z);

        public static bool operator ==(Coordinate? left, Coordinate? right)
            => left?.Equals(right) ?? right is null;

        public static bool operator !=(Coordinate? left, Coordinate? right)
            => !(left == right);

        public override string ToString()
            => $"({X:F2}cm, {Y:F2}cm, {Z:F2}cm)";
    }
}