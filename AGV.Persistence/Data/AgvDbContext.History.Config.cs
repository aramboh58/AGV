using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

namespace AGV.Persistence.Data
{
    /// <summary>
    /// AgvDbContext partial — history table EF Core configuration.
    /// </summary>
    public sealed partial class AgvDbContext
    {
        private static void ConfigureHistory(ModelBuilder mb)
        {
            // ----------------------------------------------------------------
            // MissionHistoryRecord
            // ----------------------------------------------------------------
            mb.Entity<MissionHistoryRecord>(e =>
            {
                e.ToTable("MissionHistory");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.MissionId).IsRequired();
                e.Property(r => r.OrderId).IsRequired().HasMaxLength(50);
                e.Property(r => r.MissionType).IsRequired().HasMaxLength(50);
                e.Property(r => r.SourceSystemRef).HasMaxLength(200);
                e.Property(r => r.LoadIdentity).HasMaxLength(200);
                e.Property(r => r.OutcomeStatus).IsRequired().HasMaxLength(50);
                e.Property(r => r.CreatedAt).IsRequired();

                e.HasIndex(r => r.MissionId)
                 .HasDatabaseName("IX_MissionHistory_MissionId");
                e.HasIndex(r => r.CreatedAt)
                 .HasDatabaseName("IX_MissionHistory_CreatedAt");
                e.HasIndex(r => r.VehicleId)
                 .HasDatabaseName("IX_MissionHistory_VehicleId");
            });

            // ----------------------------------------------------------------
            // MissionStateTransitionRecord
            // ----------------------------------------------------------------
            mb.Entity<MissionStateTransitionRecord>(e =>
            {
                e.ToTable("MissionStateTransition");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.MissionId).IsRequired();
                e.Property(r => r.FromState).IsRequired().HasMaxLength(50);
                e.Property(r => r.ToState).IsRequired().HasMaxLength(50);
                e.Property(r => r.Reason).IsRequired().HasMaxLength(100);
                e.Property(r => r.Timestamp).IsRequired();

                e.HasIndex(r => new { r.MissionId, r.Timestamp })
                 .HasDatabaseName("IX_MissionStateTransition_MissionId_Time");
            });

