using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;
using AGV.Core.Interfaces;
using AGV.Core.Logging;
using AGV.Core.Messages;
using AGV.Fleet.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AGV.Fleet.Services
{
    /// <summary>
    /// Implements IChargeQueueManager — manages all three charging modes.
    ///
    /// Single ownership: ChargeQueueManagerService is the sole writer
    /// of charge slot assignment state. Fleet manager and MQTT publisher
    /// read charge state via IChargeQueueManager interface only.
    ///
    /// Three charging modes (NYT College Point):
    ///   Opportunity: 15 inline FIFO slots (9 lower + 6 upper corridor)
    ///   Mandatory:   12 discrete stations (upper right zone)
    ///   Maintenance: Periodic full drain + recharge (lead-acid health)
    ///
    /// Evaluation cycle:
    ///   Runs on a timer — evaluates all idle vehicles for charging need.
    ///   Also triggered by VehicleStateUpdate messages when SOC changes.
    /// </summary>
    public sealed class ChargeQueueManagerService
        : BackgroundService, IChargeQueueManager
    {
        private readonly VehicleRegistry _registry;
        private readonly ChannelRegistry _channels;
        private readonly ChargingThresholds _thresholds;
        private readonly ILogger _logger;

        // Opportunity charge FIFO queue
        // Key: charge NodeId, Value: vehicleId occupying it (null=free)
        private readonly Dictionary<int, int?> _opportunitySlots = new();

        // FIFO wait queue for opportunity charging
        private readonly Queue<int> _opportunityWaitQueue = new();

        // Mandatory charge stations
        // Key: charge NodeId, Value: vehicleId occupying it (null=free)
        private readonly Dictionary<int, int?> _mandatorySlots = new();

        // Maintenance cycle tracking
        // Key: vehicleId, Value: last maintenance cycle completion UTC
        private readonly Dictionary<int, DateTime> _lastMaintenance = new();
        private readonly HashSet<int> _maintenanceScheduled = new();

        private readonly object _chargeLock = new();

        // Evaluation interval
        private readonly TimeSpan _evaluationInterval =
            TimeSpan.FromSeconds(15);

        public ChargeQueueManagerService(
            VehicleRegistry registry,
            ChannelRegistry channels,
            ChargingThresholds thresholds,
            ILoggerFactory loggerFactory)
        {
            _registry = registry;
            _channels = channels;
            _thresholds = thresholds;
            _logger = loggerFactory.CreateLogger(LogDomains.Charging);
        }

        // ----------------------------------------------------------------
        // BackgroundService
        // ----------------------------------------------------------------

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "ChargeQueueManagerService starting — " +
                "opportunity slots: {Opp}, mandatory stations: {Mand}",
                _opportunitySlots.Count,
                _mandatorySlots.Count);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_evaluationInterval, stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;
                EvaluateAllVehicles();
            }

            _logger.LogInformation(
                "ChargeQueueManagerService stopped");
        }

        // ----------------------------------------------------------------
        // Slot initialization (called by Host startup)
        // ----------------------------------------------------------------

        /// <summary>
        /// Initializes opportunity charge slots from the road map.
        /// Called once at startup by the host wiring.
        /// </summary>
        public void InitializeOpportunitySlots(
            IEnumerable<int> slotNodeIds)
        {
            lock (_chargeLock)
            {
                _opportunitySlots.Clear();
                foreach (var nodeId in slotNodeIds)
                    _opportunitySlots[nodeId] = null;

                _logger.LogInformation(
                    "Opportunity charge slots initialized: {Count}",
                    _opportunitySlots.Count);
            }
        }

        /// <summary>
        /// Initializes mandatory charge stations from the road map.
        /// </summary>
        public void InitializeMandatoryStations(
            IEnumerable<int> stationNodeIds)
        {
            lock (_chargeLock)
            {
                _mandatorySlots.Clear();
                foreach (var nodeId in stationNodeIds)
                    _mandatorySlots[nodeId] = null;

                _logger.LogInformation(
                    "Mandatory charge stations initialized: {Count}",
                    _mandatorySlots.Count);
            }
        }

        // ----------------------------------------------------------------
        // IChargeQueueManager implementation
        // ----------------------------------------------------------------

        public async Task<ChargeAssignment?> EvaluateChargingNeedAsync(
            int vehicleId,
            decimal currentSoc,
            int currentNodeId,
            CancellationToken cancellationToken = default)
        {
            var vehicle = _registry.GetById(vehicleId);
            if (vehicle is null) return null;

            // Mandatory charge takes priority over opportunity
            if (currentSoc < _thresholds.MandatoryEnterSoc)
            {
                var station = await RequestMandatoryStationAsync(
                    vehicleId, cancellationToken);
                if (station.HasValue)
                {
                    return new ChargeAssignment
                    {
                        VehicleId = vehicleId,
                        SerialNumber = vehicle.SerialNumber,
                        ChargeNodeId = station.Value,
                        ChargeType = ChargeType.Mandatory,
                    };
                }
            }
            else if (currentSoc < _thresholds.OpportunityEnterSoc)
            {
                var slot = await RequestOpportunitySlotAsync(
                    vehicleId, cancellationToken);
                if (slot.HasValue)
                {
                    return new ChargeAssignment
                    {
                        VehicleId = vehicleId,
                        SerialNumber = vehicle.SerialNumber,
                        ChargeNodeId = slot.Value,
                        ChargeType = ChargeType.Opportunity,
                        OpportunityExitSocThreshold =
                            _thresholds.OpportunityExitSoc,
                    };
                }
            }

            // Check maintenance cycle
            if (IsMaintenanceDue(vehicleId))
            {
                await ScheduleMaintenanceCycleAsync(
                    vehicleId, cancellationToken);
            }

            return null;
        }

        public Task<int?> RequestOpportunitySlotAsync(
            int vehicleId,
            CancellationToken cancellationToken = default)
        {
            lock (_chargeLock)
            {
                // Check if already assigned
                var existing = GetVehicleChargeSlot(vehicleId);
                if (existing.HasValue)
                    return Task.FromResult<int?>(existing.Value);

                // Find first free slot
                foreach (var kvp in _opportunitySlots)
                {
                    if (kvp.Value is null)
                    {
                        _opportunitySlots[kvp.Key] = vehicleId;
                        _logger.LogInformation(
                            "Vehicle {VehicleId} assigned opportunity " +
                            "charge slot {NodeId}",
                            vehicleId, kvp.Key);
                        return Task.FromResult<int?>(kvp.Key);
                    }
                }

                // No slot available — add to wait queue
                if (!_opportunityWaitQueue.Contains(vehicleId))
                {
                    _opportunityWaitQueue.Enqueue(vehicleId);
                    _logger.LogInformation(
                        "Vehicle {VehicleId} queued for opportunity " +
                        "charge (queue depth: {Depth})",
                        vehicleId, _opportunityWaitQueue.Count);
                }

                return Task.FromResult<int?>(null);
            }
        }

        public Task<int?> RequestMandatoryStationAsync(
            int vehicleId,
            CancellationToken cancellationToken = default)
        {
            lock (_chargeLock)
            {
                var existing = GetVehicleChargeSlot(vehicleId);
                if (existing.HasValue)
                    return Task.FromResult<int?>(existing.Value);

                foreach (var kvp in _mandatorySlots)
                {
                    if (kvp.Value is null)
                    {
                        _mandatorySlots[kvp.Key] = vehicleId;
                        _logger.LogInformation(
                            "Vehicle {VehicleId} assigned mandatory " +
                            "charge station {NodeId} " +
                            "(SOC={SOC}%)",
                            vehicleId, kvp.Key,
                            _registry.GetById(vehicleId)
                                ?.BatteryStateOfCharge
                                .ToString("F1") ?? "?");
                        return Task.FromResult<int?>(kvp.Key);
                    }
                }

                _logger.LogWarning(
                    "Vehicle {VehicleId} needs mandatory charge " +
                    "but all {Count} stations occupied",
                    vehicleId, _mandatorySlots.Count);

                return Task.FromResult<int?>(null);
            }
        }

        public Task ReleaseSlotAsync(
            int vehicleId,
            int chargeNodeId,
            CancellationToken cancellationToken = default)
        {
            lock (_chargeLock)
            {
                // Release opportunity slot
                if (_opportunitySlots.ContainsKey(chargeNodeId))
                {
                    _opportunitySlots[chargeNodeId] = null;
                    _logger.LogInformation(
                        "Vehicle {VehicleId} released opportunity " +
                        "charge slot {NodeId}",
                        vehicleId, chargeNodeId);

                    // Advance wait queue
                    if (_opportunityWaitQueue.Count > 0)
                    {
                        var next = _opportunityWaitQueue.Dequeue();
                        _opportunitySlots[chargeNodeId] = next;
                        _logger.LogInformation(
                            "Vehicle {VehicleId} advanced from " +
                            "wait queue to slot {NodeId}",
                            next, chargeNodeId);
                    }
                }
                // Release mandatory station
                else if (_mandatorySlots.ContainsKey(chargeNodeId))
                {
                    _mandatorySlots[chargeNodeId] = null;
                    _logger.LogInformation(
                        "Vehicle {VehicleId} released mandatory " +
                        "charge station {NodeId}",
                        vehicleId, chargeNodeId);

                    // Record maintenance completion if applicable
                    if (_maintenanceScheduled.Contains(vehicleId))
                    {
                        _maintenanceScheduled.Remove(vehicleId);
                        _lastMaintenance[vehicleId] = DateTime.UtcNow;
                        _logger.LogInformation(
                            "Vehicle {VehicleId} maintenance " +
                            "cycle complete",
                            vehicleId);
                    }
                }
            }

            return Task.CompletedTask;
        }

        public bool IsVehicleCharging(int vehicleId)
            => GetVehicleChargeSlot(vehicleId).HasValue;

        public int? GetVehicleChargeSlot(int vehicleId)
        {
            lock (_chargeLock)
            {
                foreach (var kvp in _opportunitySlots)
                    if (kvp.Value == vehicleId) return kvp.Key;
                foreach (var kvp in _mandatorySlots)
                    if (kvp.Value == vehicleId) return kvp.Key;
                return null;
            }
        }

        public int OpportunityQueueDepth
        {
            get { lock (_chargeLock) return _opportunityWaitQueue.Count; }
        }

        public int MandatoryStationsOccupied
        {
            get
            {
                lock (_chargeLock)
                    return _mandatorySlots.Values.Count(v => v.HasValue);
            }
        }

        public int MandatoryStationsAvailable
        {
            get
            {
                lock (_chargeLock)
                    return _mandatorySlots.Values.Count(v => !v.HasValue);
            }
        }

        public Task ScheduleMaintenanceCycleAsync(
            int vehicleId,
            CancellationToken cancellationToken = default)
        {
            lock (_chargeLock)
            {
                if (!_maintenanceScheduled.Contains(vehicleId))
                {
                    _maintenanceScheduled.Add(vehicleId);
                    _logger.LogInformation(
                        "Maintenance cycle scheduled for " +
                        "vehicle {VehicleId}",
                        vehicleId);
                }
            }
            return Task.CompletedTask;
        }

        public bool IsMaintenanceCycleScheduled(int vehicleId)
        {
            lock (_chargeLock)
                return _maintenanceScheduled.Contains(vehicleId);
        }

        public ChargingThresholds GetThresholds() => _thresholds;

        // ----------------------------------------------------------------
        // Private
        // ----------------------------------------------------------------

        private void EvaluateAllVehicles()
        {
            var candidates = _registry.GetInService()
                .Where(v => v.IsOnline
                         && v.OrderState == Core.Enums.OrderState.Idle
                         && !IsVehicleCharging(v.VehicleId))
                .ToList();

            foreach (var vehicle in candidates)
            {
                _ = EvaluateChargingNeedAsync(
                    vehicle.VehicleId,
                    vehicle.BatteryStateOfCharge,
                    vehicle.CurrentNodeId ?? 0);
            }
        }

        private bool IsMaintenanceDue(int vehicleId)
        {
            lock (_chargeLock)
            {
                if (_maintenanceScheduled.Contains(vehicleId))
                    return false; // already scheduled

                if (!_lastMaintenance.TryGetValue(vehicleId,
                    out var lastCycle))
                    return false; // no history yet — don't force first cycle

                var daysSince = (DateTime.UtcNow - lastCycle).TotalDays;
                return daysSince >= _thresholds.MaintenanceCycleIntervalDays;
            }
        }
    }
}
