using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;
using AGV.Core.Messages;

namespace AGV.Core.Interfaces
{
    /// <summary>
    /// Contract for external business system integration.
    ///
    /// Provides a bidirectional tether between the AGV host and
    /// customer-side business systems — SAP, Oracle, WMS, ERP, etc.
    ///
    /// Inbound (external system → AGV host):
    ///   Mission requests, priority overrides, schedule changes
    ///
    /// Outbound (AGV host → external system):
    ///   Mission completions, vehicle status, fault notifications,
    ///   production metrics, inventory movement confirmations
    ///
    /// Three built-in implementations:
    ///   NullAdapter     — no-op, standalone operation (Phase 1 default)
    ///   RestAdapter     — configurable REST client/server (Phase 2)
    ///   MqttAdapter     — non-VDA 5050 MQTT broker integration (Phase 2)
    ///
    /// Site-specific implementations (Phase 3):
    ///   NYTOracleAdapter — Oracle DB, paper roll inventory, press metrics
    ///   PnGSapAdapter    — SAP REST, SKU→drop resolution, order confirmation
    ///
    /// Critical resilience invariant:
    ///   External system unavailability MUST NEVER affect AGV floor
    ///   operations. All outbound calls are fire-and-forget or async
    ///   with timeout. The circuit breaker pattern (Polly) protects
    ///   the host from cascading failures. Outbound events are queued
    ///   locally and replayed when connectivity is restored.
    ///
    /// Relationship to ICustomizationApi:
    ///   ICustomizationApi — internal AGV logic (mission swap rules,
    ///                       storage strategy, APL macro execution)
    ///   IExternalSystemAdapter — crosses system boundary to ERP/WMS
    ///   They interact: adapter receives ERP order → customization
    ///   resolves drop destination → adapter confirms completion to ERP.
    /// </summary>
    public interface IExternalSystemAdapter
    {
        // ----------------------------------------------------------------
        // Inbound — external system → AGV host
        // ----------------------------------------------------------------

        /// <summary>
        /// Registers a handler invoked when an external system
        /// submits a mission request (e.g. SAP pick order,
        /// Oracle roll assignment).
        ///
        /// The handler creates a MissionContext and enqueues it
        /// via IFleetManager.EnqueueMissionAsync.
        /// </summary>
        void OnMissionRequested(
            Func<ExternalMissionRequest,
                CancellationToken, Task> handler);

        /// <summary>
        /// Registers a handler invoked when an external system
        /// requests a priority change on a queued mission.
        /// </summary>
        void OnPriorityOverride(
            Func<ExternalPriorityOverride,
                CancellationToken, Task> handler);

        // ----------------------------------------------------------------
        // Outbound — AGV host → external system
        // ----------------------------------------------------------------

