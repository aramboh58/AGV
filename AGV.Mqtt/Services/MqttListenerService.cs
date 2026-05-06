using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Interfaces;
using AGV.Core.Logging;
using AGV.Core.Messages;
using AGV.Fleet.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using System.Text.Json;

namespace AGV.Mqtt.Services
{
    /// <summary>
    /// Subscribes to all VDA 5050 vehicle topics and routes incoming
    /// messages to the appropriate Channel<T> in the ChannelRegistry.
    ///
    /// Message routing:
    ///   /state         → VehicleStateUpdates channel
    ///   /visualization → VehiclePositionUpdates channel
    ///   /connection    → ConnectionStateTracker
    ///   /factsheet     → IVehicleAdapter.OnFactSheetReceived callback
    ///
    /// Uses MQTTnet ManagedClient which handles:
    ///   — Automatic reconnection on broker dropout
    ///   — Subscription persistence across reconnects
    ///   — Message queuing during disconnects
    ///
    /// Thread safety:
    ///   MQTTnet delivers messages on its own thread pool.
    ///   All channel writes are thread-safe.
    ///   JSON deserialization is stateless and safe for concurrent use.
    /// </summary>
    public sealed class MqttListenerService
        : BackgroundService
    {
        private readonly MqttOptions _options;
        private readonly Vda5050TopicRouter _topicRouter;
        private readonly ConnectionStateTracker _connectionTracker;
        private readonly ChannelRegistry _channels;
        private readonly ILogger _logger;
        private readonly ILogger _messageLogger;

        private IManagedMqttClient? _client;

        // Registered callbacks (set by MqttVehicleAdapter)
        private Func<VehicleConnectionEvent,
            CancellationToken, Task>? _onConnectionChanged;
        private Func<VehicleFactSheetEvent,
            CancellationToken, Task>? _onFactSheetReceived;

        private static readonly JsonSerializerOptions JsonOptions
            = new() { PropertyNameCaseInsensitive = true };

        public MqttListenerService(
            MqttOptions options,
            Vda5050TopicRouter topicRouter,
            ConnectionStateTracker connectionTracker,
            ChannelRegistry channels,
            ILoggerFactory loggerFactory)
        {
            _options = options;
            _topicRouter = topicRouter;
            _connectionTracker = connectionTracker;
            _channels = channels;
            _logger = loggerFactory
                .CreateLogger(LogDomains.Mqtt);
            _messageLogger = loggerFactory
                .CreateLogger(LogDomains.VdaMessages);
        }

        // ----------------------------------------------------------------
        // Callback registration
        // ----------------------------------------------------------------

        public void OnConnectionChanged(
            Func<VehicleConnectionEvent, CancellationToken, Task> handler)
        {
            _onConnectionChanged = handler;
            _connectionTracker.OnConnectionChanged(handler);
        }

        public void OnFactSheetReceived(
            Func<VehicleFactSheetEvent, CancellationToken, Task> handler)
            => _onFactSheetReceived = handler;

        // ----------------------------------------------------------------
        // BackgroundService
        // ----------------------------------------------------------------

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "MqttListenerService starting — " +
                "broker={Host}:{Port}",
                _options.BrokerHost, _options.BrokerPort);

            var factory = new MqttFactory();
            _client = factory.CreateManagedMqttClient();

            _client.ApplicationMessageReceivedAsync +=
                OnMessageReceivedAsync;

            _client.ConnectedAsync += args =>
            {
                _logger.LogInformation(
                    "MQTT listener connected to broker");
                return Task.CompletedTask;
            };

            _client.DisconnectedAsync += args =>
            {
                _logger.LogWarning(
                    "MQTT listener disconnected: {Reason}",
                    args.ReasonString ?? "unknown");
                return Task.CompletedTask;
            };

            var clientOptions = new ManagedMqttClientOptionsBuilder()
                .WithAutoReconnectDelay(
                    TimeSpan.FromSeconds(_options.ReconnectDelaySeconds))
                .WithClientOptions(new MqttClientOptionsBuilder()
                    .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
                    .WithClientId($"agv-host-listener-{Guid.NewGuid():N}")
                    .WithKeepAlivePeriod(
                        TimeSpan.FromSeconds(_options.KeepAliveSeconds))
                    .Build())
                .Build();

            // Subscribe to all vehicle topics via wildcard
            await _client.SubscribeAsync(
                _topicRouter.AllVehiclesTopic(),
                MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);

            await _client.StartAsync(clientOptions);

            _logger.LogInformation(
                "MQTT listener subscribed to: {Topic}",
                _topicRouter.AllVehiclesTopic());

