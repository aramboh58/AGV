using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Enums;

namespace AGV.Persistence.Data
{
    // ====================================================================
    // Mission lifecycle history
    // Retention: 2 years
    // ====================================================================

    public sealed class MissionHistoryRecord
    {
        public long Id { get; set; }
        public int MissionId { get; set; }
        public string OrderId { get; set; } = string.Empty;
        public string MissionType { get; set; } = string.Empty;
        public int? VehicleId { get; set; }
        public string? SourceSystemRef { get; set; }
        public int PickupNodeId { get; set; }
        public int DropNodeId { get; set; }
        public string? LoadIdentity { get; set; }
        public MissionPriority Priority { get; set; }
        public int TransferCount { get; set; }
        public string OutcomeStatus { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? AssignedAt { get; set; }
        public DateTime? PickupArrivedAt { get; set; }
        public DateTime? PickupCompletedAt { get; set; }
        public DateTime? DropArrivedAt { get; set; }
        public DateTime? DropCompletedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public double? TotalDurationSeconds { get; set; }
        public double? TravelDurationSeconds { get; set; }
        public double? WaitDurationSeconds { get; set; }
    }

    public sealed class MissionStateTransitionRecord
    {
        public long Id { get; set; }
        public int MissionId { get; set; }
        public string FromState { get; set; } = string.Empty;
        public string ToState { get; set; } = string.Empty;
        public int? VehicleId { get; set; }
        public string Reason { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }

    // ====================================================================
    // Vehicle status history
    // Full resolution retention: 30 days
    // Hourly aggregates retention: 1 year
    // Daily aggregates retention: 5 years
    // ====================================================================

    public sealed class VehicleStatusHistoryRecord
    {
        public long Id { get; set; }
        public int VehicleId { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsHeartbeat { get; set; }   // true = 60s heartbeat, false = change-driven
        public int? LastNodeId { get; set; }
        public decimal? X { get; set; }
        public decimal? Y { get; set; }
        public decimal? HeadingDegrees { get; set; }
        public ActivityState ActivityState { get; set; }
        public OperatingMode OperatingMode { get; set; }
        public decimal BatteryStateOfCharge { get; set; }
        public bool IsLoaded { get; set; }
        public bool IsOnline { get; set; }
        public int? ActiveMissionId { get; set; }
        public string? ErrorState { get; set; }
    }

    public sealed class VehicleStatusHistoryHourlyRecord
    {
        public long Id { get; set; }
        public int VehicleId { get; set; }
        public DateTime PeriodStart { get; set; }   // UTC hour boundary
        public double MinutesDriving { get; set; }
        public double MinutesIdle { get; set; }
        public double MinutesCharging { get; set; }
        public double MinutesWaitingOnLock { get; set; }
        public double MinutesFaulted { get; set; }
        public double MinutesOffline { get; set; }
        public decimal AvgBatterySOC { get; set; }
        public decimal MinBatterySOC { get; set; }
        public int MissionsCompleted { get; set; }
    }

    public sealed class VehicleStatusHistoryDailyRecord
    {
        public long Id { get; set; }
        public int VehicleId { get; set; }
        public DateTime PeriodDate { get; set; }    // UTC date
        public double MinutesDriving { get; set; }
        public double MinutesIdle { get; set; }
        public double MinutesCharging { get; set; }
        public double MinutesWaitingOnLock { get; set; }
        public double MinutesFaulted { get; set; }
        public double MinutesOffline { get; set; }
        public decimal AvgBatterySOC { get; set; }
        public decimal MinBatterySOC { get; set; }
        public int MissionsCompleted { get; set; }
        public int ChargeEventsOpportunity { get; set; }
        public int ChargeEventsMandatory { get; set; }
    }

    // ====================================================================
    // Charge cycle history
    // Retention: 5 years
    // ====================================================================

    public sealed class ChargeCycleHistoryRecord
    {
        public long CycleId { get; set; }
        public int VehicleId { get; set; }
        public int? ChargerId { get; set; }
        public string ChargeType { get; set; } = string.Empty;
        public DateTime ChargeStartedAt { get; set; }
        public DateTime? ChargeCompletedAt { get; set; }
        public double? DurationMinutes { get; set; }
        public decimal SOCAtStart { get; set; }
        public decimal SOCAtEnd { get; set; }
        public double? SOCDeltaPerHour { get; set; }
        public decimal? BatteryTempAtStart { get; set; }
        public decimal? BatteryTempAtEnd { get; set; }
        public decimal? BatteryTempPeak { get; set; }
        public double? EqualizeTimeMinutes { get; set; }
        public bool FaultOccurred { get; set; }
        public string? FaultCode { get; set; }
    }

    public sealed class ChargeCycleSOCTraceRecord
    {
        public long Id { get; set; }
        public long CycleId { get; set; }
        public DateTime Timestamp { get; set; }
        public decimal SOC { get; set; }
        public decimal? BatteryTemp { get; set; }
    }

