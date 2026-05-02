using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using AGV.Core.Enums;
using AGV.Persistence.Data;

namespace AGV.Persistence.Data
{
    /// <summary>
    /// AgvDbContext partial — historical data tables.
    ///
    /// Separated from the main context file for readability.
    /// All historical tables follow the same conventions:
    ///   — Composite index on (VehicleId, Timestamp) for range queries
    ///   — All retention periods enforced by HistoryAggregationService
    ///   — No foreign keys to operational tables (history is append-only
    ///     and must survive operational record deletion)
    /// </summary>
    public sealed partial class AgvDbContext
    {
        // ----------------------------------------------------------------
        // Mission lifecycle history
        // ----------------------------------------------------------------
        public DbSet<MissionHistoryRecord>
            MissionHistory => Set<MissionHistoryRecord>();

        public DbSet<MissionStateTransitionRecord>
            MissionStateTransitions => Set<MissionStateTransitionRecord>();

        // ----------------------------------------------------------------
        // Vehicle status history
        // ----------------------------------------------------------------
        public DbSet<VehicleStatusHistoryRecord>
            VehicleStatusHistory => Set<VehicleStatusHistoryRecord>();

        public DbSet<VehicleStatusHistoryHourlyRecord>
            VehicleStatusHistoryHourly
            => Set<VehicleStatusHistoryHourlyRecord>();

        public DbSet<VehicleStatusHistoryDailyRecord>
            VehicleStatusHistoryDaily
            => Set<VehicleStatusHistoryDailyRecord>();

        // ----------------------------------------------------------------
        // Charge cycle history
        // ----------------------------------------------------------------
        public DbSet<ChargeCycleHistoryRecord>
            ChargeCycleHistory => Set<ChargeCycleHistoryRecord>();

        public DbSet<ChargeCycleSOCTraceRecord>
            ChargeCycleSOCTrace => Set<ChargeCycleSOCTraceRecord>();

        // ----------------------------------------------------------------
        // Fleet performance analytics
        // ----------------------------------------------------------------
        public DbSet<FleetPerformanceHourlyRecord>
            FleetPerformanceHourly => Set<FleetPerformanceHourlyRecord>();

        public DbSet<VehicleUtilizationHourlyRecord>
            VehicleUtilizationHourly => Set<VehicleUtilizationHourlyRecord>();

        // ----------------------------------------------------------------
        // Traffic management history
        // ----------------------------------------------------------------
        public DbSet<LockContentionHistoryRecord>
            LockContentionHistory => Set<LockContentionHistoryRecord>();

        public DbSet<DeadlockHistoryRecord>
            DeadlockHistory => Set<DeadlockHistoryRecord>();

        // ----------------------------------------------------------------
        // Forensic buffer
        // ----------------------------------------------------------------
        public DbSet<ForensicFlushRecord>
            ForensicFlushes => Set<ForensicFlushRecord>();

        public DbSet<ForensicCommandLogRecord>
            ForensicCommandLog => Set<ForensicCommandLogRecord>();

        public DbSet<ForensicStatusLogRecord>
            ForensicStatusLog => Set<ForensicStatusLogRecord>();

        public DbSet<ForensicTrafficLogRecord>
            ForensicTrafficLog => Set<ForensicTrafficLogRecord>();
    }
}