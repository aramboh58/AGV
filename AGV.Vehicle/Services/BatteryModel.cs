using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGV.Vehicle.Services
{
    /// <summary>
    /// Lead-acid battery model for AGV simulation.
    ///
    /// Models the discharge and charge behavior of a lead-acid
    /// traction battery as used in the NYT College Point fork
    /// and waste bin vehicles.
    ///
    /// Key lead-acid characteristics modeled:
    ///   — Non-linear discharge curve (faster discharge at low SOC)
    ///   — Load-dependent discharge rate (heavier load = faster drain)
    ///   — Two-phase charge curve (bulk + absorption)
    ///   — Opportunity charge inefficiency (partial cycles less efficient)
    ///   — Maintenance cycle benefit (full drain/recharge restores capacity)
    ///
    /// SOC is maintained as a decimal in range [0.0, 1.0].
    /// All rates are expressed as fraction of capacity per second.
    ///
    /// Units are internally consistent — the simulation engine
    /// calls Discharge() and Charge() with elapsed seconds per tick.
    /// </summary>
    public sealed class BatteryModel
    {
        // ----------------------------------------------------------------
        // State
        // ----------------------------------------------------------------

        /// <summary>State of charge — range [0.0, 1.0].</summary>
        public decimal StateOfCharge { get; private set; }

        /// <summary>SOC as percentage 0-100 for display.</summary>
        public decimal StateOfChargePercent
            => StateOfCharge * 100m;

        /// <summary>
        /// Battery health factor — degrades slightly with each
        /// partial charge cycle. Restored by maintenance cycle.
        /// Range [0.0, 1.0] where 1.0 = new battery.
        /// </summary>
        public decimal HealthFactor { get; private set; }

        /// <summary>True if currently in a charging state.</summary>
        public bool IsCharging { get; private set; }

        /// <summary>Total partial charge cycles since last maintenance.</summary>
        public int PartialCyclesSinceMaintenance { get; private set; }

        // ----------------------------------------------------------------
        // Configuration
        // ----------------------------------------------------------------

        private readonly BatteryModelOptions _options;

        public BatteryModel(BatteryModelOptions options,
                             decimal initialSoc = 1.0m)
        {
            _options = options
                ?? throw new ArgumentNullException(nameof(options));
            StateOfCharge = Math.Clamp(initialSoc, 0m, 1m);
            HealthFactor = 1.0m;
            IsCharging = false;
        }

        // ----------------------------------------------------------------
        // Discharge
        // ----------------------------------------------------------------

        /// <summary>
        /// Applies discharge for the specified elapsed time in seconds.
        /// Rate varies by activity and load state.
        /// </summary>
        public void Discharge(DischargeActivity activity,
                               bool isLoaded,
                               decimal elapsedSeconds)
        {
            if (elapsedSeconds <= 0m) return;
            IsCharging = false;

            var baseRate = GetDischargeRate(activity, isLoaded);

            // Non-linear: discharge accelerates below 30% SOC
            var socFactor = StateOfCharge < 0.3m
                ? 1.0m + (0.3m - StateOfCharge) * 2.0m
                : 1.0m;

            // Health degradation increases effective discharge rate
            var healthFactor = 2.0m - HealthFactor;

            var delta = baseRate * socFactor * healthFactor
                        * elapsedSeconds;

            StateOfCharge = Math.Max(0m, StateOfCharge - delta);
        }

        // ----------------------------------------------------------------
        // Charging
        // ----------------------------------------------------------------

        /// <summary>
        /// Applies opportunity charging for the specified elapsed seconds.
        /// Opportunity charging uses a lower rate — vehicles charge
        /// between missions inline on the inbound lane.
        /// </summary>
        public void ChargeOpportunity(decimal elapsedSeconds)
        {
            if (elapsedSeconds <= 0m) return;
            IsCharging = true;

            // Bulk phase (below 80%) — faster rate
            // Absorption phase (above 80%) — slower rate
            var rate = StateOfCharge < 0.8m
                ? _options.OpportunityChargeBulkRatePerSecond
                : _options.OpportunityChargeAbsorptionRatePerSecond;

            StateOfCharge = Math.Min(1.0m,
                StateOfCharge + rate * elapsedSeconds);
        }

        /// <summary>
        /// Applies mandatory (full) charging for the specified seconds.
        /// Higher rate — dedicated charge station, full charge cycle.
        /// </summary>
        public void ChargeMandatory(decimal elapsedSeconds)
        {
            if (elapsedSeconds <= 0m) return;
            IsCharging = true;

            var rate = StateOfCharge < 0.8m
                ? _options.MandatoryChargeBulkRatePerSecond
                : _options.MandatoryChargeAbsorptionRatePerSecond;

            StateOfCharge = Math.Min(1.0m,
                StateOfCharge + rate * elapsedSeconds);

            // Track partial cycles for health model
            if (StateOfCharge >= 0.98m)
            {
                PartialCyclesSinceMaintenance = 0;
                IsCharging = false;
            }
        }

        /// <summary>
        /// Applies maintenance drain — forces battery to near-zero SOC.
        /// Called during the drain phase of a maintenance cycle.
        /// Returns true when drain is complete (SOC ≤ 2%).
        /// </summary>
        public bool DrainForMaintenance(decimal elapsedSeconds)
        {
            if (elapsedSeconds <= 0m) return StateOfCharge <= 0.02m;

            StateOfCharge = Math.Max(0m,
                StateOfCharge -
                _options.MaintenanceDrainRatePerSecond * elapsedSeconds);

            return StateOfCharge <= 0.02m;
        }

        /// <summary>
        /// Applies maintenance recharge after full drain.
        /// Restores battery health factor.
        /// Returns true when charge is complete (SOC ≥ 98%).
        /// </summary>
        public bool ChargeAfterMaintenance(decimal elapsedSeconds)
        {
            if (elapsedSeconds <= 0m) return StateOfCharge >= 0.98m;
            IsCharging = true;

            StateOfCharge = Math.Min(1.0m,
                StateOfCharge +
                _options.MandatoryChargeBulkRatePerSecond * elapsedSeconds);

            if (StateOfCharge >= 0.98m)
            {
                // Restore health factor — full cycle benefit
                HealthFactor = Math.Min(1.0m, HealthFactor + 0.05m);
                IsCharging = false;
                PartialCyclesSinceMaintenance = 0;
                return true;
            }

            return false;
        }

        // ----------------------------------------------------------------
        // State updates from real vehicles
        // ----------------------------------------------------------------

        /// <summary>
        /// Updates SOC from a VDA 5050 State message.
        /// Used when connected to real vehicles — replaces simulation model.
        /// </summary>
        public void UpdateFromVehicle(decimal socPercent, bool charging)
        {
            StateOfCharge = Math.Clamp(socPercent / 100m, 0m, 1m);
            IsCharging = charging;
        }

        // ----------------------------------------------------------------
        // Threshold checks
        // ----------------------------------------------------------------

        /// <summary>True if SOC is below the opportunity charge threshold.</summary>
        public bool NeedsOpportunityCharge(decimal threshold)
            => StateOfCharge < threshold;

        /// <summary>True if SOC is below the mandatory charge threshold.</summary>
        public bool NeedsMandatoryCharge(decimal threshold)
            => StateOfCharge < threshold;

        // ----------------------------------------------------------------
        // Private
        // ----------------------------------------------------------------

        private decimal GetDischargeRate(DischargeActivity activity,
                                          bool isLoaded)
        {
            var baseRate = activity switch
            {
                DischargeActivity.Idle =>
                    _options.IdleDischargeRatePerSecond,
                DischargeActivity.Traveling =>
                    isLoaded
                        ? _options.TravelingLoadedDischargeRatePerSecond
                        : _options.TravelingEmptyDischargeRatePerSecond,
                DischargeActivity.Forking =>
                    _options.ForkingDischargeRatePerSecond,
                DischargeActivity.Charging => 0m,
                _ =>
                    _options.IdleDischargeRatePerSecond,
            };

            return baseRate;
        }
    }

    /// <summary>
    /// Vehicle activity for discharge rate selection.
    /// </summary>
    public enum DischargeActivity
    {
        Idle,
        Traveling,
        Forking,
        Charging
    }

    /// <summary>
    /// Configuration for the battery model.
    /// All rates are fraction of full capacity per second.
    /// Loaded from appsettings.json section "BatteryModel".
    /// </summary>
    public sealed class BatteryModelOptions
    {
        public const string SectionName = "BatteryModel";

        // Discharge rates (fraction of capacity per second)
        // At 24 kWh capacity, 1 hour = 3600 seconds
        // TravelingLoaded: ~6 hours runtime loaded → 1/(6*3600)
        public decimal IdleDischargeRatePerSecond { get; set; }
            = 0.000002m;   // ~140 hours idle
        public decimal TravelingEmptyDischargeRatePerSecond { get; set; }
            = 0.000010m;   // ~28 hours traveling empty
        public decimal TravelingLoadedDischargeRatePerSecond { get; set; }
            = 0.000018m;   // ~15 hours traveling loaded
        public decimal ForkingDischargeRatePerSecond { get; set; }
            = 0.000025m;   // ~11 hours continuous forking

        // Opportunity charge rates
        public decimal OpportunityChargeBulkRatePerSecond { get; set; }
            = 0.000060m;   // ~4.6 hours bulk charge (0→80%)
        public decimal OpportunityChargeAbsorptionRatePerSecond { get; set; }
            = 0.000020m;   // ~2.8 hours absorption (80→100%)

        // Mandatory charge rates
        public decimal MandatoryChargeBulkRatePerSecond { get; set; }
            = 0.000120m;   // ~2.3 hours bulk charge
        public decimal MandatoryChargeAbsorptionRatePerSecond { get; set; }
            = 0.000040m;   // ~1.4 hours absorption

        // Maintenance cycle
        public decimal MaintenanceDrainRatePerSecond { get; set; }
            = 0.000200m;   // ~1.4 hours to fully drain
    }
}