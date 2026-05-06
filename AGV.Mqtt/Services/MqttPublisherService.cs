using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Interfaces;
using AGV.Core.Logging;
using AGV.Core.Messages;
using AGV.Fleet.Infrastructure;
using AGV.Vehicle.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using System.Text.Json;

namespace AGV.Mqtt.Services
{
    /// <summary>
    /// Consumes dispatch and charge assignment channels and publishes
    /// VDA 5050 Order and InstantAction messages to vehicles via MQTT.
    ///
    /// Consumes:
    ///   DispatchDecisions channel → builds + sends VDA 5050 Order
    ///   ChargeAssignments channel → builds + sends charge Order
    ///
    /// Also handles:
    ///   waitForTrigger  — hold vehicle at node pending traffic clearance
    ///   triggerRelease  — release held vehicle
    ///   cancelOrder     — cancel active order on fault/transfer
    ///   startCharging   — attached to charge node in charge order
    ///   stopCharging    — InstantAction when charge complete
    ///
    /// Uses MQTTnet ManagedClient — same instance approach as listener
    /// for connection resilience. Publisher uses a separate client ID.
    /// </summary>
    public sealed class MqttPublisherService : BackgroundService
    {
        private readonly MqttOptions _options;
        private readonly Vda5050TopicRouter _topicRouter;
        private readonly ChannelRegistry _channels;
        private readonly OrderBuilder _orderBuilder;
        private readonly ILogger _logger;
        private readonly ILogger _messageLogger;

        private IManagedMqttClient? _client;

        // Per-vehicle order update ID tracking
        private readonly System.Collections.Concurrent
            .ConcurrentDictionary<string, int> _orderUpdateIds = new();

        private static readonly JsonSerializerOptions JsonOptions
            = new()
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition =
                    System.Text.Json.Serialization
                        .JsonIgnoreCondition.WhenWritingNull,
            };

        public MqttPublisherService(
            MqttOptions options,
            Vda5050TopicRouter topicRouter,
            ChannelRegistry channels,
            OrderBuilder orderBuilder,
            ILoggerFactory loggerFactory)
        {
            _options = options;
            _topicRouter = topicRouter;
            _channels = channels;
            _orderBuilder = orderBuilder;
            _logger = loggerFactory.CreateLogger(LogDomains.Mqtt);
            _messageLogger = loggerFactory
                .CreateLogger(LogDomains.VdaMessages);
        }

