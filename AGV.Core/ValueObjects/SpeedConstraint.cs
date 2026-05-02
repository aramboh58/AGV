using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.ValueObjects
{
    /// <summary>
    /// Defines the speed constraints for a move on the road network.
    /// 
    /// Speed is always a positive magnitude in meters per second.
    /// Direction (forward/reverse) is expressed separately via TravelDirection.
    /// 
    /// DefaultSpeed is the normal operating speed for this move.
    /// MaxSpeed is the absolute ceiling — never exceeded regardless of
    /// vehicle capability or host override.
    /// </summary>
    public sealed class SpeedConstraint : IEquatable<SpeedConstraint>
    {
        /// <summary>
        /// Normal operating speed for this move in meters per second.
        /// Positive magnitude only — direction is in TravelDirection.
        /// </summary>
        public decimal DefaultSpeed { get; }

        /// <summary>
        /// Absolute maximum speed for this move in meters per second.
        /// The host may instruct a lower speed but never higher than this.
        /// </summary>
        public decimal MaxSpeed { get; }

        public SpeedConstraint(decimal defaultSpeed, decimal maxSpeed)
        {
            if (defaultSpeed <= 0)
                throw new ArgumentOutOfRangeException(nameof(defaultSpeed),
                    "Default speed must be greater than zero.");

            if (maxSpeed <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSpeed),
                    "Max speed must be greater than zero.");

            if (defaultSpeed > maxSpeed)
                throw new ArgumentException(
                    $"Default speed ({defaultSpeed} m/s) cannot exceed " +
                    $"max speed ({maxSpeed} m/s).");

            DefaultSpeed = defaultSpeed;
            MaxSpeed = maxSpeed;
        }

        /// <summary>
        /// Returns a new SpeedConstraint clamped to the given ceiling.
        /// Used when the host needs to impose a temporary speed restriction
        /// below the move's defined maximum — for example in congested zones.
        /// </summary>
        public SpeedConstraint ClampTo(decimal speedCeiling)
        {
            if (speedCeiling <= 0)
                throw new ArgumentOutOfRangeException(nameof(speedCeiling),
                    "Speed ceiling must be greater than zero.");

            var clampedDefault = Math.Min(DefaultSpeed, speedCeiling);
            var clampedMax = Math.Min(MaxSpeed, speedCeiling);

            // Ensure default doesn't exceed the new clamped max
            clampedDefault = Math.Min(clampedDefault, clampedMax);

            return new SpeedConstraint(clampedDefault, clampedMax);
        }

        /// <summary>
        /// Validates that a requested speed does not exceed this constraint.
        /// Returns the effective speed — the lesser of requested and max.
        /// </summary>
        public decimal EffectiveSpeed(decimal requestedSpeed)
            => Math.Min(requestedSpeed, MaxSpeed);

        /// <summary>
        /// True if default and max speeds are identical —
        /// no variation is permitted on this move.
        /// </summary>
        public bool IsFixed => DefaultSpeed == MaxSpeed;

        public bool Equals(SpeedConstraint? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return DefaultSpeed == other.DefaultSpeed
                && MaxSpeed == other.MaxSpeed;
        }

        public override bool Equals(object? obj)
            => obj is SpeedConstraint other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(DefaultSpeed, MaxSpeed);

        public static bool operator ==(SpeedConstraint? left,
                                        SpeedConstraint? right)
            => left?.Equals(right) ?? right is null;

        public static bool operator !=(SpeedConstraint? left,
                                        SpeedConstraint? right)
            => !(left == right);

        public override string ToString()
            => $"Default={DefaultSpeed:F4} m/s, Max={MaxSpeed:F4} m/s";
    }
}