            // ----------------------------------------------------------------
            // VehicleStatusHistoryRecord
            // ----------------------------------------------------------------
            mb.Entity<VehicleStatusHistoryRecord>(e =>
            {
                e.ToTable("VehicleStatusHistory");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.VehicleId).IsRequired();
                e.Property(r => r.Timestamp).IsRequired();
                e.Property(r => r.IsHeartbeat).IsRequired();
                e.Property(r => r.BatteryStateOfCharge)
                 .HasColumnType("decimal(5,2)");
                e.Property(r => r.X).HasColumnType("decimal(10,2)");
                e.Property(r => r.Y).HasColumnType("decimal(10,2)");
                e.Property(r => r.HeadingDegrees)
                 .HasColumnType("decimal(8,4)");
                e.Property(r => r.ErrorState).HasMaxLength(200);

                e.HasIndex(r => new { r.VehicleId, r.Timestamp })
                 .HasDatabaseName("IX_VehicleStatusHistory_VehicleId_Time");
            });

            // ----------------------------------------------------------------
            // VehicleStatusHistoryHourlyRecord
            // ----------------------------------------------------------------
            mb.Entity<VehicleStatusHistoryHourlyRecord>(e =>
            {
                e.ToTable("VehicleStatusHistoryHourly");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.VehicleId).IsRequired();
                e.Property(r => r.PeriodStart).IsRequired();
                e.Property(r => r.AvgBatterySOC).HasColumnType("decimal(5,2)");
                e.Property(r => r.MinBatterySOC).HasColumnType("decimal(5,2)");

                e.HasIndex(r => new { r.VehicleId, r.PeriodStart })
                 .IsUnique()
                 .HasDatabaseName("UX_VehicleStatusHourly_VehicleId_Period");
            });

            // ----------------------------------------------------------------
            // VehicleStatusHistoryDailyRecord
            // ----------------------------------------------------------------
            mb.Entity<VehicleStatusHistoryDailyRecord>(e =>
            {
                e.ToTable("VehicleStatusHistoryDaily");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.VehicleId).IsRequired();
                e.Property(r => r.PeriodDate).IsRequired();
                e.Property(r => r.AvgBatterySOC).HasColumnType("decimal(5,2)");
                e.Property(r => r.MinBatterySOC).HasColumnType("decimal(5,2)");

                e.HasIndex(r => new { r.VehicleId, r.PeriodDate })
                 .IsUnique()
                 .HasDatabaseName("UX_VehicleStatusDaily_VehicleId_Period");
            });

            // ----------------------------------------------------------------
            // ChargeCycleHistoryRecord
            // ----------------------------------------------------------------
            mb.Entity<ChargeCycleHistoryRecord>(e =>
            {
                e.ToTable("ChargeCycleHistory");
                e.HasKey(r => r.CycleId);
                e.Property(r => r.CycleId).ValueGeneratedOnAdd();
                e.Property(r => r.VehicleId).IsRequired();
                e.Property(r => r.ChargeType).IsRequired().HasMaxLength(50);
                e.Property(r => r.ChargeStartedAt).IsRequired();
                e.Property(r => r.SOCAtStart).HasColumnType("decimal(5,2)");
                e.Property(r => r.SOCAtEnd).HasColumnType("decimal(5,2)");
                e.Property(r => r.BatteryTempAtStart)
                 .HasColumnType("decimal(5,2)");
                e.Property(r => r.BatteryTempAtEnd)
                 .HasColumnType("decimal(5,2)");
                e.Property(r => r.BatteryTempPeak)
                 .HasColumnType("decimal(5,2)");
                e.Property(r => r.FaultCode).HasMaxLength(50);

                e.HasIndex(r => new { r.VehicleId, r.ChargeStartedAt })
                 .HasDatabaseName("IX_ChargeCycleHistory_VehicleId_Time");
            });

            // ----------------------------------------------------------------
            // ChargeCycleSOCTraceRecord
            // ----------------------------------------------------------------
            mb.Entity<ChargeCycleSOCTraceRecord>(e =>
            {
                e.ToTable("ChargeCycleSOCTrace");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.CycleId).IsRequired();
                e.Property(r => r.Timestamp).IsRequired();
                e.Property(r => r.SOC).HasColumnType("decimal(5,2)");
                e.Property(r => r.BatteryTemp).HasColumnType("decimal(5,2)");

                e.HasIndex(r => new { r.CycleId, r.Timestamp })
                 .HasDatabaseName("IX_ChargeCycleSOCTrace_CycleId_Time");
            });

            // ----------------------------------------------------------------
            // FleetPerformanceHourlyRecord
            // ----------------------------------------------------------------
            mb.Entity<FleetPerformanceHourlyRecord>(e =>
            {
                e.ToTable("FleetPerformanceHourly");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.PeriodStart).IsRequired();

                e.HasIndex(r => r.PeriodStart)
                 .IsUnique()
                 .HasDatabaseName("UX_FleetPerformanceHourly_Period");
            });

            // ----------------------------------------------------------------
            // VehicleUtilizationHourlyRecord
            // ----------------------------------------------------------------
            mb.Entity<VehicleUtilizationHourlyRecord>(e =>
            {
                e.ToTable("VehicleUtilizationHourly");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.VehicleId).IsRequired();
                e.Property(r => r.PeriodStart).IsRequired();

                e.HasIndex(r => new { r.VehicleId, r.PeriodStart })
                 .IsUnique()
                 .HasDatabaseName("UX_VehicleUtilizationHourly_VehicleId_Period");
            });

            // ----------------------------------------------------------------
            // LockContentionHistoryRecord
            // ----------------------------------------------------------------
            mb.Entity<LockContentionHistoryRecord>(e =>
            {
                e.ToTable("LockContentionHistory");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.WaitingVehicleId).IsRequired();
                e.Property(r => r.BlockingVehicleId).IsRequired();
                e.Property(r => r.BlockedNodeId).IsRequired();
                e.Property(r => r.ContentionStartedAt).IsRequired();
                e.Property(r => r.ResolutionType).IsRequired().HasMaxLength(50);

                e.HasIndex(r => new
                {
                    r.WaitingVehicleId,
                    r.ContentionStartedAt
                })
                 .HasDatabaseName("IX_LockContention_VehicleId_Time");
            });

            // ----------------------------------------------------------------
            // DeadlockHistoryRecord
            // ----------------------------------------------------------------
            mb.Entity<DeadlockHistoryRecord>(e =>
            {
                e.ToTable("DeadlockHistory");
                e.HasKey(r => r.DeadlockId);
                e.Property(r => r.DeadlockId).ValueGeneratedOnAdd();
                e.Property(r => r.DetectedAt).IsRequired();
                e.Property(r => r.InvolvedVehicleIds)
                 .IsRequired().HasMaxLength(200);

                e.HasIndex(r => r.DetectedAt)
                 .HasDatabaseName("IX_DeadlockHistory_DetectedAt");
            });

            // ----------------------------------------------------------------
            // ForensicFlushRecord
            // ----------------------------------------------------------------
            mb.Entity<ForensicFlushRecord>(e =>
            {
                e.ToTable("ForensicFlush");
                e.HasKey(r => r.FlushId);
                e.Property(r => r.FlushId).ValueGeneratedOnAdd();
                e.Property(r => r.TriggerEvent).IsRequired().HasMaxLength(100);
                e.Property(r => r.InvolvedVehicleIds)
                 .IsRequired().HasMaxLength(200);
                e.Property(r => r.FlushTimestamp).IsRequired();

                e.HasIndex(r => r.FlushTimestamp)
                 .HasDatabaseName("IX_ForensicFlush_Timestamp");
                e.HasIndex(r => r.PrimaryVehicleId)
                 .HasDatabaseName("IX_ForensicFlush_VehicleId");
            });

            // ----------------------------------------------------------------
            // ForensicCommandLogRecord
            // ----------------------------------------------------------------
            mb.Entity<ForensicCommandLogRecord>(e =>
            {
                e.ToTable("ForensicCommandLog");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.FlushId).IsRequired();
                e.Property(r => r.VehicleId).IsRequired();
                e.Property(r => r.Timestamp).IsRequired();
                e.Property(r => r.MessageType).IsRequired().HasMaxLength(50);
                e.Property(r => r.OrderId).HasMaxLength(50);
                e.Property(r => r.ActionId).HasMaxLength(100);
                e.Property(r => r.NodeSequence).HasMaxLength(500);
                e.Property(r => r.MqttTopic).HasMaxLength(300);
                e.Property(r => r.RawPayload);

                e.HasIndex(r => new { r.FlushId, r.VehicleId, r.Timestamp })
                 .HasDatabaseName("IX_ForensicCommandLog_Flush_Vehicle_Time");
            });

            // ----------------------------------------------------------------
            // ForensicStatusLogRecord
            // ----------------------------------------------------------------
            mb.Entity<ForensicStatusLogRecord>(e =>
            {
                e.ToTable("ForensicStatusLog");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.FlushId).IsRequired();
                e.Property(r => r.VehicleId).IsRequired();
                e.Property(r => r.Timestamp).IsRequired();
                e.Property(r => r.BatterySOC).HasColumnType("decimal(5,2)");
                e.Property(r => r.X).HasColumnType("decimal(10,2)");
                e.Property(r => r.Y).HasColumnType("decimal(10,2)");
                e.Property(r => r.HeadingDegrees)
                 .HasColumnType("decimal(8,4)");
                e.Property(r => r.OperatingMode).IsRequired().HasMaxLength(50);
                e.Property(r => r.ActiveOrderId).HasMaxLength(50);
                e.Property(r => r.ActionStates);
                e.Property(r => r.Errors);

                e.HasIndex(r => new { r.FlushId, r.VehicleId, r.Timestamp })
                 .HasDatabaseName("IX_ForensicStatusLog_Flush_Vehicle_Time");
            });

            // ----------------------------------------------------------------
            // ForensicTrafficLogRecord
            // ----------------------------------------------------------------
            mb.Entity<ForensicTrafficLogRecord>(e =>
            {
                e.ToTable("ForensicTrafficLog");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.FlushId).IsRequired();
                e.Property(r => r.VehicleId).IsRequired();
                e.Property(r => r.Timestamp).IsRequired();
                e.Property(r => r.Phase).IsRequired().HasMaxLength(50);
                e.Property(r => r.ResourcesChecked);
                e.Property(r => r.CheckResults);
                e.Property(r => r.ResourcesLocked);
                e.Property(r => r.PhaseOutcome).IsRequired().HasMaxLength(50);

                e.HasIndex(r => new { r.FlushId, r.VehicleId, r.Timestamp })
                 .HasDatabaseName("IX_ForensicTrafficLog_Flush_Vehicle_Time");
            });
        }
    }
}