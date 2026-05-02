using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Messages;

namespace AGV.Core.Interfaces
{
    /// <summary>
    /// Contract for the charge queue manager service.
    ///
    /// The charge queue manager is the sole owner of charge slot
    /// assignment state. It manages three distinct charging modes
    /// that reflect the operational reality of lead-acid battery
    /// AGV fleets:
    ///
    ///   Opportunity charging (FIFO inline queue):
    ///     Vehicles charge between missions when SOC drops below
    ///     the opportunity threshold. Multiple charge positions
    ///     exist inline on the inbound lanes. Vehicles queue FIFO
    ///     and exit when SOC reaches the exit threshold.
    ///     For NYT College Point: 15 positions (9 lower + 6 upper).
    ///
    ///   Mandatory charging (discrete stations):
    ///     Vehicles are directed to dedicated charge stations when
    ///     SOC falls below the mandatory threshold. One vehicle per
    ///     station. Vehicle charges to ~100% before returning.
    ///     For NYT College Point: 12 stations.
    ///
    ///   Maintenance cycle (scheduled):
    ///     Full drain followed by full recharge. Maintains lead-acid
    ///     battery health. Scheduled periodically per vehicle.
    ///     Performed at maintenance-designated stations.
    ///
    /// Single ownership principle:
    ///   ChargeQueueManagerService is the sole writer of charge slot
    ///   assignments. Fleet manager and vehicle adapter read charge
    ///   state via this interface only.
    /// </summary>
    public interface IChargeQueueManager
    {
        /// <summary>
        /// Evaluates whether a vehicle needs charging and if so,
        /// assigns the appropriate charge slot and type.
        ///
        /// Called by the fleet manager after each mission completion
        /// and on each idle vehicle evaluation cycle.
        ///
        /// Returns a ChargeAssignment if a slot was assigned,
        /// or null if the vehicle does not need charging or no
        /// slot is currently available.
        /// </summary>
        Task<ChargeAssignment?> EvaluateChargingNeedAsync(
            int vehicleId,
            decimal currentSoc,
            int currentNodeId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Requests a specific opportunity charge slot for a vehicle.
        /// Returns the assigned slot NodeId, or null if all slots
        /// are occupied and the vehicle is added to the wait queue.
        ///
        /// The FIFO queue ensures vehicles charge in arrival order —
        /// no priority jumping in the opportunity charge queue.
        /// </summary>
        Task<int?> RequestOpportunitySlotAsync(
            int vehicleId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Requests a mandatory charge station for a vehicle.
        /// Returns the assigned station NodeId, or null if all
        /// stations are occupied.
        /// </summary>
        Task<int?> RequestMandatoryStationAsync(
            int vehicleId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Releases a charge slot when a vehicle has finished charging
        /// and is returning to service.
        ///
        /// For opportunity slots: advances the next vehicle in the
        /// FIFO queue into the released slot.
        ///
        /// For mandatory stations: marks station as available.
        /// </summary>
        Task ReleaseSlotAsync(
            int vehicleId,
            int chargeNodeId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns true if the specified vehicle is currently assigned
        /// to a charge slot of any type.
        /// </summary>
        bool IsVehicleCharging(int vehicleId);

        /// <summary>
        /// Returns the NodeId of the charge slot currently assigned
        /// to the specified vehicle. Returns null if not charging.
        /// </summary>
        int? GetVehicleChargeSlot(int vehicleId);

        /// <summary>
        /// Returns the current depth of the opportunity charge
        /// wait queue — vehicles waiting for a slot to open.
        /// Used by the dashboard and fleet manager for load balancing.
        /// </summary>
        int OpportunityQueueDepth { get; }

        /// <summary>
        /// Returns the number of mandatory charge stations currently
        /// occupied.
        /// </summary>
        int MandatoryStationsOccupied { get; }

        /// <summary>
        /// Returns the number of mandatory charge stations available.
        /// </summary>
        int MandatoryStationsAvailable { get; }

        /// <summary>
        /// Schedules a maintenance cycle for a vehicle.
        /// The vehicle will be directed to a maintenance station
        /// at the next available opportunity (typically when it
        /// would otherwise park idle).
        ///
        /// Maintenance cycles are tracked per vehicle to ensure
        /// each vehicle undergoes the full drain/recharge cycle
        /// on the configured interval (typically weekly for
        /// lead-acid batteries).
        /// </summary>
        Task ScheduleMaintenanceCycleAsync(
            int vehicleId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns true if a maintenance cycle is scheduled or
        /// currently in progress for the specified vehicle.
        /// </summary>
        bool IsMaintenanceCycleScheduled(int vehicleId);

        /// <summary>
        /// Returns the SOC thresholds currently configured.
        /// </summary>
        ChargingThresholds GetThresholds();
    }

    /// <summary>
    /// SOC threshold configuration for the three charging modes.
    /// Loaded from appsettings.json and injectable for testing.
    /// </summary>
    public sealed class ChargingThresholds
    {
        /// <summary>
        /// SOC below which a vehicle enters the opportunity
        /// charge queue. Default: 75%.
        /// </summary>
        public decimal OpportunityEnterSoc { get; init; } = 75m;

        /// <summary>
        /// SOC above which a vehicle exits opportunity charging
        /// and returns to service. Default: 85%.
        /// </summary>
        public decimal OpportunityExitSoc { get; init; } = 85m;

        /// <summary>
        /// SOC below which a vehicle is directed to a mandatory
        /// charge station. Overrides opportunity charging.
        /// Default: 30%.
        /// </summary>
        public decimal MandatoryEnterSoc { get; init; } = 30m;

        /// <summary>
        /// SOC at which mandatory charging is considered complete.
        /// Default: 98%.
        /// </summary>
        public decimal MandatoryExitSoc { get; init; } = 98m;

        /// <summary>
        /// Interval in days between maintenance cycle events
        /// per vehicle. Default: 7 days (weekly).
        /// </summary>
        public int MaintenanceCycleIntervalDays { get; init; } = 7;
    }
}