        // ----------------------------------------------------------------
        // BackgroundService
        // ----------------------------------------------------------------

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "MqttPublisherService starting — " +
                "broker={Host}:{Port}",
                _options.BrokerHost, _options.BrokerPort);

            var factory = new MqttFactory();
            _client = factory.CreateManagedMqttClient();

            _client.ConnectedAsync += _ =>
            {
                _logger.LogInformation(
                    "MQTT publisher connected to broker");
                return Task.CompletedTask;
            };

            _client.DisconnectedAsync += args =>
            {
                _logger.LogWarning(
                    "MQTT publisher disconnected: {Reason}",
                    args.ReasonString ?? "unknown");
                return Task.CompletedTask;
            };

            var clientOptions = new ManagedMqttClientOptionsBuilder()
                .WithAutoReconnectDelay(
                    TimeSpan.FromSeconds(_options.ReconnectDelaySeconds))
                .WithClientOptions(new MqttClientOptionsBuilder()
                    .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
                    .WithClientId(
                        $"agv-host-publisher-{Guid.NewGuid():N}")
                    .WithKeepAlivePeriod(
                        TimeSpan.FromSeconds(_options.KeepAliveSeconds))
                    .Build())
                .Build();

            await _client.StartAsync(clientOptions);

            // Run dispatch and charge consumers in parallel
            var dispatchTask = ConsumeDispatchDecisionsAsync(stoppingToken);
            var chargeTask = ConsumeChargeAssignmentsAsync(stoppingToken);

            await Task.WhenAll(dispatchTask, chargeTask);

            await _client.StopAsync();
            _logger.LogInformation("MqttPublisherService stopped");
        }

        // ----------------------------------------------------------------
        // Dispatch decision consumer
        // ----------------------------------------------------------------

        private async Task ConsumeDispatchDecisionsAsync(
            CancellationToken stoppingToken)
        {
            await foreach (var decision in
                _channels.DispatchDecisions.Reader
                    .ReadAllAsync(stoppingToken))
            {
                await PublishDispatchOrderAsync(decision, stoppingToken);
            }
        }

        private async Task PublishDispatchOrderAsync(
            MissionDispatchDecision decision,
            CancellationToken cancellationToken)
        {
            try
            {
                var updateId = NextOrderUpdateId(decision.SerialNumber);

                // Build route nodes from decision
                var routeNodes = decision.RouteNodeIds
                    .Select((nodeId, i) => new RouteNode
                    {
                        NodeId = nodeId,
                        ArrivalHeadingDegrees = 0m,
                    })
                    .ToList()
                    .AsReadOnly();

                // Build pick action for the final (pickup) node
                var pickAction = _orderBuilder.BuildPickAction();
                var actions = OrderActions.WithPickAt(
                    decision.RouteNodeIds[^1], pickAction);

                // Use a stub FactSheet for window sizing
                // Real fact sheet used when VehicleFactSheet is wired
                var factSheet = new Core.Entities.VehicleFactSheet(
                    vehicleId: 0,
                    protocolVersion: "2.0.0",
                    maxOrderHorizonDepth: 10,
                    supportsNurbsTrajectory: false,
                    supportedActionTypes: "pick,drop,startCharging,stopCharging",
                    maxSpeedMs: 1.5m,
                    maxPayloadKg: 1500m,
                    lengthMeters: 2.5m,
                    widthMeters: 1.2m);

                var order = _orderBuilder.BuildMissionOrder(
                    decision.OrderId,
                    updateId,
                    routeNodes,
                    decision.RouteMoveIds,
                    factSheet,
                    actions);

                await PublishOrderAsync(
                    decision.SerialNumber, order, cancellationToken);

                _logger.LogInformation(
                    "Published order {OrderId} to vehicle {Serial} " +
                    "({NodeCount} nodes)",
                    decision.OrderId,
                    decision.SerialNumber,
                    routeNodes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to publish dispatch order {OrderId} " +
                    "to {Serial}",
                    decision.OrderId, decision.SerialNumber);
            }
        }

        // ----------------------------------------------------------------
        // Charge assignment consumer
        // ----------------------------------------------------------------

        private async Task ConsumeChargeAssignmentsAsync(
            CancellationToken stoppingToken)
        {
            await foreach (var assignment in
                _channels.ChargeAssignments.Reader
                    .ReadAllAsync(stoppingToken))
            {
                await PublishChargeOrderAsync(assignment, stoppingToken);
            }
        }

        private async Task PublishChargeOrderAsync(
            ChargeAssignment assignment,
            CancellationToken cancellationToken)
        {
            try
            {
                var updateId = NextOrderUpdateId(assignment.SerialNumber);
                var orderId = $"CHG-{assignment.VehicleId:D4}-" +
                               $"{updateId:D4}";

                var routeNodes = assignment.RouteNodeIds
                    .Select((nodeId, i) => new RouteNode
                    {
                        NodeId = nodeId,
                        ArrivalHeadingDegrees = 0m,
                    })
                    .ToList()
                    .AsReadOnly();

                var factSheet = new Core.Entities.VehicleFactSheet(
                    vehicleId: 0,
                    protocolVersion: "2.0.0",
                    maxOrderHorizonDepth: 10,
                    supportsNurbsTrajectory: false,
                    supportedActionTypes: "startCharging,stopCharging",
                    maxSpeedMs: 1.5m,
                    maxPayloadKg: 1500m,
                    lengthMeters: 2.5m,
                    widthMeters: 1.2m);

                var order = _orderBuilder.BuildChargeOrder(
                    orderId,
                    updateId,
                    routeNodes,
                    assignment.RouteMoveIds,
                    factSheet);

                await PublishOrderAsync(
                    assignment.SerialNumber, order, cancellationToken);

                _logger.LogInformation(
                    "Published charge order {OrderId} to " +
                    "vehicle {Serial} (type={ChargeType})",
                    orderId,
                    assignment.SerialNumber,
                    assignment.ChargeType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to publish charge order to {Serial}",
                    assignment.SerialNumber);
            }
        }

        // ----------------------------------------------------------------
        // InstantAction publishing
        // ----------------------------------------------------------------

        /// <summary>
        /// Sends a waitForTrigger InstantAction to hold a vehicle
        /// at its current position pending traffic clearance.
        /// Called by TrafficManagerService via IVehicleAdapter.
        /// </summary>
        public async Task SendWaitForTriggerAsync(
            string serialNumber,
            string triggerId,
            CancellationToken cancellationToken = default)
        {
            var action = _orderBuilder.BuildWaitForTriggerAction(triggerId);
            await PublishInstantActionAsync(
                serialNumber,
                new VehicleInstantAction
                {
                    HeaderId = Guid.NewGuid().ToString("N")[..8],
                    InstantActions = new[] { action },
                },
                cancellationToken);

            _logger.LogDebug(
                "Sent waitForTrigger to {Serial} (triggerId={Id})",
                serialNumber, triggerId);
        }

        /// <summary>
        /// Sends a triggerRelease InstantAction to release a held vehicle.
        /// </summary>
        public async Task SendTriggerReleaseAsync(
            string serialNumber,
            string triggerId,
            CancellationToken cancellationToken = default)
        {
            var action = new OrderAction
            {
                ActionId = $"TR-{triggerId}",
                ActionType = "triggerRelease",
                BlockingType = "SOFT",
                Parameters = new List<ActionParameter>
                {
                    new() { Key = "triggerId", Value = triggerId }
                }.AsReadOnly(),
            };

            await PublishInstantActionAsync(
                serialNumber,
                new VehicleInstantAction
                {
                    HeaderId = Guid.NewGuid().ToString("N")[..8],
                    InstantActions = new[] { action },
                },
                cancellationToken);
        }

        /// <summary>
        /// Sends a cancelOrder InstantAction to abort the vehicle's
        /// current order. Used on vehicle fault or mission transfer.
        /// </summary>
        public async Task SendCancelOrderAsync(
            string serialNumber,
            CancellationToken cancellationToken = default)
        {
            var action = new OrderAction
            {
                ActionId = $"CANCEL-{DateTime.UtcNow.Ticks}",
                ActionType = "cancelOrder",
                BlockingType = "HARD",
                Parameters = Array.Empty<ActionParameter>(),
            };

            await PublishInstantActionAsync(
                serialNumber,
                new VehicleInstantAction
                {
                    HeaderId = Guid.NewGuid().ToString("N")[..8],
                    InstantActions = new[] { action },
                },
                cancellationToken);

            _logger.LogInformation(
                "Sent cancelOrder to {Serial}", serialNumber);
        }

        // ----------------------------------------------------------------
        // Core publish methods
        // ----------------------------------------------------------------

        private async Task PublishOrderAsync(
            string serialNumber,
            VehicleOrder order,
            CancellationToken cancellationToken)
        {
            if (_client is null) return;

            var topic = _topicRouter.OrderTopic(serialNumber);
            var payload = JsonSerializer.Serialize(order, JsonOptions);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel(
                    MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _client.EnqueueAsync(message);

            _messageLogger.LogDebug(
                "Published order to {Topic} ({Bytes} bytes)",
                topic, payload.Length);
        }

        public async Task PublishInstantActionAsync(
            string serialNumber,
            VehicleInstantAction instantAction,
            CancellationToken cancellationToken = default)
        {
            if (_client is null) return;

            var topic = _topicRouter.InstantActionsTopic(serialNumber);
            var payload = JsonSerializer.Serialize(
                instantAction, JsonOptions);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel(
                    MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _client.EnqueueAsync(message);
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private int NextOrderUpdateId(string serialNumber)
            => _orderUpdateIds.AddOrUpdate(
                serialNumber, 1, (_, current) => current + 1);
    }
}
