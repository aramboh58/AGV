using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Routing.Services
{
    /// <summary>
    /// Defines the cost of heading changes between moves.
    ///
    /// The pose-space A* algorithm expands the state space to include
    /// vehicle heading at each node — (NodeId, HeadingBucket) pairs.
    /// This means routing considers not just which nodes to visit but
    /// the heading the vehicle arrives at each node with.
    ///
    /// Turn costs are added to the g(n) cost function when the vehicle
    /// must change heading between an incoming and outgoing move.
    /// A route that avoids turns is preferred over a shorter route
    /// that requires multiple heading changes — reflecting the real
    /// operational cost of turns (time, wear, traffic disruption).
    ///
    /// Heading buckets:
    ///   Rather than treating every 0.0001° as a distinct heading,
    ///   headings are bucketed into discrete categories for the
    ///   pose-space expansion. The bucket size is configurable.
    ///   Default: 45° buckets (8 directions).
    ///
    /// Turn cost categories:
    ///   No turn        (0°):    0.0 seconds added
    ///   Shallow turn   (&lt;45°):  configurable (default 2.0s)
    ///   Quarter turn   (45°):   configurable (default 5.0s)
    ///   Three-quarter  (90°):   configurable (default 10.0s)
    ///   Half turn      (180°):  configurable (default 15.0s)
    ///
    /// All values are in seconds — consistent with the time-optimal
    /// A* cost function (g(n) = travel time + turn costs).
    /// </summary>
    public sealed class TurnCostTable
    {
        private readonly TurnCostOptions _options;

        public TurnCostTable(TurnCostOptions options)
        {
            _options = options
                ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Returns the turn cost in seconds for the heading change
        /// between an incoming move's EndHeading and an outgoing
        /// move's StartHeading.
        ///
        /// Both headings are in signed degrees (-180 to +180).
        /// The absolute angular difference drives the cost tier.
        /// </summary>
        public double GetTurnCost(decimal incomingHeading,
                                   decimal outgoingHeading)
        {
            var delta = Math.Abs(NormalizeHeadingDelta(
                (double)(outgoingHeading - incomingHeading)));

            return delta switch
            {
                0.0 => 0.0,
                < 22.5 => _options.ShallowTurnCostSeconds,
                >= 22.5 and < 67.5 => _options.QuarterTurnCostSeconds,
                >= 67.5 and < 112.5 => _options.ThreeQuarterTurnCostSeconds,
                >= 112.5 and < 157.5 => _options.HalfTurnCostSeconds,
                _ => _options.ReverseTurnCostSeconds
            };
        }

        /// <summary>
        /// Returns the heading bucket index for a given heading.
        /// Used by the pose-space graph to group similar headings.
        /// </summary>
        public int GetHeadingBucket(decimal heading)
        {
            var normalized = ((double)heading + 360.0) % 360.0;
            return (int)(normalized / _options.BucketSizeDegrees) %
                   _options.BucketCount;
        }

        /// <summary>
        /// Returns the representative heading (degrees) for a bucket.
        /// Used when constructing the pose-expanded adjacency graph.
        /// </summary>
        public double GetBucketCenterHeading(int bucket)
            => bucket * _options.BucketSizeDegrees +
               _options.BucketSizeDegrees / 2.0;

        /// <summary>Total number of heading buckets.</summary>
        public int BucketCount => _options.BucketCount;

        /// <summary>
        /// Normalizes a heading delta to the range [-180, +180].
        /// </summary>
        private static double NormalizeHeadingDelta(double delta)
        {
            while (delta > 180.0) delta -= 360.0;
            while (delta < -180.0) delta += 360.0;
            return delta;
        }
    }

    /// <summary>
    /// Configuration options for turn cost calculation.
    /// Loaded from appsettings.json section "Routing:TurnCosts".
    /// All costs are in seconds.
    /// </summary>
    public sealed class TurnCostOptions
    {
        public const string SectionName = "Routing:TurnCosts";

        /// <summary>
        /// Heading bucket size in degrees.
        /// Default: 45.0 (8 buckets covering full 360°).
        /// </summary>
        public double BucketSizeDegrees { get; set; } = 45.0;

        /// <summary>Total number of heading buckets (360 / BucketSizeDegrees).</summary>
        public int BucketCount => (int)(360.0 / BucketSizeDegrees);

        /// <summary>Cost for heading change less than 22.5°. Default: 2.0s.</summary>
        public double ShallowTurnCostSeconds { get; set; } = 2.0;

        /// <summary>Cost for ~45° heading change. Default: 5.0s.</summary>
        public double QuarterTurnCostSeconds { get; set; } = 5.0;

        /// <summary>Cost for ~90° heading change. Default: 10.0s.</summary>
        public double ThreeQuarterTurnCostSeconds { get; set; } = 10.0;

        /// <summary>Cost for ~135° heading change. Default: 15.0s.</summary>
        public double HalfTurnCostSeconds { get; set; } = 15.0;

        /// <summary>
        /// Cost for near-180° heading change (reverse direction).
        /// Default: 20.0s — significantly penalized.
        /// </summary>
        public double ReverseTurnCostSeconds { get; set; } = 20.0;
    }
}