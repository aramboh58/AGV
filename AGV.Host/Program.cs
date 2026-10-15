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
using AGV.Dashboard.Options;
using AGV.Core.Enums;
using System.Runtime.InteropServices;

// ----------------------------------------------------------------
// Disable console QuickEdit Mode (9/20 diagnostic session). Root
// cause of the multi-session click-delay mystery, confirmed live via
// Visual Studio's debugger (Parallel Stacks / Threads windows):
// clicking/selecting text in the console window suspends ALL console
// output (a Win32 GetBufferInfo call blocks) until the selection is
// released. If that happens while a thread is inside an application
// lock that logs before releasing it, every other thread needing
// that same lock piles up behind it — explaining the near-zero-CPU,
// widely-variable-duration, self-resolving contention chased across
// many sessions (see ToDo18/19.md #11). The actual application locks
// were separately fixed (logging moved outside every lock block in
// MissionQueueService.cs, ChargeQueueManagerService.cs, and
// TrafficManagerService.cs) — that is the real, permanent fix. This
// is a belt-and-suspenders companion: disabling QuickEdit Mode in
// code means the trigger itself can never occur again, regardless of
// anyone's manual Windows Terminal/console settings. Safe to call on
// any platform — silently does nothing if there's no console (e.g.
// running as a Windows Service) or the API isn't available.
// ----------------------------------------------------------------
TryDisableConsoleQuickEdit();

// ----------------------------------------------------------------
// Bootstrap Serilog early so startup errors are captured
// ----------------------------------------------------------------
Log.Logger = new LoggerConfiguration()
    .WriteTo.Async(a =>
    {
        a.Console();
        a.File(
            "logs/server_log-.txt",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: null);
    })
    .CreateBootstrapLogger();

