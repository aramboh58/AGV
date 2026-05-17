using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AGV.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Area",
                columns: table => new
                {
                    AreaRecordId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveFromVersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    AreaName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    MaxVehicleCount = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Area", x => x.AreaRecordId);
                });

            migrationBuilder.CreateTable(
                name: "ChargeCycleHistory",
                columns: table => new
                {
                    CycleId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VehicleId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChargerId = table.Column<int>(type: "INTEGER", nullable: true),
                    ChargeType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ChargeStartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChargeCompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DurationMinutes = table.Column<double>(type: "REAL", nullable: true),
                    SOCAtStart = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    SOCAtEnd = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    SOCDeltaPerHour = table.Column<double>(type: "REAL", nullable: true),
                    BatteryTempAtStart = table.Column<decimal>(type: "decimal(5,2)", nullable: true),
                    BatteryTempAtEnd = table.Column<decimal>(type: "decimal(5,2)", nullable: true),
                    BatteryTempPeak = table.Column<decimal>(type: "decimal(5,2)", nullable: true),
                    EqualizeTimeMinutes = table.Column<double>(type: "REAL", nullable: true),
                    FaultOccurred = table.Column<bool>(type: "INTEGER", nullable: false),
                    FaultCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargeCycleHistory", x => x.CycleId);
                });

            migrationBuilder.CreateTable(
                name: "ChargeCycleSOCTrace",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CycleId = table.Column<long>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SOC = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    BatteryTemp = table.Column<decimal>(type: "decimal(5,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargeCycleSOCTrace", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeadlockHistory",
                columns: table => new
                {
                    DeadlockId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DetectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsGhost = table.Column<bool>(type: "INTEGER", nullable: false),
                    InvolvedVehicleIds = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EscapeNodeUsed = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvingVehicleId = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadlockHistory", x => x.DeadlockId);
                });

            migrationBuilder.CreateTable(
                name: "FleetPerformanceHourly",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeriodStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MissionsCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                    MissionsFaulted = table.Column<int>(type: "INTEGER", nullable: false),
                    MissionsTransferred = table.Column<int>(type: "INTEGER", nullable: false),
                    MissionsSwapped = table.Column<int>(type: "INTEGER", nullable: false),
                    AvgMissionDurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    AvgTravelDurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    AvgWaitDurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    DeadlocksDetected = table.Column<int>(type: "INTEGER", nullable: false),
                    DeadlocksResolved = table.Column<int>(type: "INTEGER", nullable: false),
                    GhostDeadlocks = table.Column<int>(type: "INTEGER", nullable: false),
                    VehicleUtilizationPct = table.Column<double>(type: "REAL", nullable: false),
                    OrderStealingEvents = table.Column<int>(type: "INTEGER", nullable: false),
                    DetourEvents = table.Column<int>(type: "INTEGER", nullable: false),
                    RedirectEvents = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetPerformanceHourly", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ForensicCommandLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FlushId = table.Column<long>(type: "INTEGER", nullable: false),
                    VehicleId = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MessageType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ActionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    NodeSequence = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    MqttTopic = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    RawPayload = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForensicCommandLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ForensicFlush",
                columns: table => new
                {
                    FlushId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TriggerEvent = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PrimaryVehicleId = table.Column<int>(type: "INTEGER", nullable: false),
                    InvolvedVehicleIds = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FlushTimestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CommandLogCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusLogCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TrafficLogCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForensicFlush", x => x.FlushId);
                });

            migrationBuilder.CreateTable(
                name: "ForensicStatusLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FlushId = table.Column<long>(type: "INTEGER", nullable: false),
                    VehicleId = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastNodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    X = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                    Y = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                    HeadingDegrees = table.Column<decimal>(type: "decimal(8,4)", nullable: true),
                    BatterySOC = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    OperatingMode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ActiveOrderId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ActionStates = table.Column<string>(type: "TEXT", nullable: true),
                    Errors = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForensicStatusLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ForensicTrafficLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FlushId = table.Column<long>(type: "INTEGER", nullable: false),
                    VehicleId = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ItineraryMoveId = table.Column<int>(type: "INTEGER", nullable: true),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ResourcesChecked = table.Column<string>(type: "TEXT", nullable: true),
                    CheckResults = table.Column<string>(type: "TEXT", nullable: true),
                    ResourcesLocked = table.Column<string>(type: "TEXT", nullable: true),
                    ContentionWithVehicleId = table.Column<int>(type: "INTEGER", nullable: true),
                    DetourEvaluated = table.Column<bool>(type: "INTEGER", nullable: false),
                    DetourTaken = table.Column<bool>(type: "INTEGER", nullable: false),
                    PhaseOutcome = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForensicTrafficLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Location",
                columns: table => new
                {
                    LocationRecordId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LocationId = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveFromLocationVersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    LocationName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Location", x => x.LocationRecordId);
                });

            migrationBuilder.CreateTable(
                name: "LocationAssignment",
                columns: table => new
                {
                    AssignmentRecordId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssignmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveFromLocationVersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    LocationId = table.Column<int>(type: "INTEGER", nullable: false),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    OperationTypeId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocationTypeId = table.Column<int>(type: "INTEGER", nullable: false),
                    GuardedAssignmentId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationAssignment", x => x.AssignmentRecordId);
                });

            migrationBuilder.CreateTable(
                name: "LockContentionHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WaitingVehicleId = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockingVehicleId = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockedNodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentionStartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ContentionResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WaitDurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    ResolutionType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LockContentionHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Mission",
                columns: table => new
                {
                    MissionId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    AssignedVehicleId = table.Column<int>(type: "INTEGER", nullable: true),
                    PickupAssignmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    DropoffAssignmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    LoadType = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DispatchedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PickupArrivedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PickupCompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DropoffArrivedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mission", x => x.MissionId);
                });

            migrationBuilder.CreateTable(
                name: "MissionHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MissionId = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    MissionType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    VehicleId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceSystemRef = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PickupNodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    DropNodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    LoadIdentity = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    TransferCount = table.Column<int>(type: "INTEGER", nullable: false),
                    OutcomeStatus = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PickupArrivedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PickupCompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DropArrivedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DropCompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TotalDurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    TravelDurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    WaitDurationSeconds = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MissionHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MissionStateTransition",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MissionId = table.Column<int>(type: "INTEGER", nullable: false),
                    FromState = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ToState = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    VehicleId = table.Column<int>(type: "INTEGER", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MissionStateTransition", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Move",
                columns: table => new
                {
                    MoveRecordId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MoveId = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveFromVersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    FromNodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToNodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    RoutingTypeId = table.Column<int>(type: "INTEGER", nullable: false),
                    TravelDirection = table.Column<byte>(type: "INTEGER", nullable: false),
                    StartHeading = table.Column<decimal>(type: "decimal(8,4)", nullable: false),
                    EndHeading = table.Column<decimal>(type: "decimal(8,4)", nullable: false),
                    ParameterA = table.Column<decimal>(type: "decimal(12,6)", nullable: false),
                    ArcLength = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    DefaultSpeed = table.Column<decimal>(type: "decimal(6,4)", nullable: false),
                    MaxSpeed = table.Column<decimal>(type: "decimal(6,4)", nullable: false),
                    MaxWeightCapacityKg = table.Column<decimal>(type: "decimal(10,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Move", x => x.MoveRecordId);
                });

            migrationBuilder.CreateTable(
                name: "Node",
                columns: table => new
                {
                    NodeRecordId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveFromVersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    NodeName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    X = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    Y = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    Z = table.Column<decimal>(type: "decimal(10,2)", nullable: false, defaultValue: 0m),
                    NodeType = table.Column<byte>(type: "INTEGER", nullable: false),
                    MapId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Node", x => x.NodeRecordId);
                });

            migrationBuilder.CreateTable(
                name: "Vehicle",
                columns: table => new
                {
                    VehicleId = table.Column<int>(type: "INTEGER", nullable: false),
                    VehicleName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SerialNumber = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    VehicleType = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentZoneId = table.Column<int>(type: "INTEGER", nullable: true),
                    CurrentNodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    CurrentMapId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BatteryStateOfCharge = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    ActivityState = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderState = table.Column<int>(type: "INTEGER", nullable: false),
                    OperatingMode = table.Column<int>(type: "INTEGER", nullable: false),
                    IsLoaded = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    IsInService = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    IsOnline = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CurrentMissionId = table.Column<int>(type: "INTEGER", nullable: true),
                    LastStateReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicle", x => x.VehicleId);
                });

            migrationBuilder.CreateTable(
                name: "VehicleStatusHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VehicleId = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsHeartbeat = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastNodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    X = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                    Y = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                    HeadingDegrees = table.Column<decimal>(type: "decimal(8,4)", nullable: true),
                    ActivityState = table.Column<int>(type: "INTEGER", nullable: false),
                    OperatingMode = table.Column<int>(type: "INTEGER", nullable: false),
                    BatteryStateOfCharge = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    IsLoaded = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsOnline = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActiveMissionId = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorState = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleStatusHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VehicleStatusHistoryDaily",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VehicleId = table.Column<int>(type: "INTEGER", nullable: false),
                    PeriodDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MinutesDriving = table.Column<double>(type: "REAL", nullable: false),
                    MinutesIdle = table.Column<double>(type: "REAL", nullable: false),
                    MinutesCharging = table.Column<double>(type: "REAL", nullable: false),
                    MinutesWaitingOnLock = table.Column<double>(type: "REAL", nullable: false),
                    MinutesFaulted = table.Column<double>(type: "REAL", nullable: false),
                    MinutesOffline = table.Column<double>(type: "REAL", nullable: false),
                    AvgBatterySOC = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    MinBatterySOC = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    MissionsCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                    ChargeEventsOpportunity = table.Column<int>(type: "INTEGER", nullable: false),
                    ChargeEventsMandatory = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleStatusHistoryDaily", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VehicleStatusHistoryHourly",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VehicleId = table.Column<int>(type: "INTEGER", nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MinutesDriving = table.Column<double>(type: "REAL", nullable: false),
                    MinutesIdle = table.Column<double>(type: "REAL", nullable: false),
                    MinutesCharging = table.Column<double>(type: "REAL", nullable: false),
                    MinutesWaitingOnLock = table.Column<double>(type: "REAL", nullable: false),
                    MinutesFaulted = table.Column<double>(type: "REAL", nullable: false),
                    MinutesOffline = table.Column<double>(type: "REAL", nullable: false),
                    AvgBatterySOC = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    MinBatterySOC = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    MissionsCompleted = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleStatusHistoryHourly", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VehicleUtilizationHourly",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeriodStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VehicleId = table.Column<int>(type: "INTEGER", nullable: false),
                    MinutesDriving = table.Column<double>(type: "REAL", nullable: false),
                    MinutesIdle = table.Column<double>(type: "REAL", nullable: false),
                    MinutesCharging = table.Column<double>(type: "REAL", nullable: false),
                    MinutesWaitingOnLock = table.Column<double>(type: "REAL", nullable: false),
                    MinutesFaulted = table.Column<double>(type: "REAL", nullable: false),
                    MissionsCompleted = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleUtilizationHourly", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Zone",
                columns: table => new
                {
                    ZoneId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ZoneName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RequiredVehicleCount = table.Column<int>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Zone", x => x.ZoneId);
                });

            migrationBuilder.CreateTable(
                name: "VehicleFactSheet",
                columns: table => new
                {
                    VehicleId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProtocolVersion = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    MaxOrderHorizonDepth = table.Column<int>(type: "INTEGER", nullable: false),
                    SupportsNurbsTrajectory = table.Column<bool>(type: "INTEGER", nullable: false),
                    SupportedActionTypes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    MaxSpeedMs = table.Column<decimal>(type: "decimal(6,4)", nullable: false),
                    MaxPayloadKg = table.Column<decimal>(type: "decimal(8,2)", nullable: false),
                    LengthMeters = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    WidthMeters = table.Column<decimal>(type: "decimal(5,3)", nullable: false),
                    LastReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleFactSheet", x => x.VehicleId);
                    table.ForeignKey(
                        name: "FK_VehicleFactSheet_Vehicle_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicle",
                        principalColumn: "VehicleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Area_AreaId_Version",
                table: "Area",
                columns: new[] { "AreaId", "EffectiveFromVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChargeCycleHistory_VehicleId_Time",
                table: "ChargeCycleHistory",
                columns: new[] { "VehicleId", "ChargeStartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChargeCycleSOCTrace_CycleId_Time",
                table: "ChargeCycleSOCTrace",
                columns: new[] { "CycleId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_DeadlockHistory_DetectedAt",
                table: "DeadlockHistory",
                column: "DetectedAt");

            migrationBuilder.CreateIndex(
                name: "UX_FleetPerformanceHourly_Period",
                table: "FleetPerformanceHourly",
                column: "PeriodStart",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForensicCommandLog_Flush_Vehicle_Time",
                table: "ForensicCommandLog",
                columns: new[] { "FlushId", "VehicleId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ForensicFlush_Timestamp",
                table: "ForensicFlush",
                column: "FlushTimestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ForensicFlush_VehicleId",
                table: "ForensicFlush",
                column: "PrimaryVehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_ForensicStatusLog_Flush_Vehicle_Time",
                table: "ForensicStatusLog",
                columns: new[] { "FlushId", "VehicleId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ForensicTrafficLog_Flush_Vehicle_Time",
                table: "ForensicTrafficLog",
                columns: new[] { "FlushId", "VehicleId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "UX_Location_LocationId_Version",
                table: "Location",
                columns: new[] { "LocationId", "EffectiveFromLocationVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocationAssignment_LocationId",
                table: "LocationAssignment",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_LocationAssignment_NodeId",
                table: "LocationAssignment",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "UX_LocationAssignment_Id_Version",
                table: "LocationAssignment",
                columns: new[] { "AssignmentId", "EffectiveFromLocationVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LockContention_VehicleId_Time",
                table: "LockContentionHistory",
                columns: new[] { "WaitingVehicleId", "ContentionStartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Mission_OrderId",
                table: "Mission",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Mission_State",
                table: "Mission",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_Mission_VehicleId",
                table: "Mission",
                column: "AssignedVehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_MissionHistory_CreatedAt",
                table: "MissionHistory",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MissionHistory_MissionId",
                table: "MissionHistory",
                column: "MissionId");

            migrationBuilder.CreateIndex(
                name: "IX_MissionHistory_VehicleId",
                table: "MissionHistory",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_MissionStateTransition_MissionId_Time",
                table: "MissionStateTransition",
                columns: new[] { "MissionId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Move_FromTo",
                table: "Move",
                columns: new[] { "FromNodeId", "ToNodeId" });

            migrationBuilder.CreateIndex(
                name: "UX_Move_MoveId_Version",
                table: "Move",
                columns: new[] { "MoveId", "EffectiveFromVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Node_NodeId_Version",
                table: "Node",
                columns: new[] { "NodeId", "EffectiveFromVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Vehicle_SerialNumber",
                table: "Vehicle",
                column: "SerialNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleStatusHistory_VehicleId_Time",
                table: "VehicleStatusHistory",
                columns: new[] { "VehicleId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "UX_VehicleStatusDaily_VehicleId_Period",
                table: "VehicleStatusHistoryDaily",
                columns: new[] { "VehicleId", "PeriodDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_VehicleStatusHourly_VehicleId_Period",
                table: "VehicleStatusHistoryHourly",
                columns: new[] { "VehicleId", "PeriodStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_VehicleUtilizationHourly_VehicleId_Period",
                table: "VehicleUtilizationHourly",
                columns: new[] { "VehicleId", "PeriodStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Area");

            migrationBuilder.DropTable(
                name: "ChargeCycleHistory");

            migrationBuilder.DropTable(
                name: "ChargeCycleSOCTrace");

            migrationBuilder.DropTable(
                name: "DeadlockHistory");

            migrationBuilder.DropTable(
                name: "FleetPerformanceHourly");

            migrationBuilder.DropTable(
                name: "ForensicCommandLog");

            migrationBuilder.DropTable(
                name: "ForensicFlush");

            migrationBuilder.DropTable(
                name: "ForensicStatusLog");

            migrationBuilder.DropTable(
                name: "ForensicTrafficLog");

            migrationBuilder.DropTable(
                name: "Location");

            migrationBuilder.DropTable(
                name: "LocationAssignment");

            migrationBuilder.DropTable(
                name: "LockContentionHistory");

            migrationBuilder.DropTable(
                name: "Mission");

            migrationBuilder.DropTable(
                name: "MissionHistory");

            migrationBuilder.DropTable(
                name: "MissionStateTransition");

            migrationBuilder.DropTable(
                name: "Move");

            migrationBuilder.DropTable(
                name: "Node");

            migrationBuilder.DropTable(
                name: "VehicleFactSheet");

            migrationBuilder.DropTable(
                name: "VehicleStatusHistory");

            migrationBuilder.DropTable(
                name: "VehicleStatusHistoryDaily");

            migrationBuilder.DropTable(
                name: "VehicleStatusHistoryHourly");

            migrationBuilder.DropTable(
                name: "VehicleUtilizationHourly");

            migrationBuilder.DropTable(
                name: "Zone");

            migrationBuilder.DropTable(
                name: "Vehicle");
        }
    }
}
