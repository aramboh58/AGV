using System.Threading;

namespace AGV.Fleet.Infrastructure
{
    /// <summary>
    /// Process-wide counters for deadlock handling. Written by the traffic
    /// manager and the fleet manager, read by the dev-only test harness to
    /// check what actually happened.
    /// </summary>
    public static class DeadlockMetrics
    {
        private static int _detected;
        private static int _resolved;
        private static int _unresolved;

        /// <summary>Distinct deadlocks first reported.</summary>
        public static int Detected => Volatile.Read(ref _detected);

        /// <summary>Deadlocks broken by re-routing a vehicle.</summary>
        public static int Resolved => Volatile.Read(ref _resolved);

        /// <summary>"No alternate route" operator alerts raised.</summary>
        public static int Unresolved => Volatile.Read(ref _unresolved);

        public static void RecordDetected() => Interlocked.Increment(ref _detected);
        public static void RecordResolved() => Interlocked.Increment(ref _resolved);
        public static void RecordUnresolved() => Interlocked.Increment(ref _unresolved);

        public static void Reset()
        {
            Volatile.Write(ref _detected, 0);
            Volatile.Write(ref _resolved, 0);
            Volatile.Write(ref _unresolved, 0);
        }
    }
}
