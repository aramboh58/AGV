using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AGV.Core.Entities;
using AGV.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AGV.Persistence.Data
{
    /// <summary>
    /// Entity Framework Core database context for the AGV host system.
    ///
    /// Provider-agnostic — the database provider (SQL Server, MySQL,
    /// SQLite) is injected at startup via the DI container based on
    /// the DatabaseProvider configuration key. No provider-specific
    /// code exists in this class.
    ///
    /// Entity configuration follows the Fluent API pattern exclusively
    /// — no data annotations on domain entities. This keeps AGV.Core
    /// entities free of any persistence concerns.
    ///
    /// DbSet naming convention: plural of entity name.
    /// Table naming convention: same as DbSet name (no pluralization
    /// surprises — explicit ToTable() calls throughout).
    /// </summary>
    public sealed partial class AgvDbContext : DbContext
    {
        public AgvDbContext(DbContextOptions<AgvDbContext> options)
            : base(options) { }

        // ----------------------------------------------------------------
        // Topology
        // ----------------------------------------------------------------
        public DbSet<Node> Nodes => Set<Node>();
        public DbSet<Move> Moves => Set<Move>();
        public DbSet<Area> Areas => Set<Area>();
        public DbSet<Zone> Zones => Set<Zone>();
        public DbSet<AreaNodeRecord> AreaNodes
            => Set<AreaNodeRecord>();
        public DbSet<NodeBlockRecord> NodeBlocks
            => Set<NodeBlockRecord>();
        public DbSet<MoveBlockRecord> MoveBlocks
            => Set<MoveBlockRecord>();

        // ----------------------------------------------------------------
        // Location layer
        // ----------------------------------------------------------------
        public DbSet<Location> Locations => Set<Location>();
        public DbSet<LocationAssignment> LocationAssignments => Set<LocationAssignment>();

        // ----------------------------------------------------------------
        // Fleet
        // ----------------------------------------------------------------
        public DbSet<Vehicle> Vehicles => Set<Vehicle>();
        public DbSet<VehicleFactSheet> VehicleFactSheets => Set<VehicleFactSheet>();
        public DbSet<Mission> Missions => Set<Mission>();
        public DbSet<RoadmapVersionRecord> RoadmapVersions
            => Set<RoadmapVersionRecord>();

        // ----------------------------------------------------------------
        // Model configuration
        // ----------------------------------------------------------------
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            ConfigureNode(modelBuilder);
            ConfigureMove(modelBuilder);
            ConfigureArea(modelBuilder);
            ConfigureAreaNode(modelBuilder);
            ConfigureNodeBlock(modelBuilder);
            ConfigureMoveBlock(modelBuilder);
            ConfigureZone(modelBuilder);
            ConfigureLocation(modelBuilder);
            ConfigureLocationAssignment(modelBuilder);
            ConfigureVehicle(modelBuilder);
            modelBuilder.Entity<Vehicle>().Ignore(v => v.PlannedRouteNodeIds);
            ConfigureVehicleFactSheet(modelBuilder); ConfigureMission(modelBuilder);
            ConfigureHistory(modelBuilder);
            ConfigureRoadmapVersion(modelBuilder);
        }

        // ------------------------------------------------------------------
        // Roadmap Version
        //-------------------------------------------------------------------
        private static void ConfigureRoadmapVersion(ModelBuilder mb)
        {
            mb.Entity<RoadmapVersionRecord>(e =>
            {
                e.ToTable("RoadmapVersion");
                e.HasKey(r => r.VersionId);
                e.Property(r => r.VersionId).ValueGeneratedOnAdd();
                e.Property(r => r.VersionLabel).IsRequired().HasMaxLength(100);
                e.Property(r => r.IsActive).IsRequired();
                e.Property(r => r.CreatedAt).IsRequired();
                e.Property(r => r.CreatedByUser).IsRequired().HasMaxLength(100);

                e.HasIndex(r => r.IsActive)
                 .HasDatabaseName("IX_RoadmapVersion_IsActive");
            });
        }

        // ----------------------------------------------------------------
        // Node
        // ----------------------------------------------------------------
        private static void ConfigureNode(ModelBuilder mb)
        {
            mb.Entity<Node>(e =>
            {
                e.ToTable("Node");
                e.HasKey(n => n.NodeRecordId);
                e.Property(n => n.NodeRecordId).ValueGeneratedOnAdd();
                e.Property(n => n.NodeId).IsRequired();
                e.Property(n => n.EffectiveFromVersionId).IsRequired();
                e.Property(n => n.IsDeleted).IsRequired().HasDefaultValue(false);
                e.Property(n => n.NodeName).HasMaxLength(100);
                e.Property(n => n.MapId).IsRequired().HasMaxLength(100);
                e.Property(n => n.NodeType).IsRequired();

                // Coordinate value object — owned entity
                e.OwnsOne(n => n.Position, pos =>
                {
                    pos.Property(p => p.X)
                       .HasColumnName("X")
                       .HasColumnType("decimal(10,2)")
                       .IsRequired();
                    pos.Property(p => p.Y)
                       .HasColumnName("Y")
                       .HasColumnType("decimal(10,2)")
                       .IsRequired();
                    pos.Property(p => p.Z)
                       .HasColumnName("Z")
                       .HasColumnType("decimal(10,2)")
                       .IsRequired()
                       .HasDefaultValue(0m);
                });

                e.HasIndex(n => new { n.NodeId, n.EffectiveFromVersionId })
                 .IsUnique()
                 .HasDatabaseName("UX_Node_NodeId_Version");
            });
        }

        // ----------------------------------------------------------------
        // Move
        // ----------------------------------------------------------------
        private static void ConfigureMove(ModelBuilder mb)
        {
            mb.Entity<Move>(e =>
            {
                e.ToTable("Move");
                e.HasKey(m => m.MoveRecordId);
                e.Property(m => m.MoveRecordId).ValueGeneratedOnAdd();
                e.Property(m => m.MoveId).IsRequired();
                e.Property(m => m.EffectiveFromVersionId).IsRequired();
                e.Property(m => m.IsDeleted).IsRequired().HasDefaultValue(false);
                e.Property(m => m.FromNodeId).IsRequired();
                e.Property(m => m.ToNodeId).IsRequired();
                e.Property(m => m.RoutingTypeId).IsRequired();
                e.Property(m => m.TravelDirection).IsRequired();
                e.Property(m => m.MaxWeightCapacityKg)
                 .HasColumnType("decimal(10,2)");

                // ClothoidParameters value object — owned entity
                e.OwnsOne(m => m.Clothoid, c =>
                {
                    c.Property(p => p.StartHeading)
                     .HasColumnName("StartHeading")
                     .HasColumnType("decimal(8,4)")
                     .IsRequired();
                    c.Property(p => p.EndHeading)
                     .HasColumnName("EndHeading")
                     .HasColumnType("decimal(8,4)")
                     .IsRequired();
                    c.Property(p => p.ParameterA)
                     .HasColumnName("ParameterA")
                     .HasColumnType("decimal(12,6)")
                     .IsRequired();
                    c.Property(p => p.ArcLength)
                     .HasColumnName("ArcLength")
                     .HasColumnType("decimal(10,2)")
                     .IsRequired();
                });

                // SpeedConstraint value object — owned entity
                e.OwnsOne(m => m.Speed, s =>
                {
                    s.Property(p => p.DefaultSpeed)
                     .HasColumnName("DefaultSpeed")
                     .HasColumnType("decimal(6,4)")
                     .IsRequired();
                    s.Property(p => p.MaxSpeed)
                     .HasColumnName("MaxSpeed")
                     .HasColumnType("decimal(6,4)")
                     .IsRequired();
                });

                e.HasIndex(m => new { m.MoveId, m.EffectiveFromVersionId })
                 .IsUnique()
                 .HasDatabaseName("UX_Move_MoveId_Version");

                e.HasIndex(m => new { m.FromNodeId, m.ToNodeId })
                 .HasDatabaseName("IX_Move_FromTo");
            });
        }

        // ----------------------------------------------------------------
        // Area
        // ----------------------------------------------------------------
        private static void ConfigureArea(ModelBuilder mb)
        {
            mb.Entity<Area>(e =>
            {
                e.ToTable("Area");
                e.HasKey(a => a.AreaRecordId);
                e.Property(a => a.AreaRecordId).ValueGeneratedOnAdd();
                e.Property(a => a.AreaId).IsRequired();
                e.Property(a => a.EffectiveFromVersionId).IsRequired();
                e.Property(a => a.IsDeleted).IsRequired().HasDefaultValue(false);
                e.Property(a => a.AreaName).IsRequired().HasMaxLength(100);
                e.Property(a => a.Description).HasMaxLength(500);
                e.Property(a => a.MaxVehicleCount);

                e.HasIndex(a => new { a.AreaId, a.EffectiveFromVersionId })
                 .IsUnique()
                 .HasDatabaseName("UX_Area_AreaId_Version");
            });
        }
        private static void ConfigureAreaNode(ModelBuilder mb)
        {
            mb.Entity<AreaNodeRecord>(e =>
            {
                e.ToTable("AreaNode");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.AreaId).IsRequired();
                e.Property(r => r.NodeId).IsRequired();
                e.Property(r => r.EffectiveFromVersionId).IsRequired();
                e.Property(r => r.IsDeleted).IsRequired().HasDefaultValue(false);

                e.HasIndex(r => new { r.AreaId, r.NodeId, r.EffectiveFromVersionId })
                 .IsUnique()
                 .HasDatabaseName("UX_AreaNode_Area_Node_Version");
                e.HasIndex(r => r.NodeId)
                 .HasDatabaseName("IX_AreaNode_NodeId");
            });
        }

        private static void ConfigureNodeBlock(ModelBuilder mb)
        {
            mb.Entity<NodeBlockRecord>(e =>
            {
                e.ToTable("NodeBlock");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.NodeId).IsRequired();
                e.Property(r => r.BlockReason).IsRequired();
                e.Property(r => r.Description).HasMaxLength(500);
                e.Property(r => r.IsEngineerBlock).IsRequired()
                 .HasDefaultValue(false);

                e.HasIndex(r => r.NodeId)
                 .HasDatabaseName("IX_NodeBlock_NodeId");
                e.HasIndex(r => r.IsEngineerBlock)
                 .HasDatabaseName("IX_NodeBlock_IsEngineerBlock");
            });
        }

        private static void ConfigureMoveBlock(ModelBuilder mb)
        {
            mb.Entity<MoveBlockRecord>(e =>
            {
                e.ToTable("MoveBlock");
                e.HasKey(r => r.Id);
                e.Property(r => r.Id).ValueGeneratedOnAdd();
                e.Property(r => r.MoveId).IsRequired();
                e.Property(r => r.BlockReason).IsRequired();
                e.Property(r => r.Description).HasMaxLength(500);
                e.Property(r => r.IsEngineerBlock).IsRequired()
                 .HasDefaultValue(false);

                e.HasIndex(r => r.MoveId)
                 .HasDatabaseName("IX_MoveBlock_MoveId");
                e.HasIndex(r => r.IsEngineerBlock)
                 .HasDatabaseName("IX_MoveBlock_IsEngineerBlock");
            });
        }
        // ----------------------------------------------------------------
        // Zone
        // ----------------------------------------------------------------
        private static void ConfigureZone(ModelBuilder mb)
        {
            mb.Entity<Zone>(e =>
            {
                e.ToTable("Zone");
                e.HasKey(z => z.ZoneId);
                e.Property(z => z.ZoneId).ValueGeneratedOnAdd();
                e.Property(z => z.ZoneName).IsRequired().HasMaxLength(100);
                e.Property(z => z.Description).HasMaxLength(500);
                e.Property(z => z.RequiredVehicleCount);
                e.Property(z => z.IsActive).IsRequired().HasDefaultValue(true);
            });
        }

        // ----------------------------------------------------------------
        // Location
        // ----------------------------------------------------------------
        private static void ConfigureLocation(ModelBuilder mb)
        {
            mb.Entity<Location>(e =>
            {
                e.ToTable("Location");
                e.HasKey(l => l.LocationRecordId);
                e.Property(l => l.LocationRecordId).ValueGeneratedOnAdd();
                e.Property(l => l.LocationId).IsRequired();
                e.Property(l => l.EffectiveFromLocationVersionId).IsRequired();
                e.Property(l => l.IsDeleted).IsRequired().HasDefaultValue(false);
                e.Property(l => l.LocationName).IsRequired().HasMaxLength(200);
                e.Property(l => l.Description).HasMaxLength(500);

                e.HasIndex(l => new
                {
                    l.LocationId,
                    l.EffectiveFromLocationVersionId
                })
                 .IsUnique()
                 .HasDatabaseName("UX_Location_LocationId_Version");
            });
        }

        // ----------------------------------------------------------------
        // LocationAssignment
        // ----------------------------------------------------------------
        private static void ConfigureLocationAssignment(ModelBuilder mb)
        {
            mb.Entity<LocationAssignment>(e =>
            {
                e.ToTable("LocationAssignment");
                e.HasKey(a => a.AssignmentRecordId);
                e.Property(a => a.AssignmentRecordId).ValueGeneratedOnAdd();
                e.Property(a => a.AssignmentId).IsRequired();
                e.Property(a => a.EffectiveFromLocationVersionId).IsRequired();
                e.Property(a => a.IsDeleted).IsRequired().HasDefaultValue(false);
                e.Property(a => a.LocationId).IsRequired();
                e.Property(a => a.NodeId).IsRequired();
                e.Property(a => a.OperationTypeId).IsRequired();
                e.Property(a => a.LocationTypeId).IsRequired();
                e.Property(a => a.GuardedAssignmentId);

                e.HasIndex(a => new
                {
                    a.AssignmentId,
                    a.EffectiveFromLocationVersionId
                })
                 .IsUnique()
                 .HasDatabaseName("UX_LocationAssignment_Id_Version");

                e.HasIndex(a => a.LocationId)
                 .HasDatabaseName("IX_LocationAssignment_LocationId");

                e.HasIndex(a => a.NodeId)
                 .HasDatabaseName("IX_LocationAssignment_NodeId");
            });
        }

        // ----------------------------------------------------------------
        // Vehicle
        // ----------------------------------------------------------------
        private static void ConfigureVehicle(ModelBuilder mb)
        {
            mb.Entity<Vehicle>(e =>
            {
                e.ToTable("Vehicle");
                e.HasKey(v => v.VehicleId);
                e.Property(v => v.VehicleId).ValueGeneratedNever();
                e.Property(v => v.VehicleName).IsRequired().HasMaxLength(50);
                e.Property(v => v.SerialNumber).IsRequired().HasMaxLength(100);
                e.Property(v => v.VehicleType).IsRequired();
                e.Property(v => v.CurrentZoneId);
                e.Property(v => v.CurrentNodeId);
                e.Property(v => v.CurrentMapId).IsRequired().HasMaxLength(100);
                e.Property(v => v.BatteryStateOfCharge)
                 .HasColumnType("decimal(5,2)");
                e.Property(v => v.ActivityState).IsRequired();
                e.Property(v => v.OrderState).IsRequired();
                e.Property(v => v.OperatingMode).IsRequired();
                e.Property(v => v.IsLoaded).IsRequired().HasDefaultValue(false);
                e.Property(v => v.IsInService).IsRequired().HasDefaultValue(true);
                e.Property(v => v.IsOnline).IsRequired().HasDefaultValue(false);
                e.Property(v => v.CurrentMissionId);
                e.Property(v => v.LastStateReceivedAt);
                e.Property(v => v.CreatedAt).IsRequired();

                e.HasIndex(v => v.SerialNumber)
                 .IsUnique()
                 .HasDatabaseName("UX_Vehicle_SerialNumber");
            });
        }

        // ----------------------------------------------------------------
        // VehicleFactSheet
        // ----------------------------------------------------------------
        private static void ConfigureVehicleFactSheet(ModelBuilder mb)
        {
            mb.Entity<VehicleFactSheet>(e =>
            {
                e.ToTable("VehicleFactSheet");
                e.HasKey(f => f.VehicleId);
                e.Property(f => f.VehicleId).ValueGeneratedNever();
                e.Property(f => f.ProtocolVersion)
                 .IsRequired().HasMaxLength(20);
                e.Property(f => f.MaxOrderHorizonDepth).IsRequired();
                e.Property(f => f.SupportsNurbsTrajectory).IsRequired();
                e.Property(f => f.SupportedActionTypes)
                 .IsRequired().HasMaxLength(500);
                e.Property(f => f.MaxSpeedMs)
                 .HasColumnType("decimal(6,4)");
                e.Property(f => f.MaxPayloadKg)
                 .HasColumnType("decimal(8,2)");
                e.Property(f => f.LengthMeters)
                 .HasColumnType("decimal(5,3)");
                e.Property(f => f.WidthMeters)
                 .HasColumnType("decimal(5,3)");
                e.Property(f => f.LastReceivedAt).IsRequired();

                // One-to-one with Vehicle
                e.HasOne<Vehicle>()
                 .WithOne()
                 .HasForeignKey<VehicleFactSheet>(f => f.VehicleId);
            });
        }

        // ----------------------------------------------------------------
        // Mission
        // ----------------------------------------------------------------
        private static void ConfigureMission(ModelBuilder mb)
        {
            mb.Entity<Mission>(e =>
            {
                e.ToTable("Mission");
                e.HasKey(m => m.MissionId);
                e.Property(m => m.MissionId).ValueGeneratedOnAdd();
                e.Property(m => m.OrderId).IsRequired().HasMaxLength(50);
                e.Property(m => m.AssignedVehicleId);
                e.Property(m => m.PickupAssignmentId).IsRequired();
                e.Property(m => m.DropoffAssignmentId).IsRequired();
                e.Property(m => m.LoadType).IsRequired();
                e.Property(m => m.State).IsRequired();
                e.Property(m => m.Priority).IsRequired();
                e.Property(m => m.CreatedAt).IsRequired();
                e.Property(m => m.DispatchedAt);
                e.Property(m => m.PickupArrivedAt);
                e.Property(m => m.PickupCompletedAt);
                e.Property(m => m.DropoffArrivedAt);
                e.Property(m => m.CompletedAt);
                e.Property(m => m.FailedAt);
                e.Property(m => m.FailureReason).HasMaxLength(500);

                e.HasIndex(m => m.OrderId)
                 .HasDatabaseName("IX_Mission_OrderId");

                e.HasIndex(m => m.AssignedVehicleId)
                 .HasDatabaseName("IX_Mission_VehicleId");

                e.HasIndex(m => m.State)
                 .HasDatabaseName("IX_Mission_State");
            });
        }
    }
}