try
{
    Log.Information("AGV Host Control System starting...");

    var builder = WebApplication.CreateBuilder(args);

    // ----------------------------------------------------------------
    // Remove ASP.NET Core's default logging providers (9/3 diagnostic
    // session). WebApplication.CreateBuilder wires up its OWN Console
    // logging provider by default, entirely separate from Serilog —
    // it was still writing directly and synchronously to Console.Out,
    // bypassing the Async wrapper added around Serilog's Console sink
    // above. This is very likely why total contention did not drop
    // after that fix: PerfView's CallTree confirmed Serilog's own
    // Console writes now correctly route through
    // serilog.sinks.async's background thread (that path's share of
    // total contention fell from ~82.6% to ~19%), but total
    // contention volume still rose, consistent with a second, still-
    // unwrapped Console writer being the new dominant contributor.
    // See ToDo10.md #11.
    // ----------------------------------------------------------------
    builder.Logging.ClearProviders();

    // ----------------------------------------------------------------
    // Serilog
    // Console sink wrapped in Async (9/3): PerfView contention trace
    // showed 82.6% of all lock contention traced to Console.Out's
    // internal SyncTextWriter lock, contended across many concurrent
    // background loops and per-vehicle logging. See ToDo10.md #11.
    //
    // File sink added (9/10): was console-only until now, meaning
    // "server_log.txt" was actually just manually-redirected/copied
    // console output with no real retention policy — a terminal's
    // scrollback buffer limit could silently lose exactly the moment
    // we need most (e.g. the frozen-rendering bug from ToDo15.md #11,
    // which may take over an hour to recur). Writing directly to a
    // real file, rolling daily, with unlimited retained files (nothing
    // auto-deleted), removes that risk. Also wrapped in the same
    // Async block — Serilog.Sinks.File was already an installed
    // package but never wired up.
    //
    // CHANGED (9/23 diagnostic session): builder.Services.AddSerilog
    // was replaced with builder.Host.UseSerilog. AddSerilog(Action<...>)
    // builds a SEPARATE Serilog.Core.Logger for dependency injection
    // and does NOT touch the static Log.Logger property — meaning the
    // bootstrap logger created above (used by every static Log.X call
    // still made later in this file) remained active as a SECOND,
    // independent logging pipeline, with its OWN dedicated async
    // background thread, writing to the same console with no
    // coordination between the two pipelines. Confirmed live via
    // Visual Studio's Threads window: two different threads
    // simultaneously inside Serilog.Sinks.Console code at once (one
    // in ConsoleSink.Emit, another in SystemConsoleTheme.Set) — only
    // possible with two independent async dispatcher threads. Per
    // Serilog's own documented two-phase-bootstrap pattern,
    // Host.UseSerilog automatically replaces Log.Logger with the
    // fully-configured instance, collapsing this back down to exactly
    // one pipeline, one dedicated thread, shared by every static call
    // and every injected ILogger<T> alike. See ToDo18/19.md #11.
    // ----------------------------------------------------------------
    builder.Host.UseSerilog((context, services, config) => config
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Async(a =>
        {
            a.Console();
            a.File(
                "logs/server_log-.txt",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null); // never auto-delete old days
        }));

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

    builder.Services.Configure<DashboardOptions>(
        builder.Configuration.GetSection(DashboardOptions.SectionName));

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
    builder.Services.AddSingleton<ActiveMissionRegistry>();
    builder.Services.AddSingleton<AGV.Topology.Services.LocationCatalog>();
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
    // RoadMapGraphHolder � wired to topology version change event
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

    // Sliding lock window (drip feed). Traffic:LockWindowNodes in
    // appsettings.json, default 4; 0 disables windowing.
    builder.Services.AddSingleton(
        builder.Configuration.GetSection(TrafficOptions.SectionName)
            .Get<TrafficOptions>() ?? new TrafficOptions());
    builder.Services.AddSingleton<RouteWindowManager>();

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
    builder.Services.AddSignalR()
        .AddJsonProtocol(options =>
        {
            options.PayloadSerializerOptions.Converters
                .Add(new System.Text.Json.Serialization
                    .JsonStringEnumConverter());
        });
    // ----------------------------------------------------------------
    // Dashboard broadcaster
    // ----------------------------------------------------------------
    builder.Services.AddSingleton<AGV.Dashboard.Services.DashboardBroadcaster>();
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<AGV.Dashboard.Services.DashboardBroadcaster>());

    // ----------------------------------------------------------------
    // Circuit-level cleanup (9/10 diagnostic session) — MUST be Scoped,
    // not Singleton. Blazor Server creates one DI scope per circuit, so
    // a Scoped registration here means each circuit's Dashboard
    // component and its DashboardCircuitHandler automatically share the
    // same CircuitCleanupRegistry instance. This gives a reliable
    // cleanup hook (fires on OnConnectionDownAsync, i.e. promptly on
    // ANY disconnect including an abrupt hard refresh) as an
    // alternative to Dashboard.razor's DisposeAsync, which was
    // confirmed via instance-ID logging to NOT fire reliably on hard
    // refresh — see ToDo14.md #11 for the full diagnostic history.
    // ----------------------------------------------------------------
    builder.Services.AddScoped<AGV.Dashboard.Services.CircuitCleanupRegistry>();
    builder.Services.AddScoped<
        Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler,
        AGV.Dashboard.Services.DashboardCircuitHandler>();

    // ----------------------------------------------------------------
    // Vehicle interface � Simulation or Mqtt
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

    // ----------------------------------------------------------------
    // Shutdown markers (10/11). Every run's log used to end abruptly
    // with no trace of why. A graceful stop (Ctrl+C / SIGTERM / console
    // close) now writes these lines and then flushes via the finally
    // block below. A hard kill (VS Stop Debugging, Task Manager,
    // Stop-Process) still cannot log or flush - if a log ends with no
    // "stopping" line, the process was killed rather than stopped.
    // ----------------------------------------------------------------
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
        Log.Information("AGV Host stopping (graceful shutdown requested)"));
    lifetime.ApplicationStopped.Register(() =>
        Log.Information("AGV Host stopped"));

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

    // Wire press demand � load press stand node IDs and subscribe RollDemanded
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

        //await AGV.Tools.ImportSiteTopology.ImportAsync(
        //    db,
        //    @"C:\Dev\AGV\NYT_Nodes_LeftJoin_Location.txt",
        //    @"C:\Dev\AGV\NYT_Moves.txt");

        //await AGV.Tools.ExportRoadmapJson.ExportAsync(db,
        //    @"C:\Dev\AGV\AGV.Host\wwwroot\nyt_agv_roadmap.json");

        var locationCatalog = app.Services
            .GetRequiredService<AGV.Topology.Services.LocationCatalog>();
        locationCatalog.Load(
            await db.Set<AGV.Core.Entities.Location>().AsNoTracking().ToListAsync(),
            await db.Set<AGV.Core.Entities.LocationAssignment>().AsNoTracking().ToListAsync());
        Console.WriteLine(
            $"Location catalog: {locationCatalog.LocationCount} locations, " +
            $"{locationCatalog.AssignmentCount} assignments.");

        // Load press stand drop node IDs (LPS*A and UPS*A nodes)
        var pressStandNodeIds = await db.Set<AGV.Core.Entities.Node>()
            .Where(n => n.NodeId >= 5000 && n.NodeId <= 6999)
            .Select(n => n.NodeId)
            .ToListAsync();
        simOptions.PressStandNodeIds = pressStandNodeIds;

        // Load staging pickup assignment IDs keyed by node name
        var stagingAssignmentIds = await db.Set<AGV.Core.Entities.Node>()
            .Where(n => n.NodeId >= 7300 && n.NodeId <= 7630
                     && n.NodeType == NodeType.DestinationOnly)
            .Select(n => n.NodeId)
            .ToListAsync();

        // Build press stand drop assignment lookup: NodeId -> AssignmentId
        var dropAssignments = await db.Set<AGV.Core.Entities.Node>()
            .Where(n => n.NodeId >= 5000 && n.NodeId <= 6999)
            .ToDictionaryAsync(n => n.NodeId, n => n.NodeId);
        int missionCounter = 0;

        // Subscribe to RollDemanded
        simEngine.RollDemanded += async (sender, args) =>
        {
            try
            {
                if (!dropAssignments.TryGetValue(
                    args.PressStandNodeId, out var dropNodeId))
                {
                    Log.Warning(
                        "RollDemanded: no assignment found for node {NodeId}",
                        args.PressStandNodeId);
                    return;
                }
                var pickupNodeId = stagingAssignmentIds[
                    Math.Abs(Interlocked.Increment(ref missionCounter))
                    % stagingAssignmentIds.Count];
                var context = new AGV.Core.Messages.MissionContext
                {
                    MissionId = 0,
                    CurrentOrderId = $"ORD{Guid.NewGuid():N}"[..12]
                        .ToUpperInvariant(),
                    PickupNodeId = pickupNodeId,
                    DropNodeId = dropNodeId,
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

// ----------------------------------------------------------------
// Local function: disable console QuickEdit Mode. See the comment
// block near the top of this file for the full rationale (9/20
// diagnostic session).
// ----------------------------------------------------------------
static void TryDisableConsoleQuickEdit()
{
    try
    {
        const int STD_INPUT_HANDLE = -10;
        const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
        const uint ENABLE_EXTENDED_FLAGS = 0x0080;

        var consoleHandle = GetStdHandle(STD_INPUT_HANDLE);
        if (consoleHandle == IntPtr.Zero
            || consoleHandle == new IntPtr(-1))
        {
            return; // no console attached (e.g. running as a Windows Service)
        }

        if (!GetConsoleMode(consoleHandle, out var mode))
            return;

        // Clear QuickEdit, but keep ENABLE_EXTENDED_FLAGS set — it
        // must be set for the mode change to take effect at all, and
        // clearing it would also disable other extended console
        // behaviors we don't want to touch.
        mode &= ~ENABLE_QUICK_EDIT_MODE;
        mode |= ENABLE_EXTENDED_FLAGS;

        SetConsoleMode(consoleHandle, mode);
    }
    catch
    {
        // Best-effort only — never let a console-mode tweak prevent
        // the application from starting.
    }
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr GetStdHandle(int nStdHandle);

[DllImport("kernel32.dll")]
static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

[DllImport("kernel32.dll")]
static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
