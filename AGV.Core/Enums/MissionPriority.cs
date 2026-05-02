using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Core.Enums
{
    /// <summary>
    /// Mission dispatch and traffic management priority levels.
    ///
    /// Priority governs two distinct behaviors:
    ///
    ///   1. Dispatch queue ordering — higher priority missions are
    ///      dispatched before lower priority missions when multiple
    ///      missions are pending and vehicles are available.
    ///
    ///   2. Lock contention resolution — when multiple vehicles are
    ///      waiting on the same locked node, the highest priority
    ///      waiting vehicle wins the atomic check+lock when the
    ///      node becomes free. Not first-come-first-served.
    ///
    /// Priority travels with the mission through assignment, transfer,
    /// and swap — always available to traffic management without
    /// requiring a separate lookup.
    /// </summary>
    public enum MissionPriority
    {
        /// <summary>
        /// Emergency — e-stop recovery, maintenance extraction,
        /// safety-critical movements. Highest dispatch priority
        /// and lock contention advantage.
        /// </summary>
        Emergency = 1,

        /// <summary>
        /// Time-critical — press deadline approaching, conveyor
        /// queue backing up, pickup deadline imminent.
        /// </summary>
        TimeCritical = 2,

        /// <summary>
        /// Normal — standard mission execution under normal
        /// production conditions.
        /// </summary>
        Normal = 3,

        /// <summary>
        /// Park or opportunity charge — lowest priority.
        /// Yields lock contention to all mission-bearing vehicles.
        /// </summary>
        ParkOrCharge = 4
    }
}