            // Wait until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken)
                .ContinueWith(_ => Task.CompletedTask);

            await _client.StopAsync();
            _logger.LogInformation("MqttListenerService stopped");
        }

        // ----------------------------------------------------------------
        // Message handling
        // ----------------------------------------------------------------

        private async Task OnMessageReceivedAsync(
            MqttApplicationMessageReceivedEventArgs args)
        {
            var topic = args.ApplicationMessage.Topic;
            var payload = args.ApplicationMessage.PayloadSegment.Array;

            if (payload is null || payload.Length == 0) return;

            var components = _topicRouter.ParseTopic(topic);
            if (components is null) return;

            _messageLogger.LogDebug(
                "Received {MessageType} from {SerialNumber} " +
                "({Bytes} bytes)",
                components.MessageType,
                components.SerialNumber,
                payload.Length);

            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(5));

            try
            {
                switch (components.MessageType)
                {
                    case Vda5050MessageType.State:
                        await HandleStateMessageAsync(
                            components.SerialNumber,
                            payload, cts.Token);
                        break;

                    case Vda5050MessageType.Visualization:
                        await HandleVisualizationMessageAsync(
                            components.SerialNumber,
                            payload, cts.Token);
                        break;

                    case Vda5050MessageType.Connection:
                        await HandleConnectionMessageAsync(
                            components.SerialNumber,
                            payload, cts.Token);
                        break;

                    case Vda5050MessageType.FactSheet:
                        await HandleFactSheetMessageAsync(
                            components.SerialNumber,
                            payload, cts.Token);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error processing {MessageType} from {SerialNumber}",
                    components.MessageType, components.SerialNumber);
            }
        }

        // ----------------------------------------------------------------
        // State message
        // ----------------------------------------------------------------

        private async Task HandleStateMessageAsync(
            string serialNumber,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            var json = Encoding.UTF8.GetString(payload);
            var dto = JsonSerializer.Deserialize<Vda5050StateDto>(
                json, JsonOptions);

            if (dto is null) return;

            var errors = dto.Errors?
                .Select(e => e.ErrorDescription ?? string.Empty)
                .ToArray()
                ?? Array.Empty<string>();

            var operatingMode = dto.OperatingMode?.ToUpperInvariant()
                switch
            {
                "SEMIAUTOMATIC" => Core.Enums.OperatingMode.SemiAutomatic,
                "MANUAL" => Core.Enums.OperatingMode.Manual,
                "SERVICE" => Core.Enums.OperatingMode.Service,
                "TEACHING" => Core.Enums.OperatingMode.Teaching,
                _ => Core.Enums.OperatingMode.Automatic,
            };

            var update = new VehicleStateUpdate
            {
                SerialNumber = serialNumber,
                LastNodeId = dto.LastNodeSequenceId,
                BatteryStateOfCharge = dto.BatteryState?.BatteryCharge ?? 0m,
                IsCharging = dto.BatteryState?.Charging ?? false,
                IsLoaded = dto.Loads?.Count > 0,
                CurrentOrderId = dto.OrderId ?? string.Empty,
                OrderUpdateId = dto.OrderUpdateId,
                OperatingMode = operatingMode,
                Errors = errors,
                ReceivedAt = DateTime.UtcNow,
            };

            await _channels.VehicleStateUpdates.Writer
                .WriteAsync(update, cancellationToken);
        }

        // ----------------------------------------------------------------
        // Visualization message
        // ----------------------------------------------------------------

        private async Task HandleVisualizationMessageAsync(
            string serialNumber,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            var json = Encoding.UTF8.GetString(payload);
            var dto = JsonSerializer.Deserialize<Vda5050VisualizationDto>(
                json, JsonOptions);

            if (dto is null) return;

            var update = new VehiclePositionUpdate
            {
                SerialNumber = serialNumber,
                NodeId = 0,   // visualization doesn't include nodeId
                MapId = dto.AgvPosition?.MapId ?? string.Empty,
                X = dto.AgvPosition?.X ?? 0m,
                Y = dto.AgvPosition?.Y ?? 0m,
                ReceivedAt = DateTime.UtcNow,
            };

            await _channels.VehiclePositionUpdates.Writer
                .WriteAsync(update, cancellationToken);
        }

        // ----------------------------------------------------------------
        // Connection message
        // ----------------------------------------------------------------

        private async Task HandleConnectionMessageAsync(
            string serialNumber,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            var json = Encoding.UTF8.GetString(payload);
            var dto = JsonSerializer.Deserialize<Vda5050ConnectionDto>(
                json, JsonOptions);

            if (dto is null) return;

            await _connectionTracker.HandleConnectionMessageAsync(
                serialNumber,
                dto.ConnectionState ?? "OFFLINE",
                cancellationToken);
        }

        // ----------------------------------------------------------------
        // Fact Sheet message
        // ----------------------------------------------------------------

        private async Task HandleFactSheetMessageAsync(
            string serialNumber,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            var json = Encoding.UTF8.GetString(payload);
            var dto = JsonSerializer.Deserialize<Vda5050FactSheetDto>(
                json, JsonOptions);

            if (dto is null || _onFactSheetReceived is null) return;

            var evt = new VehicleFactSheetEvent
            {
                SerialNumber = serialNumber,
                MaxOrderHorizonDepth = dto.AgvGeometry?
                    .MaxOrderHorizonDepth ?? 10,
                SupportsNurbsTrajectory = false,
                SupportedActionTypes = string.Join(",",
                    dto.TypeSpecification?
                        .SeriesDescription ?? string.Empty),
                MaxSpeedMs = dto.PhysicalParameters?
                    .SpeedMax ?? 1.5m,
                MaxPayloadKg = dto.LoadSpecification?
                    .MaxLoadMass ?? 1000m,
                LengthMeters = dto.PhysicalParameters?
                    .LengthReference ?? 2.0m,
                WidthMeters = dto.PhysicalParameters?
                    .WidthReference ?? 1.2m,
                ReceivedAt = DateTime.UtcNow,
            };

            await _onFactSheetReceived(evt, cancellationToken);
        }
    }

    // ----------------------------------------------------------------
    // VDA 5050 JSON DTOs (deserialization only)
    // Minimal — only fields the host actually uses
    // ----------------------------------------------------------------

    internal sealed class Vda5050StateDto
    {
        public string? OrderId { get; set; }
        public int OrderUpdateId { get; set; }
        public int LastNodeSequenceId { get; set; }
        public string? OperatingMode { get; set; }
        public Vda5050BatteryStateDto? BatteryState { get; set; }
        public List<Vda5050LoadDto>? Loads { get; set; }
        public List<Vda5050ErrorDto>? Errors { get; set; }
        public Vda5050PositionDto? AgvPosition { get; set; }
    }

    internal sealed class Vda5050BatteryStateDto
    {
        public decimal BatteryCharge { get; set; }
        public bool Charging { get; set; }
    }

    internal sealed class Vda5050LoadDto
    {
        public string? LoadId { get; set; }
        public string? LoadType { get; set; }
    }

    internal sealed class Vda5050ErrorDto
    {
        public string? ErrorType { get; set; }
        public string? ErrorDescription { get; set; }
        public string? ErrorLevel { get; set; }
    }

    internal sealed class Vda5050PositionDto
    {
        public decimal X { get; set; }
        public decimal Y { get; set; }
        public decimal Theta { get; set; }
        public string? MapId { get; set; }
    }

    internal sealed class Vda5050VisualizationDto
    {
        public Vda5050PositionDto? AgvPosition { get; set; }
    }

    internal sealed class Vda5050ConnectionDto
    {
        public string? ConnectionState { get; set; }
    }

    internal sealed class Vda5050FactSheetDto
    {
        public Vda5050GeometryDto? AgvGeometry { get; set; }
        public Vda5050PhysicalParametersDto? PhysicalParameters { get; set; }
        public Vda5050TypeSpecificationDto? TypeSpecification { get; set; }
        public Vda5050LoadSpecificationDto? LoadSpecification { get; set; }
    }

    internal sealed class Vda5050GeometryDto
    {
        public int MaxOrderHorizonDepth { get; set; }
    }

    internal sealed class Vda5050PhysicalParametersDto
    {
        public decimal SpeedMax { get; set; }
        public decimal LengthReference { get; set; }
        public decimal WidthReference { get; set; }
    }

    internal sealed class Vda5050TypeSpecificationDto
    {
        public string? SeriesDescription { get; set; }
    }

    internal sealed class Vda5050LoadSpecificationDto
    {
        public decimal MaxLoadMass { get; set; }
    }

    /// <summary>
    /// MQTT connection options loaded from appsettings.json.
    /// </summary>
    public sealed class MqttOptions
    {
        public const string SectionName = "Mqtt";
        public string BrokerHost { get; set; } = "localhost";
        public int BrokerPort { get; set; } = 1883;
        public int KeepAliveSeconds { get; set; } = 60;
        public int ReconnectDelaySeconds { get; set; } = 5;
        public string InterfaceName { get; set; } = "uagv";
        public string MajorVersion { get; set; } = "v2";
        public string Manufacturer { get; set; } = "JBT";
    }
}
