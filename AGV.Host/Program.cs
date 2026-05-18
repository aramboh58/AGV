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

    var builder = Host.CreateApplicationBuilder(args);

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
    var host = builder.Build();

    // Apply database migrations at startup
    await DatabaseProviderRegistration
        .ApplyMigrationsAsync(host.Services);

    Log.Information("AGV Host Control System started successfully");

    // Seed topology if empty
    using (var scope = host.Services.CreateScope())
    {
        var seedService = new AGV.Persistence.Services.TopologySeedService(
            scope.ServiceProvider
                 .GetRequiredService<AGV.Persistence.Data.AgvDbContext>(),
            scope.ServiceProvider
                 .GetRequiredService<ILoggerFactory>()
                 .CreateLogger<AGV.Persistence.Services.TopologySeedService>());

        var jsonPath = Path.Combine(
            AppContext.BaseDirectory, "nyt_agv_roadmap.json");

        await seedService.SeedIfEmptyAsync(jsonPath);
    }

    // Load vehicles from database into VehicleRegistry
    using (var scope = host.Services.CreateScope())
    {
        var db = scope.ServiceProvider
            .GetRequiredService<AGV.Persistence.Data.AgvDbContext>();
        var registry = host.Services
            .GetRequiredService<AGV.Fleet.Infrastructure.VehicleRegistry>();

        var vehicles = await db.Set<AGV.Core.Entities.Vehicle>()
            .ToListAsync();

        registry.RegisterAll(vehicles);

        Log.Information("VehicleRegistry loaded: {Count} vehicles",
            vehicles.Count);
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "AGV Host Control System terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
