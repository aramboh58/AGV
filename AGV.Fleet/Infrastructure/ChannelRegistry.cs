using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Messages;
using System.Threading.Channels;

namespace AGV.Fleet.Infrastructure
{
    /// <summary>
    /// Central registry of all inter-service Channel<T> instances.
    ///
    /// The channel registry is the single place where all cross-service
    /// communication channels are created and held. It is registered as
    /// a singleton in the DI container and injected into each service
    /// that needs to publish or consume messages.
    ///
    /// Channel ownership principle:
    ///   Each channel has exactly one writer (owner) and one or more
    ///   readers (consumers). The owner is the sole writer of the
    ///   state that flows through the channel. This eliminates the
    ///   lock contention and deadlock risk that plagued the JBT MFC
    ///   dispatcher/traffic manager interaction.
    ///
    /// Channel sizing:
    ///   All channels are bounded to prevent unbounded memory growth
    ///   under load. DropOldest is used for position updates (we only
    ///   care about the latest position) and Wait is used for mission
    ///   decisions (every decision must be processed).
    /// </summary>
    public sealed class ChannelRegistry
    {
        // ----------------------------------------------------------------
        // Vehicle state channels
        // Writer: MqttListenerService (or SimulationEngineService)
        // Reader: FleetManagerService
        // ----------------------------------------------------------------

        /// <summary>
        /// Full VDA 5050 state updates from vehicles.
        /// Triggers fleet manager evaluation cycle.
        /// </summary>
        public Channel<VehicleStateUpdate> VehicleStateUpdates { get; }
            = Channel.CreateBounded<VehicleStateUpdate>(
                new BoundedChannelOptions(500)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false   // multiple vehicles write
                });

        /// <summary>
        /// High-frequency position updates from VDA 5050 visualization topic.
        /// Used for dashboard real-time map updates only.
        /// Writer: MqttListenerService
        /// Reader: DashboardHub (SignalR)
        /// </summary>
        public Channel<VehiclePositionUpdate> VehiclePositionUpdates { get; }
            = Channel.CreateBounded<VehiclePositionUpdate>(
                new BoundedChannelOptions(1000)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });

        // ----------------------------------------------------------------
        // Mission dispatch channels
        // Writer: FleetManagerService
        // Reader: TrafficManagerService + MqttPublisherService
        // ----------------------------------------------------------------

        /// <summary>
        /// Dispatch decisions — vehicle assigned to mission, route planned.
        /// Traffic manager reserves resources; MQTT publisher sends order.
        /// </summary>
        public Channel<MissionDispatchDecision> DispatchDecisions { get; }
            = Channel.CreateBounded<MissionDispatchDecision>(
                new BoundedChannelOptions(100)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,  // both traffic + MQTT read
                    SingleWriter = true
                });

        // ----------------------------------------------------------------
        // Charge assignment channels
        // Writer: ChargeQueueManagerService
        // Reader: FleetManagerService + MqttPublisherService
        // ----------------------------------------------------------------

        /// <summary>
        /// Charge slot assignments — vehicle directed to charge station.
        /// Fleet manager updates vehicle state; MQTT publisher sends order.
        /// </summary>
        public Channel<ChargeAssignment> ChargeAssignments { get; }
            = Channel.CreateBounded<ChargeAssignment>(
                new BoundedChannelOptions(50)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = true
                });

        // ----------------------------------------------------------------
        // Mission transfer channels
        // Writer: TrafficManagerService (on vehicle fault detection)
        // Reader: FleetManagerService
        // ----------------------------------------------------------------

        /// <summary>
        /// Mission transfers triggered by vehicle fault or removal.
        /// Fleet manager handles orphaned lock release + reassignment.
        /// CRITICAL: orphaned lock release happens FIRST on consumption.
        /// </summary>
        public Channel<MissionTransfer> MissionTransfers { get; }
            = Channel.CreateBounded<MissionTransfer>(
                new BoundedChannelOptions(50)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });

        // ----------------------------------------------------------------
        // Mission swap channels
        // Writer: SwapCandidateEvaluator
        // Reader: FleetManagerService
        // ----------------------------------------------------------------

        /// <summary>
        /// Mission swap proposals — two vehicles at sibling pickup nodes
        /// in mismatched arrival order (P&G Tabler Station pattern).
        /// </summary>
        public Channel<MissionSwap> MissionSwaps { get; }
            = Channel.CreateBounded<MissionSwap>(
                new BoundedChannelOptions(20)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true
                });

        // ----------------------------------------------------------------
        // Routing rebuild channel
        // Writer: TrafficManagerService (on block state change)
        // Reader: RoutingBackgroundService
        // ----------------------------------------------------------------

        /// <summary>
        /// Signals the routing engine to rebuild its pose-expanded graph
        /// because runtime blocking state has changed.
        /// Uses a unit-value channel — only the signal matters, not data.
        /// </summary>
        public Channel<bool> RoutingRebuildRequests { get; }
            = Channel.CreateBounded<bool>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });

        // ----------------------------------------------------------------
        // Forensic flush channel
        // Writer: Any service detecting a mishap event
        // Reader: ForensicBufferService
        // ----------------------------------------------------------------

        /// <summary>
        /// Forensic flush trigger — mishap event detected, flush buffers.
        /// </summary>
        public Channel<ForensicFlushRequest> ForensicFlushRequests { get; }
            = Channel.CreateBounded<ForensicFlushRequest>(
                new BoundedChannelOptions(20)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });

        public Channel<VehicleStateUpdate> DashboardStateUpdates { get; } =
            Channel.CreateUnbounded<VehicleStateUpdate>();
        public Channel<MissionCounterUpdate> MissionCounters { get; } =
            Channel.CreateUnbounded<MissionCounterUpdate>();
        public Channel<SimClockUpdate> SimClock { get; } =
            Channel.CreateUnbounded<SimClockUpdate>();
        public Channel<AlertUpdate> Alerts { get; } =
            Channel.CreateUnbounded<AlertUpdate>();
        public Channel<VehicleMissionUpdate> MissionUpdates { get; } =
            Channel.CreateUnbounded<VehicleMissionUpdate>();
    }

    /// <summary>
    /// A forensic flush request — identifies the triggering event
    /// and the vehicles whose buffers should be flushed.
    /// </summary>
    public sealed record ForensicFlushRequest
    {
        public string TriggerEvent { get; init; } = string.Empty;
        public int PrimaryVehicleId { get; init; }
        public IReadOnlyList<int> InvolvedVehicleIds { get; init; }
            = Array.Empty<int>();
        public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
    }
}