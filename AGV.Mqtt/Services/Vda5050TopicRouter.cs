using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Logging;
using Microsoft.Extensions.Logging;

namespace AGV.Mqtt.Services
{
    /// <summary>
    /// Parses VDA 5050 MQTT topic strings and routes incoming messages
    /// to the correct handler based on message type.
    ///
    /// VDA 5050 topic structure:
    ///   {interfaceName}/{majorVersion}/{manufacturer}/{serialNumber}/{topic}
    ///
    /// Example topics:
    ///   uagv/v2/JBT/SN-F01/state
    ///   uagv/v2/JBT/SN-F01/connection
    ///   uagv/v2/JBT/SN-F01/visualization
    ///   uagv/v2/JBT/SN-F01/factsheet
    ///
    /// Host publishes to:
    ///   uagv/v2/JBT/SN-F01/order
    ///   uagv/v2/JBT/SN-F01/instantActions
    /// </summary>
    public sealed class Vda5050TopicRouter
    {
        private readonly string _topicPrefix;
        private readonly ILogger _logger;

        public Vda5050TopicRouter(
            string interfaceName,
            string majorVersion,
            string manufacturer,
            ILoggerFactory loggerFactory)
        {
            _topicPrefix = $"{interfaceName}/{majorVersion}/{manufacturer}";
            _logger = loggerFactory.CreateLogger(LogDomains.Mqtt);
        }

        // ----------------------------------------------------------------
        // Topic construction (outbound — host → vehicle)
        // ----------------------------------------------------------------

        public string OrderTopic(string serialNumber)
            => $"{_topicPrefix}/{serialNumber}/order";

        public string InstantActionsTopic(string serialNumber)
            => $"{_topicPrefix}/{serialNumber}/instantActions";

        // ----------------------------------------------------------------
        // Subscription topics (inbound — vehicle → host)
        // ----------------------------------------------------------------

        /// <summary>
        /// Wildcard subscription pattern for all vehicle messages.
        /// Subscribe to this one topic to receive all vehicle messages.
        /// </summary>
        public string AllVehiclesTopic()
            => $"{_topicPrefix}/+/#";

        public string StateTopic(string serialNumber)
            => $"{_topicPrefix}/{serialNumber}/state";

        public string ConnectionTopic(string serialNumber)
            => $"{_topicPrefix}/{serialNumber}/connection";

        public string VisualizationTopic(string serialNumber)
            => $"{_topicPrefix}/{serialNumber}/visualization";

        public string FactSheetTopic(string serialNumber)
            => $"{_topicPrefix}/{serialNumber}/factsheet";

        // ----------------------------------------------------------------
        // Topic parsing (inbound routing)
        // ----------------------------------------------------------------

        /// <summary>
        /// Parses an incoming MQTT topic and extracts the serial number
        /// and message type. Returns null if the topic does not match
        /// the expected VDA 5050 structure.
        /// </summary>
        public TopicComponents? ParseTopic(string topic)
        {
            if (string.IsNullOrWhiteSpace(topic)) return null;

            // Expected: {prefix}/{serialNumber}/{messageType}
            if (!topic.StartsWith(_topicPrefix + "/",
                StringComparison.OrdinalIgnoreCase))
                return null;

            var remainder = topic[(_topicPrefix.Length + 1)..];
            var parts = remainder.Split('/');

            if (parts.Length < 2) return null;

            var serialNumber = parts[0];
            var messageType = parts[1].ToLowerInvariant();

            var type = messageType switch
            {
                "state" => Vda5050MessageType.State,
                "connection" => Vda5050MessageType.Connection,
                "visualization" => Vda5050MessageType.Visualization,
                "factsheet" => Vda5050MessageType.FactSheet,
                _ => Vda5050MessageType.Unknown,
            };

            if (type == Vda5050MessageType.Unknown)
            {
                _logger.LogDebug(
                    "Unknown VDA 5050 message type: {MessageType} " +
                    "from {SerialNumber}",
                    messageType, serialNumber);
            }

            return new TopicComponents(serialNumber, type, topic);
        }

        /// <summary>
        /// Returns true if the topic is a state message.
        /// Fast path used by the listener inner loop.
        /// </summary>
        public bool IsStateTopic(string topic)
            => topic.EndsWith("/state",
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns true if the topic is a visualization message.
        /// </summary>
        public bool IsVisualizationTopic(string topic)
            => topic.EndsWith("/visualization",
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parsed components of a VDA 5050 MQTT topic.
    /// </summary>
    public sealed record TopicComponents(
        string SerialNumber,
        Vda5050MessageType MessageType,
        string RawTopic);

    /// <summary>
    /// VDA 5050 message types received from vehicles.
    /// </summary>
    public enum Vda5050MessageType
    {
        Unknown,
        State,
        Connection,
        Visualization,
        FactSheet,
    }
}
