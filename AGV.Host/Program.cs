using AGV.Core.Interfaces;
using AGV.Fleet.Infrastructure;
using AGV.Fleet.Services;
using AGV.Host;
using AGV.Mqtt.Services;
using AGV.Persistence.Data;
using AGV.Routing.Services;
using AGV.Simulation.Services;
using AGV.Topology.Services;
using AGV.Vehicle.Services;
using AGV.Dashboard.Hubs;
using AGV.Dashboard.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

// ----------------------------------------------------------------
// Bootstrap Serilog early so startup errors are captured
// ----------------------------------------------------------------
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("AGV Host Control System starting...");

    var builder = WebApplication.CreateBuilder(args);

    // ----------------------------------------------------------------
    // Serilog
    // ----------------------------------------------------------------
    builder.Services.AddSerilog((services, config) => config
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    // ----------------------------------------------------------------
    // Configuration binding
    // ----------------------------------------------------------------
    builder.Services.Configure<MqttOptions>(
        builder.Configuration.GetSection(MqttOptions.SectionName));

    builder.Services.Configure<TopologyOptions>(
        builder.Configuration.GetSection(TopologyOptions.SectionName));
    
    builder.Services.AddSingleton<TopologyOptions>(sp =>
        sp.GetRequiredService<IOptions<TopologyOptions>>().Value);

    builder.Services.Configure<SimulationOptions>(
        builder.Configuration.GetSection(SimulationOptions.SectionName));

    builder.Services.AddSingleton<BatteryModelOptions>(sp =>
        sp.GetRequiredService<IOptions<BatteryModelOptions>>().Value);

    builder.Services.AddSingleton<SimulationOptions>(sp =>
        sp.GetRequiredService<IOptions<SimulationOptions>>().Value);

    builder.Services.Configure<TurnCostOptions>(
        builder.Configuration.GetSection(TurnCostOptions.SectionName));

    builder.Services.Configure<BatteryModelOptions>(
        builder.Configuration.GetSection(BatteryModelOptions.SectionName));

    builder.Services.AddOptions<AGV.Core.Interfaces.ChargingThresholds>()
        .Bind(builder.Configuration.GetSection("ChargingThresholds"));

    // ----------------------------------------------------------------
    // Database
    // ----------------------------------------------------------------
    builder.Services.AddAgvDatabase(builder.Configuration);

    // ----------------------------------------------------------------
    // Core singletons
    // ----------------------------------------------------------------
    builder.Services.AddSingleton<ChannelRegistry>();
    builder.Services.AddSingleton<VehicleRegistry>();
    builder.Services.AddSingleton<CheckTableCache>();
    builder.Services.AddSingleton<RuntimeBlockingState>();
    builder.Services.AddSingleton<TopologyVersionManager>();

    // ----------------------------------------------------------------
    // Topology
    // ----------------------------------------------------------------
    builder.Services.AddSingleton<TopologyService>(sp =>
    {
        var connString = builder.Configuration
            .GetConnectionString("AgvDatabase")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:AgvDatabase is required.");
        var provider = builder.Configuration
            .GetValue<string>("DatabaseProvider") ?? "Sqlite";

        return new TopologyService(
            connString,
            provider,
            sp.GetRequiredService<TopologyVersionManager>(),
            sp.GetRequiredService<RuntimeBlockingState>());
    });
    builder.Services.AddHostedService<TopologyBackgroundService>();

    // ----------------------------------------------------------------
    // Routing
    // ----------------------------------------------------------------
    builder.Services.AddSingleton<TurnCostTable>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<TurnCostOptions>>();
        return new TurnCostTable(opts.Value);
    });
    builder.Services.AddSingleton<AStarRoutingEngine>();
    builder.Services.AddSingleton<IRoutingEngine>(sp =>
        sp.GetRequiredService<AStarRoutingEngine>());
    builder.Services.AddHostedService<RoutingBackgroundService>();

    // ----------------------------------------------------------------
    // Vehicle
    // ----------------------------------------------------------------
    // RoadMapGraphHolder — wired to topology version change event
    builder.Services.AddSingleton<RoadMapGraphHolder>(sp =>
    {
        var holder = new RoadMapGraphHolder();
        var manager = sp.GetRequiredService<TopologyVersionManager>();
        manager.TopologyVersionChanged += (_, args) =>
            holder.Update(args.Graph);
        return holder;
    });

    builder.Services.AddSingleton<OrderBuilder>(sp =>
        new OrderBuilder(
            sp.GetRequiredService<RoadMapGraphHolder>()));

    // ----------------------------------------------------------------
    // Fleet
    // ----------------------------------------------------------------
    builder.Services.AddSingleton<MissionQueueService>();

    builder.Services.AddSingleton<ChargingThresholds>(sp =>
    {
        var opts = sp.GetRequiredService
            <IOptions<ChargingThresholds>>();
        return opts.Value;
    });

    builder.Services.AddSingleton<ChargeQueueManagerService>();
    builder.Services.AddSingleton<IChargeQueueManager>(sp =>
        sp.GetRequiredService<ChargeQueueManagerService>());
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<ChargeQueueManagerService>());

    builder.Services.AddSingleton<TrafficManagerService>();
    builder.Services.AddSingleton<ITrafficManager>(sp =>
        sp.GetRequiredService<TrafficManagerService>());
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<TrafficManagerService>());

    builder.Services.AddSingleton<FleetManagerService>();
    builder.Services.AddSingleton<IFleetManager>(sp =>
        sp.GetRequiredService<FleetManagerService>());
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<FleetManagerService>());

    // ----------------------------------------------------------------
    // Customization + External System
    // ----------------------------------------------------------------
    builder.Services.AddSingleton<ICustomizationApi,
        DefaultCustomizationApi>();
    builder.Services.AddSingleton<IExternalSystemAdapter,
        NullExternalSystemAdapter>();

    // ----------------------------------------------------------------
    // ASP.NET Core / SignalR / Blazor
    // ----------------------------------------------------------------
    builder.Services.AddRazorPages();
    builder.Services.AddServerSideBlazor();
    builder.Services.AddSignalR();

    // ----------------------------------------------------------------
    // Dashboard broadcaster
    // ----------------------------------------------------------------
    builder.Services.AddSingleton<AGV.Dashboard.Services.DashboardBroadcaster>();
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<AGV.Dashboard.Services.DashboardBroadcaster>());

    // ----------------------------------------------------------------
    // Vehicle interface — Simulation or Mqtt
    // ----------------------------------------------------------------
    var vehicleInterface = builder.Configuration
        .GetValue<string>("VehicleInterface") ?? "Simulation";

    if (vehicleInterface.Equals("Mqtt",
        StringComparison.OrdinalIgnoreCase))
    {
        var mqttOpts = builder.Configuration
            .GetSection(MqttOptions.SectionName)
            .Get<MqttOptions>() ?? new MqttOptions();

        var topicRouter = new Vda5050TopicRouter(
            mqttOpts.InterfaceName,
            mqttOpts.MajorVersion,
            mqttOpts.Manufacturer,
            null!);  // loggerFactory injected at runtime

        builder.Services.AddSingleton(topicRouter);
        builder.Services.AddSingleton<ConnectionStateTracker>();
        builder.Services.AddSingleton<MqttListenerService>();
        builder.Services.AddSingleton<MqttPublisherService>();
        builder.Services.AddSingleton<MqttVehicleAdapter>();
        builder.Services.AddSingleton<IVehicleAdapter>(sp =>
            sp.GetRequiredService<MqttVehicleAdapter>());
        builder.Services.AddHostedService(sp =>
            sp.GetRequiredService<MqttListenerService>());
        builder.Services.AddHostedService(sp =>
            sp.GetRequiredService<MqttPublisherService>());

        Log.Information("Vehicle interface: MQTT/VDA 5050");
    }
    else
    {
        builder.Services.AddSingleton<SimulatedVehicleAdapter>();
        builder.Services.AddSingleton<IVehicleAdapter>(sp =>
            sp.GetRequiredService<SimulatedVehicleAdapter>());

        builder.Services.AddSingleton<SimulationEngineService>();
        builder.Services.AddHostedService(sp =>
            sp.GetRequiredService<SimulationEngineService>());

        Log.Information(
            "Vehicle interface: Simulation (SpeedFactor={Factor}x)",
            builder.Configuration
                .GetValue<decimal>("Simulation:SpeedFactor", 60m));
    }

    // ----------------------------------------------------------------
    // Windows Service support
    // ----------------------------------------------------------------
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "AGV Host Control System";
    });

    // ----------------------------------------------------------------
    // Build and run
    // ----------------------------------------------------------------
    var app = builder.Build();

    app.UseStaticFiles();
    app.UseRouting();
    app.MapBlazorHub();
    app.MapHub<AGV.Dashboard.Hubs.FleetHub>("/fleethub");
    app.MapFallbackToPage("/_Host");

    // Apply database migrations at startup
    await DatabaseProviderRegistration
        .ApplyMigrationsAsync(app.Services);

    Log.Information("AGV Host Control System started successfully");

    // Seed topology if empty
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider
            .GetRequiredService<AGV.Persistence.Data.AgvDbContext>();

        var initialSoc = builder.Configuration
            .GetSection("Simulation:VehicleInitialSoc")
            .Get<Dictionary<string, decimal>>()
            ?? new Dictionary<string, decimal>();

        var seedService = new AGV.Persistence.Services.TopologySeedService(
            db,
            scope.ServiceProvider
                 .GetRequiredService<ILoggerFactory>()
                 .CreateLogger<AGV.Persistence.Services.TopologySeedService>(),
            initialSoc);

        var jsonPath = Path.Combine(
            AppContext.BaseDirectory, "nyt_agv_roadmap.json");

        await seedService.SeedIfEmptyAsync(jsonPath);
    }

    // Load vehicles from database into VehicleRegistry
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider
            .GetRequiredService<AGV.Persistence.Data.AgvDbContext>();
        var registry = app.Services
            .GetRequiredService<AGV.Fleet.Infrastructure.VehicleRegistry>();

        var vehicles = await db.Set<AGV.Core.Entities.Vehicle>()
            .ToListAsync();

        registry.RegisterAll(vehicles);

        Log.Information("VehicleRegistry loaded: {Count} vehicles",
            vehicles.Count);
    }

    // Initialize charge slots from database node names
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider
            .GetRequiredService<AGV.Persistence.Data.AgvDbContext>();
        var chargeManager = app.Services
            .GetRequiredService<AGV.Fleet.Services.ChargeQueueManagerService>();

        var allNodes = await db.Set<AGV.Core.Entities.Node>()
            .ToListAsync();

        var opportunityNodeIds = allNodes
            .Where(n => n.NodeName != null && (
                n.NodeName.StartsWith("LC") ||
                n.NodeName.StartsWith("UC")))
            .Select(n => n.NodeId)
            .ToList();

        var mandatoryNodeIds = allNodes
            .Where(n => n.NodeName != null &&
                n.NodeName.StartsWith("MC"))
            .Select(n => n.NodeId)
            .ToList();

        chargeManager.InitializeOpportunitySlots(opportunityNodeIds);
        chargeManager.InitializeMandatoryStations(mandatoryNodeIds);

        Log.Information(
            "Charge slots initialized: {Opp} opportunity, {Mand} mandatory",
            opportunityNodeIds.Count, mandatoryNodeIds.Count);
    }

    // Wire press demand — load press stand node IDs and subscribe RollDemanded
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider
            .GetRequiredService<AGV.Persistence.Data.AgvDbContext>();
        var simOptions = app.Services
            .GetRequiredService<SimulationOptions>();
        var simEngine = app.Services
            .GetRequiredService<SimulationEngineService>();
        var fleetManager = app.Services
            .GetRequiredService<IFleetManager>();

    //    await AGV.Tools.ExportRoadmapJson.ExportAsync(db,
    //@"C:\Dev\AGV\AGV.Host\wwwroot\nyt_agv_roadmap.json");

        // Load press stand drop node IDs (LPS*A and UPS*A nodes)
        var pressStandNodeIds = await db.Set<AGV.Core.Entities.Node>()
            .Where(n => n.NodeName != null && (
                (n.NodeName.StartsWith("LPS") && n.NodeName.EndsWith("A")) ||
                (n.NodeName.StartsWith("UPS") && n.NodeName.EndsWith("A"))))
            .Select(n => n.NodeId)
            .ToListAsync();

        simOptions.PressStandNodeIds = pressStandNodeIds;

        // Load staging pickup assignment IDs keyed by node name
        var stagingAssignments = await db.Set<AGV.Core.Entities.LocationAssignment>()
            .Join(db.Set<AGV.Core.Entities.Node>(),
                a => a.NodeId,
                n => n.NodeId,
                (a, n) => new { a.AssignmentId, n.NodeName })
            .Where(x => x.NodeName != null &&
                x.NodeName.StartsWith("STG"))
            .ToListAsync();

        // Build press stand drop assignment lookup: NodeId -> AssignmentId
        var dropAssignments = await db.Set<AGV.Core.Entities.LocationAssignment>()
            .Join(db.Set<AGV.Core.Entities.Node>(),
                a => a.NodeId,
                n => n.NodeId,
                (a, n) => new { a.AssignmentId, a.NodeId, n.NodeName })
            .Where(x => x.NodeName != null && (
                (x.NodeName.StartsWith("LPS") && x.NodeName.EndsWith("A")) ||
                (x.NodeName.StartsWith("UPS") && x.NodeName.EndsWith("A"))))
            .ToDictionaryAsync(x => x.NodeId, x => x.AssignmentId);

        // Pick a random staging assignment for pickup
        var stagingAssignmentIds = stagingAssignments
            .Select(x => x.AssignmentId)
            .ToList();

        int missionCounter = 0;

        // Subscribe to RollDemanded
        simEngine.RollDemanded += async (sender, args) =>
        {
            try
            {
                if (!dropAssignments.TryGetValue(
                    args.PressStandNodeId, out var dropAssignmentId))
                {
                    Log.Warning(
                        "RollDemanded: no assignment found for node {NodeId}",
                        args.PressStandNodeId);
                    return;
                }

                var pickupAssignmentId = stagingAssignmentIds[
                    Math.Abs(Interlocked.Increment(ref missionCounter))
                    % stagingAssignmentIds.Count];

                var context = new AGV.Core.Messages.MissionContext
                {
                    MissionId = 0,
                    CurrentOrderId = $"ORD{Guid.NewGuid():N}"[..12]
                        .ToUpperInvariant(),
                    PickupNodeId = pickupAssignmentId,
                    DropNodeId = dropAssignmentId,
                    Priority = AGV.Core.Enums.MissionPriority.Normal,
                    CreatedAt = DateTime.UtcNow,
                };

                await fleetManager.EnqueueMissionAsync(context);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "RollDemanded handler failed");
            }
        };

        Log.Information(
            "Press demand wired: {Count} press stands, {Staging} staging positions",
            pressStandNodeIds.Count, stagingAssignmentIds.Count);
    }

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "AGV Host Control System terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
