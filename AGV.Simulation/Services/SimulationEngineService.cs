using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AGV.Simulation.Services
{
    /// <summary>
    /// Discrete event simulation engine — the main loop that drives
    /// all simulated vehicles forward in time.
    ///
    /// The engine runs on a background thread at a configurable tick
    /// rate. Each tick advances all simulated vehicles by the elapsed
    /// simulated time, which may be accelerated relative to wall clock
    /// time via the SpeedFactor setting.
    ///
    /// Example configurations:
    ///   SpeedFactor=1.0   → 1:1 realtime (useful for MQTT testing)
    ///   SpeedFactor=60.0  → 1 real second = 1 sim minute
    ///   SpeedFactor=300.0 → 1 real second = 5 sim minutes (demo mode)
    ///
    /// Press demand model:
    ///   Each press stand consumes rolls at a configurable rate.
    ///   Demand surges at edition change times (NYT production pattern).
    ///   When a stand's accumulator reaches 1.0 a roll delivery mission
    ///   is generated and enqueued with the fleet manager.
    ///
    /// The simulation engine does NOT generate missions directly —
    /// it signals demand via ISimulationDemandSource which the
    /// fleet manager consumes. This keeps the simulation layer
    /// cleanly separated from the host dispatch logic.
    /// </summary>
    public sealed class SimulationEngineService : BackgroundService
    {
        private readonly SimulatedVehicleAdapter _adapter;
        private readonly SimulationOptions _options;
        private readonly ILogger _logger;

        // Simulation time tracking
        private decimal _simTimeSeconds;
        private long _tickCount;

        // Press demand accumulators
        // Key: press stand node ID, Value: accumulated demand (0.0-1.0+)
        private readonly Dictionary<int, decimal> _demandAccumulators
            = new();

        public SimulationEngineService(
            SimulatedVehicleAdapter adapter,
            SimulationOptions options,
            ILoggerFactory loggerFactory)
        {
            _adapter = adapter;
            _options = options;
            _logger = loggerFactory.CreateLogger(LogDomains.Fleet);
        }

        // ----------------------------------------------------------------
        // Public state
        // ----------------------------------------------------------------

        /// <summary>Current simulated time in seconds.</summary>
        public decimal SimTimeSeconds => _simTimeSeconds;

        /// <summary>
        /// Simulated time formatted as HH:MM:SS.
        /// </summary>
        public string SimTimeFormatted
        {
            get
            {
                var t = (long)_simTimeSeconds;
                return $"{t / 3600:D2}:{(t % 3600) / 60:D2}:{t % 60:D2}";
            }
        }

        /// <summary>Total ticks executed.</summary>
        public long TickCount => _tickCount;

        /// <summary>Current simulation speed factor.</summary>
        public decimal SpeedFactor
        {
            get => _options.SpeedFactor;
            set => _options.SpeedFactor = Math.Max(0.1m,
                Math.Min(value, 3600m));
        }

        // ----------------------------------------------------------------
        // BackgroundService
        // ----------------------------------------------------------------

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "SimulationEngineService starting — " +
                "dt={Dt}s speed={Speed}x " +
                "({RealSecondsPerSimMinute:F1} real sec/sim min)",
                _options.TimeStepSeconds,
                _options.SpeedFactor,
                60m / _options.SpeedFactor);

            // Start the vehicle adapter
            await _adapter.StartAsync(stoppingToken);

            // Initialize demand accumulators
            InitializeDemandAccumulators();

            // Main simulation loop
            while (!stoppingToken.IsCancellationRequested)
            {
                var tickStart = DateTime.UtcNow;

                // Advance simulation
                await TickAsync(stoppingToken);

                // Sleep to maintain wall clock pacing
                var wallStepMs = (double)(_options.TimeStepSeconds
                    / _options.SpeedFactor * 1000m);
                var elapsed = (DateTime.UtcNow - tickStart)
                    .TotalMilliseconds;
                var sleepMs = Math.Max(0, wallStepMs - elapsed);

                if (sleepMs > 0)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(sleepMs),
                        stoppingToken);
                }
            }

            await _adapter.StopAsync(stoppingToken);
            _logger.LogInformation(
                "SimulationEngineService stopped — " +
                "sim time: {SimTime}, ticks: {Ticks}",
                SimTimeFormatted, _tickCount);
        }

        // ----------------------------------------------------------------
        // Tick
        // ----------------------------------------------------------------

        private async Task TickAsync(CancellationToken cancellationToken)
        {
            var dt = _options.TimeStepSeconds;
            _simTimeSeconds += dt;
            _tickCount++;

            // Advance all vehicles
            await _adapter.TickAsync(dt, cancellationToken);

            // Update press demand
            if (_options.EnablePressDemand)
            {
                UpdatePressDemand(dt);
            }

            // Periodic status log
            if (_tickCount % _options.StatusLogIntervalTicks == 0)
            {
                _logger.LogInformation(
                    "Simulation t={SimTime} " +
                    "ticks={Ticks}",
                    SimTimeFormatted, _tickCount);
            }
        }

        // ----------------------------------------------------------------
        // Press demand model (NYT College Point)
        // ----------------------------------------------------------------

        private void InitializeDemandAccumulators()
        {
            foreach (var nodeId in _options.PressStandNodeIds)
            {
                // Stagger initial accumulators to spread first missions
                _demandAccumulators[nodeId] =
                    (decimal)Random.Shared.NextDouble();
            }

            _logger.LogInformation(
                "Press demand initialized for {Count} press stands",
                _demandAccumulators.Count);
        }

        private void UpdatePressDemand(decimal elapsedSeconds)
        {
            var demandMultiplier = GetDemandMultiplier();
            var baseRate = _options.RollsPerStandPerHour
                / 3600m;  // rolls per second

            foreach (var nodeId in _demandAccumulators.Keys.ToList())
            {
                _demandAccumulators[nodeId] +=
                    baseRate * demandMultiplier * elapsedSeconds;

                if (_demandAccumulators[nodeId] >= 1.0m)
                {
                    _demandAccumulators[nodeId] -= 1.0m;
                    OnRollDemanded(nodeId);
                }
            }
        }

        /// <summary>
        /// Returns demand multiplier based on simulated time.
        /// Surges at edition change times — NYT production pattern.
        /// </summary>
        private decimal GetDemandMultiplier()
        {
            if (!_options.EnableEditionChangeSurges)
                return 1.0m;

            var simTimeOfDay = _simTimeSeconds % 86400m;

            foreach (var editionChangeTime in
                _options.EditionChangeTimes)
            {
                var delta = Math.Abs(
                    (double)(simTimeOfDay - editionChangeTime));
                var halfWindow = (double)_options.EditionSurgeDurationSeconds
                    / 2.0;

                if (delta < halfWindow)
                {
                    var proximity = 1.0m -
                        (decimal)(delta / halfWindow);
                    return 1.0m + _options.EditionSurgeMultiplier
                        * proximity;
                }
            }

            return 1.0m;
        }

        /// <summary>
        /// Called when a press stand demands a new roll.
        /// Raises event for the fleet manager to create a mission.
        /// </summary>
        private void OnRollDemanded(int pressStandNodeId)
        {
            RollDemanded?.Invoke(this,
                new RollDemandedEventArgs(pressStandNodeId));
        }

        /// <summary>
        /// Raised when a press stand needs a roll delivered.
        /// Subscribed by the simulation host wiring to create missions.
        /// </summary>
        public event EventHandler<RollDemandedEventArgs>? RollDemanded;
    }

    /// <summary>
    /// Event args for press stand roll demand.
    /// </summary>
    public sealed class RollDemandedEventArgs : EventArgs
    {
        public int PressStandNodeId { get; }
        public DateTime DemandedAt { get; }

        public RollDemandedEventArgs(int pressStandNodeId)
        {
            PressStandNodeId = pressStandNodeId;
            DemandedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Configuration for the simulation engine.
    /// Loaded from appsettings.json section "Simulation".
    /// </summary>
    public sealed class SimulationOptions
    {
        public const string SectionName = "Simulation";

        /// <summary>
        /// Simulated time step per tick in seconds.
        /// Default: 1.0 second per tick.
        /// </summary>
        public decimal TimeStepSeconds { get; set; } = 1.0m;

        /// <summary>
        /// Simulation speed relative to wall clock time.
        /// Default: 60x (1 real second = 1 sim minute).
        /// </summary>
        public decimal SpeedFactor { get; set; } = 60.0m;

        /// <summary>
        /// Whether to generate press roll demand missions.
        /// </summary>
        public bool EnablePressDemand { get; set; } = true;

        /// <summary>
        /// Roll consumption rate per press stand per hour.
        /// Default: 2.5 rolls/hour (typical NYT production run).
        /// </summary>
        public decimal RollsPerStandPerHour { get; set; } = 2.5m;

        /// <summary>
        /// Whether to model edition change demand surges.
        /// </summary>
        public bool EnableEditionChangeSurges { get; set; } = true;

        /// <summary>
        /// Edition change times in simulated seconds from midnight.
        /// NYT typical pattern: 7PM, 9PM, 11PM, 1AM, 3AM, 5AM.
        /// </summary>
        public List<decimal> EditionChangeTimes { get; set; } = new()
        {
            19 * 3600m,  // 7 PM
            21 * 3600m,  // 9 PM
            23 * 3600m,  // 11 PM
            1  * 3600m,  // 1 AM
            3  * 3600m,  // 3 AM
            5  * 3600m,  // 5 AM
        };

        /// <summary>
        /// Duration of edition change surge window in seconds.
        /// Default: 1800 seconds (30 minutes).
        /// </summary>
        public decimal EditionSurgeDurationSeconds { get; set; }
            = 1800m;

        /// <summary>
        /// Peak demand multiplier during edition change.
        /// Default: 0.6 → peaks at 1.6x normal demand.
        /// </summary>
        public decimal EditionSurgeMultiplier { get; set; } = 0.6m;

        /// <summary>
        /// Logical NodeIds of press stand pickup positions.
        /// Populated at startup from the road map.
        /// </summary>
        public List<int> PressStandNodeIds { get; set; } = new();

        /// <summary>
        /// How often to log a status message (in ticks).
        /// Default: every 300 ticks.
        /// </summary>
        public long StatusLogIntervalTicks { get; set; } = 300;

        /// <summary>
        /// Random seed for reproducible simulation runs.
        /// </summary>
        public int RandomSeed { get; set; } = 42;
    }
}
