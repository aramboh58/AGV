using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AGV.Topology.Services
{
    /// <summary>
    /// Hosted background service that manages topology lifecycle.
    ///
    /// Responsibilities:
    ///   1. Initial topology load at startup
    ///   2. Periodic polling for newer topology versions
    ///   3. Triggering version activation when a new version is found
    ///
    /// This service runs as a .NET hosted service registered in
    /// AGV.Host's DI container. It starts automatically when the
    /// host starts and stops gracefully on shutdown.
    ///
    /// Polling interval is configurable via TopologyOptions.
    /// Default: 60 seconds between version checks.
    ///
    /// On startup the service blocks until the initial topology load
    /// completes — no routing or dispatch can proceed until a valid
    /// topology is loaded.
    /// </summary>
    public sealed class TopologyBackgroundService : BackgroundService
    {
        private readonly TopologyService _topologyService;
        private readonly TopologyVersionManager _versionManager;
        private readonly TopologyOptions _options;
        private readonly ILogger<TopologyBackgroundService> _logger;

        public TopologyBackgroundService(
            TopologyService topologyService,
            TopologyVersionManager versionManager,
            TopologyOptions options,
            ILogger<TopologyBackgroundService> logger)
        {
            _topologyService = topologyService
                ?? throw new ArgumentNullException(nameof(topologyService));
            _versionManager = versionManager
                ?? throw new ArgumentNullException(nameof(versionManager));
            _options = options
                ?? throw new ArgumentNullException(nameof(options));
            _logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "TopologyBackgroundService starting — " +
                "polling interval: {IntervalSeconds}s",
                _options.VersionPollIntervalSeconds);

            // Initial load — block until topology is available
            await LoadInitialTopologyAsync(stoppingToken);

            // Polling loop
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.VersionPollIntervalSeconds),
                    stoppingToken);

                if (stoppingToken.IsCancellationRequested) break;

                await CheckForTopologyUpdateAsync(stoppingToken);
            }

            _logger.LogInformation("TopologyBackgroundService stopped.");
        }

        private async Task LoadInitialTopologyAsync(
            CancellationToken cancellationToken)
        {
            var attempt = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                attempt++;
                try
                {
                    _logger.LogInformation(
                        "Loading initial topology (attempt {Attempt})...",
                        attempt);

                    var loaded = await _topologyService
                        .CheckAndLoadLatestVersionAsync(
                            loadedByUser: "system_startup",
                            cancellationToken: cancellationToken);

                    if (loaded && _versionManager.IsLoaded)
                    {
                        _logger.LogInformation(
                            "Initial topology loaded: {Summary}",
                            _versionManager.ActiveVersion?.ToString());
                        return;
                    }

                    _logger.LogWarning(
                        "No topology found in database. " +
                        "Retrying in {RetrySeconds}s...",
                        _options.StartupRetrySeconds);
                }
                catch (Exception ex) when (
                    !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex,
                        "Failed to load initial topology (attempt {Attempt}). " +
                        "Retrying in {RetrySeconds}s...",
                        attempt,
                        _options.StartupRetrySeconds);
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(_options.StartupRetrySeconds),
                    cancellationToken);
            }
        }

        private async Task CheckForTopologyUpdateAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                var updated = await _topologyService
                    .CheckAndLoadLatestVersionAsync(
                        loadedByUser: "system_poll",
                        cancellationToken: cancellationToken);

                if (updated)
                {
                    _logger.LogInformation(
                        "Topology updated to: {Summary}",
                        _versionManager.ActiveVersion?.ToString());
                }
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex,
                    "Error checking for topology update. " +
                    "Will retry at next poll interval.");
            }
        }
    }

    /// <summary>
    /// Configuration options for the topology service.
    /// Loaded from appsettings.json section "Topology".
    /// </summary>
    public sealed class TopologyOptions
    {
        public const string SectionName = "Topology";

        /// <summary>
        /// How often to poll the database for newer topology versions.
        /// Default: 60 seconds.
        /// </summary>
        public int VersionPollIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// How long to wait between retries if initial topology load
        /// fails (database unavailable, no active version, etc.)
        /// Default: 10 seconds.
        /// </summary>
        public int StartupRetrySeconds { get; set; } = 10;

        /// <summary>
        /// Connection string name to use from appsettings.json.
        /// Default: "AgvDatabase"
        /// </summary>
        public string ConnectionStringName { get; set; } = "AgvDatabase";
    }
}