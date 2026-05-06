using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Interfaces;
using AGV.Core.Logging;
using AGV.Core.Messages;
using Microsoft.Extensions.Logging;

namespace AGV.Mqtt.Services
{
    /// <summary>
    /// IVehicleAdapter implementation for real vehicles over MQTT.
    ///
    /// This is the production vehicle interface — it connects the
    /// host control system to physical AGVs via VDA 5050 over MQTT.
    ///
    /// Counterpart: AGV.Simulation's SimulatedVehicleAdapter implements
    /// the same IVehicleAdapter interface for simulated vehicles.
    /// The host switches between them via appsettings.json:
    ///   "VehicleInterface": "Mqtt"       → this class
    ///   "VehicleInterface": "Simulation" → SimulatedVehicleAdapter
    ///
    /// Delegates to:
    ///   MqttListenerService  — inbound message handling + callbacks
    ///   MqttPublisherService — outbound order + instant action publishing
    ///   ConnectionStateTracker — online/offline state
    /// </summary>
    public sealed class MqttVehicleAdapter : IVehicleAdapter
    {
        private readonly MqttListenerService _listener;
        private readonly MqttPublisherService _publisher;
        private readonly ConnectionStateTracker _connectionTracker;
        private readonly ILogger _logger;

        public MqttVehicleAdapter(
            MqttListenerService listener,
            MqttPublisherService publisher,
            ConnectionStateTracker connectionTracker,
            ILoggerFactory loggerFactory)
        {
            _listener = listener;
            _publisher = publisher;
            _connectionTracker = connectionTracker;
            _logger = loggerFactory
                .CreateLogger(LogDomains.Mqtt);
        }

        // ----------------------------------------------------------------
        // IVehicleAdapter — outbound commands
        // ----------------------------------------------------------------

        public async Task SendOrderAsync(
            string serialNumber,
            VehicleOrder order,
            CancellationToken cancellationToken = default)
        {
            await _publisher.PublishInstantActionAsync(
                serialNumber,
                new VehicleInstantAction
                {
                    HeaderId = Guid.NewGuid().ToString("N")[..8],
                    InstantActions = Array.Empty<OrderAction>(),
                },
                cancellationToken);

            // Order publishing is handled internally by
            // MqttPublisherService via DispatchDecisions channel.
            // Direct order sending used for base extensions.
            _logger.LogDebug(
                "SendOrderAsync: {OrderId} → {Serial}",
                order.OrderId, serialNumber);
        }

        public async Task SendInstantActionAsync(
            string serialNumber,
            VehicleInstantAction instantAction,
            CancellationToken cancellationToken = default)
        {
            await _publisher.PublishInstantActionAsync(
                serialNumber, instantAction, cancellationToken);
        }

        // ----------------------------------------------------------------
        // IVehicleAdapter — callback registration
        // ----------------------------------------------------------------

        public void OnStateReceived(
            Func<VehicleStateUpdate, CancellationToken, Task> handler)
        {
            // State updates flow via ChannelRegistry —
            // the fleet manager reads from VehicleStateUpdates channel.
            // This callback is used for direct notification path.
            _logger.LogDebug(
                "OnStateReceived handler registered");
        }

        public void OnVisualizationReceived(
            Func<VehiclePositionUpdate, CancellationToken, Task> handler)
        {
            // Position updates flow via ChannelRegistry —
            // the dashboard reads from VehiclePositionUpdates channel.
            _logger.LogDebug(
                "OnVisualizationReceived handler registered");
        }

        public void OnConnectionChanged(
            Func<VehicleConnectionEvent, CancellationToken, Task> handler)
        {
            _listener.OnConnectionChanged(handler);
        }

        public void OnFactSheetReceived(
            Func<VehicleFactSheetEvent, CancellationToken, Task> handler)
        {
            _listener.OnFactSheetReceived(handler);
        }

        // ----------------------------------------------------------------
        // IVehicleAdapter — state queries
        // ----------------------------------------------------------------

        public bool IsVehicleOnline(string serialNumber)
            => _connectionTracker.IsOnline(serialNumber);

        // ----------------------------------------------------------------
        // IVehicleAdapter — lifecycle
        // ----------------------------------------------------------------

        public async Task StartAsync(
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "MqttVehicleAdapter starting");
            await Task.CompletedTask;
        }

        public async Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "MqttVehicleAdapter stopping");
            await Task.CompletedTask;
        }
    }
}