    // ====================================================================
    // Fleet performance analytics
    // Retention: 5 years
    // ====================================================================

    public sealed class FleetPerformanceHourlyRecord
    {
        public long Id { get; set; }
        public DateTime PeriodStart { get; set; }
        public int MissionsCompleted { get; set; }
        public int MissionsFaulted { get; set; }
        public int MissionsTransferred { get; set; }
        public int MissionsSwapped { get; set; }
        public double AvgMissionDurationSeconds { get; set; }
        public double AvgTravelDurationSeconds { get; set; }
        public double AvgWaitDurationSeconds { get; set; }
        public int DeadlocksDetected { get; set; }
        public int DeadlocksResolved { get; set; }
        public int GhostDeadlocks { get; set; }
        public double VehicleUtilizationPct { get; set; }
        public int OrderStealingEvents { get; set; }
        public int DetourEvents { get; set; }
        public int RedirectEvents { get; set; }
    }

    public sealed class VehicleUtilizationHourlyRecord
    {
        public long Id { get; set; }
        public DateTime PeriodStart { get; set; }
        public int VehicleId { get; set; }
        public double MinutesDriving { get; set; }
        public double MinutesIdle { get; set; }
        public double MinutesCharging { get; set; }
        public double MinutesWaitingOnLock { get; set; }
        public double MinutesFaulted { get; set; }
        public int MissionsCompleted { get; set; }
    }

    // ====================================================================
    // Traffic management history
    // Retention: 1 year
    // ====================================================================

    public sealed class LockContentionHistoryRecord
    {
        public long Id { get; set; }
        public int WaitingVehicleId { get; set; }
        public int BlockingVehicleId { get; set; }
        public int BlockedNodeId { get; set; }
        public DateTime ContentionStartedAt { get; set; }
        public DateTime? ContentionResolvedAt { get; set; }
        public double? WaitDurationSeconds { get; set; }
        public string ResolutionType { get; set; } = string.Empty;
    }

    public sealed class DeadlockHistoryRecord
    {
        public long DeadlockId { get; set; }
        public DateTime DetectedAt { get; set; }
        public DateTime? ConfirmedAt { get; set; }
        public bool IsGhost { get; set; }
        public string InvolvedVehicleIds { get; set; } = string.Empty;
        public int? EscapeNodeUsed { get; set; }
        public int? ResolvingVehicleId { get; set; }
        public DateTime? ResolvedAt { get; set; }
    }

    // ====================================================================
    // Forensic buffer flush records
    // Retention: 90 days
    // ====================================================================

    public sealed class ForensicFlushRecord
    {
        public long FlushId { get; set; }
        public string TriggerEvent { get; set; } = string.Empty;
        public int PrimaryVehicleId { get; set; }
        public string InvolvedVehicleIds { get; set; } = string.Empty;
        public DateTime FlushTimestamp { get; set; }
        public int CommandLogCount { get; set; }
        public int StatusLogCount { get; set; }
        public int TrafficLogCount { get; set; }
    }

    public sealed class ForensicCommandLogRecord
    {
        public long Id { get; set; }
        public long FlushId { get; set; }
        public int VehicleId { get; set; }
        public DateTime Timestamp { get; set; }
        public string MessageType { get; set; } = string.Empty;
        public string? OrderId { get; set; }
        public string? ActionId { get; set; }
        public string? NodeSequence { get; set; }
        public string? MqttTopic { get; set; }
        public string? RawPayload { get; set; }
    }

    public sealed class ForensicStatusLogRecord
    {
        public long Id { get; set; }
        public long FlushId { get; set; }
        public int VehicleId { get; set; }
        public DateTime Timestamp { get; set; }
        public int? LastNodeId { get; set; }
        public decimal? X { get; set; }
        public decimal? Y { get; set; }
        public decimal? HeadingDegrees { get; set; }
        public decimal BatterySOC { get; set; }
        public string OperatingMode { get; set; } = string.Empty;
        public string? ActiveOrderId { get; set; }
        public string? ActionStates { get; set; }
        public string? Errors { get; set; }
    }

    public sealed class ForensicTrafficLogRecord
    {
        public long Id { get; set; }
        public long FlushId { get; set; }
        public int VehicleId { get; set; }
        public DateTime Timestamp { get; set; }
        public int? ItineraryMoveId { get; set; }
        public string Phase { get; set; } = string.Empty;
        public string? ResourcesChecked { get; set; }
        public string? CheckResults { get; set; }
        public string? ResourcesLocked { get; set; }
        public int? ContentionWithVehicleId { get; set; }
        public bool DetourEvaluated { get; set; }
        public bool DetourTaken { get; set; }
        public string PhaseOutcome { get; set; } = string.Empty;
    }
}
