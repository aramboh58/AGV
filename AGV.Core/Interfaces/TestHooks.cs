using System;
using System.Threading;
using System.Threading.Tasks;

namespace AGV.Core.Interfaces
{
    /// <summary>
    /// Hooks the simulated vehicle adapter offers to the dev-only test
    /// harness. Not implemented by the MQTT adapter.
    /// </summary>
    public interface ISimulationTestHooks
    {
        /// <summary>
        /// Puts a simulated vehicle at a node: idle, unloaded, no order.
        /// With testMode true the vehicle simply goes idle at the end of
        /// an order (no pick/drop handling), until placed again with
        /// testMode false.
        /// </summary>
        Task PlaceVehicleAsync(
            int vehicleId,
            int nodeId,
            bool testMode,
            CancellationToken cancellationToken = default,
            bool keepLoad = false);

        /// <summary>True when the simulated vehicle has no order pending.</summary>
        bool IsVehicleAtRest(int vehicleId);

        /// <summary>The simulated vehicle's own current node (0 if unknown).</summary>
        int GetVehicleNodeId(int vehicleId);
    }

    /// <summary>
    /// Simulation settings the test harness reads and temporarily changes.
    /// </summary>
    public interface ISimulationControl
    {
        decimal SpeedFactor { get; set; }

        /// <summary>True while the background press-demand mission
        /// generator is switched on.</summary>
        bool PressDemandEnabled { get; }
    }
}
