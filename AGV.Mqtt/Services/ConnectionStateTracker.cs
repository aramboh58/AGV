using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Interfaces;
using AGV.Core.Logging;
using AGV.Core.Messages;
using AGV.Fleet.Infrastructure;
using Microsoft.Extensions.Logging;

namespace AGV.Mqtt.Services
{
    /// <summary>
    /// Tracks the online/offline connection state of all vehicles.
    ///
    /// VDA 5050 connection lifecycle:
    ///   1. Vehicle connects to broker — publishes connection message
    ///      with connectionState = "ONLINE"
    ///   2. Vehicle publishes Last Will on connect — broker sends it
    ///      automatically if the vehicle drops off unexpectedly
    ///   3. Last Will payload has connectionState = "CONNECTIONBROKEN"
    ///   4. Graceful disconnect has connectionState = "OFFLINE"
    ///
    /// On unexpected dropout (CONNECTIONBROKEN):
    ///   — Vehicle is marked offline immediately
    ///   — Dead mission detection is triggered
    ///   — FleetManagerService handles mission transfer via channel
    ///
    /// Thread safety:
    ///   ConcurrentDictionary for state storage.
    ///   Callbacks invoked on the MQTT listener thread —
    ///   handlers must be non-blocking.
    /// </summary>
    public sealed class ConnectionStateTracker
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary
            <string, VehicleConnectionState> _states = new();

        private readonly ChannelRegistry _channels;
        private readonly ILogger _logger;

        // Registered callbacks
        private Func<VehicleConnectionEvent, CancellationToken, Task>?
            _onConnectionChanged;

        public ConnectionStateTracker(
            ChannelRegistry channels,
            ILoggerFactory loggerFactory)
        {
            _channels = channels;
            _logger = loggerFactory.CreateLogger(LogDomains.Connection);
        }

        // ----------------------------------------------------------------
        // Callback registration
        // ----------------------------------------------------------------

        public void OnConnectionChanged(
            Func<VehicleConnectionEvent, CancellationToken, Task> handler)
            => _onConnectionChanged = handler;

        // ----------------------------------------------------------------
        // State updates
        // ----------------------------------------------------------------

        /// <summary>
        /// Called when a VDA 5050 connection message is received.
        /// connectionState values: "ONLINE", "OFFLINE", "CONNECTIONBROKEN"
        /// </summary>
        public async Task HandleConnectionMessageAsync(
            string serialNumber,
            string connectionState,
            CancellationToken cancellationToken = default)
        {
            var isOnline = connectionState
                .Equals("ONLINE", StringComparison.OrdinalIgnoreCase);

            var wasOnline = _states.TryGetValue(serialNumber, out var prev)
                && prev.IsOnline;

            var state = new VehicleConnectionState
            {
                SerialNumber = serialNumber,
                IsOnline = isOnline,
                ConnectionState = connectionState,
                LastUpdatedAt = DateTime.UtcNow,
            };

            _states[serialNumber] = state;

            // Log significant transitions
            if (isOnline && !wasOnline)
            {
                _logger.LogInformation(
                    "Vehicle {SerialNumber} ONLINE",
                    serialNumber);
            }
            else if (!isOnline && wasOnline)
            {
                var isBroken = connectionState.Equals(
                    "CONNECTIONBROKEN",
                    StringComparison.OrdinalIgnoreCase);

                _logger.LogWarning(
                    "Vehicle {SerialNumber} {State} — " +
                    "{Action}",
                    serialNumber,
                    connectionState,
                    isBroken
                        ? "triggering dead mission detection"
                        : "graceful disconnect");
            }

            // Notify fleet manager via callback
            var evt = new VehicleConnectionEvent
            {
                SerialNumber = serialNumber,
                IsOnline = isOnline,
                EventAt = DateTime.UtcNow,
            };

            if (_onConnectionChanged is not null)
            {
                await _onConnectionChanged(evt, cancellationToken);
            }
        }

        /// <summary>
        /// Called when MQTT broker confirms a vehicle has connected
        /// (TCP level connection, before VDA 5050 messages begin).
        /// </summary>
        public void HandleBrokerConnect(string clientId)
        {
            _logger.LogDebug(
                "MQTT client connected: {ClientId}", clientId);
        }

        /// <summary>
        /// Called when MQTT broker reports a client disconnected
        /// (TCP level — may precede or replace VDA 5050 Last Will).
        /// </summary>
        public void HandleBrokerDisconnect(string clientId)
        {
            _logger.LogDebug(
                "MQTT client disconnected: {ClientId}", clientId);
        }

        // ----------------------------------------------------------------
        // State queries
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns true if the vehicle with the given serial number
        /// is currently online.
        /// </summary>
        public bool IsOnline(string serialNumber)
            => _states.TryGetValue(serialNumber, out var state)
            && state.IsOnline;

        /// <summary>
        /// Returns the connection state for a vehicle,
        /// or null if never seen.
        /// </summary>
        public VehicleConnectionState? GetState(string serialNumber)
            => _states.TryGetValue(serialNumber, out var state)
                ? state : null;

        /// <summary>
        /// Returns all currently online vehicle serial numbers.
        /// </summary>
        public IReadOnlyList<string> GetOnlineSerialNumbers()
            => _states.Values
                .Where(s => s.IsOnline)
                .Select(s => s.SerialNumber)
                .ToList()
                .AsReadOnly();

        /// <summary>
        /// Returns count of currently online vehicles.
        /// </summary>
        public int OnlineCount
            => _states.Values.Count(s => s.IsOnline);
    }

    /// <summary>
    /// Current connection state of a vehicle.
    /// </summary>
    public sealed class VehicleConnectionState
    {
        public string SerialNumber { get; init; } = string.Empty;
        public bool IsOnline { get; init; }
        public string ConnectionState { get; init; } = string.Empty;
        public DateTime LastUpdatedAt { get; init; }
    }
}