        /// <summary>
        /// Publishes a mission completion event to the external system.
        /// Called when a mission reaches Finished or Failed state.
        ///
        /// Fire-and-forget with retry — never blocks the fleet manager.
        /// </summary>
        Task PublishMissionCompleteAsync(
            MissionContext context,
            MissionOutcome outcome,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Publishes a vehicle status snapshot to the external system.
        /// Called periodically or on significant state changes.
        /// </summary>
        Task PublishVehicleStatusAsync(
            ExternalVehicleStatus status,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Publishes a fault or error event to the external system.
        /// Examples: vehicle fault, mission transfer, deadlock resolved.
        /// </summary>
        Task PublishFaultEventAsync(
            ExternalFaultEvent fault,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Publishes a named metric value to the external system.
        /// Examples: tears per press run (NYT), picks per hour,
        /// fleet utilization percentage.
        /// </summary>
        Task PublishMetricAsync(
            string metricName,
            decimal value,
            string? unit = null,
            CancellationToken cancellationToken = default);

        // ----------------------------------------------------------------
        // Lifecycle
        // ----------------------------------------------------------------

        /// <summary>
        /// Starts the adapter — opens connections, begins polling.
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops the adapter gracefully — flushes queued outbound
        /// events before closing connections.
        /// </summary>
        Task StopAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns true if the external system is currently reachable.
        /// Used by the dashboard to show connectivity status.
        /// </summary>
        bool IsConnected { get; }
    }

    // ----------------------------------------------------------------
    // Supporting types
    // ----------------------------------------------------------------

    /// <summary>
    /// A mission request received from an external business system.
    /// </summary>
    public sealed class ExternalMissionRequest
    {
        /// <summary>
        /// The external system's own reference for this request.
        /// Preserved in MissionContext.SourceSystemReference.
        /// </summary>
        public string ExternalReference { get; init; } = string.Empty;

        /// <summary>
        /// Pickup location identifier — resolved to a NodeId by
        /// ICustomizationApi or the location service.
        /// </summary>
        public string PickupLocationCode { get; init; } = string.Empty;

        /// <summary>
        /// Drop location identifier — may be null for systems
        /// where drop destination is determined at pickup
        /// (P&G pattern — SAP pre-assigns before pickup).
        /// </summary>
        public string? DropLocationCode { get; init; }

        /// <summary>
        /// Optional load identity from the external system.
        /// Examples: SKU, pallet ID, roll barcode.
        /// </summary>
        public string? LoadIdentity { get; init; }

        /// <summary>
        /// Requested priority. External systems may not use the
        /// same priority vocabulary — mapped to MissionPriority
        /// by the site-specific adapter.
        /// </summary>
        public MissionPriority Priority { get; init; }
            = MissionPriority.Normal;

        /// <summary>
        /// Optional deadline by which pickup must be initiated.
        /// </summary>
        public DateTime? PickupDeadline { get; init; }

        /// <summary>UTC timestamp when this request was received.</summary>
        public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A priority override request from an external system.
    /// </summary>
    public sealed class ExternalPriorityOverride
    {
        public string ExternalReference { get; init; } = string.Empty;
        public MissionPriority NewPriority { get; init; }
        public string? Reason { get; init; }
        public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Mission outcome for external system notification.
    /// </summary>
    public enum MissionOutcome
    {
        Completed = 1,
        Failed = 2,
        Cancelled = 3,
        Transferred = 4,
    }

    /// <summary>
    /// Vehicle status snapshot for external system reporting.
    /// </summary>
    public sealed class ExternalVehicleStatus
    {
        public int VehicleId { get; init; }
        public string VehicleName { get; init; } = string.Empty;
        public string ActivityState { get; init; } = string.Empty;
        public decimal BatterySOC { get; init; }
        public bool IsOnline { get; init; }
        public bool IsLoaded { get; init; }
        public int? ActiveMissionId { get; init; }
        public DateTime SnapshotAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Fault event for external system notification.
    /// </summary>
    public sealed class ExternalFaultEvent
    {
        public string FaultType { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public int? VehicleId { get; init; }
        public int? MissionId { get; init; }
        public string Severity { get; init; } = "Warning";
        public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Null implementation — no-op adapter for standalone operation.
    /// Default for Phase 1. All outbound calls silently succeed.
    /// All inbound callbacks never fire (no external system connected).
    /// </summary>
    public sealed class NullExternalSystemAdapter : IExternalSystemAdapter
    {
        public bool IsConnected => false;

        public void OnMissionRequested(
            Func<ExternalMissionRequest,
                CancellationToken, Task> handler)
        { }

        public void OnPriorityOverride(
            Func<ExternalPriorityOverride,
                CancellationToken, Task> handler)
        { }

        public Task PublishMissionCompleteAsync(
            MissionContext context,
            MissionOutcome outcome,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishVehicleStatusAsync(
            ExternalVehicleStatus status,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishFaultEventAsync(
            ExternalFaultEvent fault,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishMetricAsync(
            string metricName,
            decimal value,
            string? unit = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StartAsync(
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StopAsync(
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
