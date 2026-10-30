using System.Collections.Concurrent;
using System.Text.Json;

using AGV.Core.Entities;
using AGV.Core.Enums;
using AGV.Core.Interfaces;
using AGV.Fleet.Infrastructure;
using AGV.Fleet.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AGV.Dashboard.Services
{
    public sealed record TestVehicleDef(
        int VehicleId, List<int> Route, bool ExpectFinish = true);

    public sealed record TestScenarioDef(
        string Name,
        string Description,
        int ExpectedResolved,
        int ExpectedUnresolved,
        int TimeoutSeconds,
        decimal SpeedFactor,
        List<TestVehicleDef> Vehicles);

    public sealed class TestRowDto
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public int VehicleCount { get; init; }
        public string Expect { get; init; } = "";
        public string Status { get; set; } = "PENDING";   // PENDING/RUNNING/PASS/FAIL
        public string Detail { get; set; } = "";
        public double Seconds { get; set; }
    }

    public sealed class TestRunDto
    {
        public bool Running { get; init; }
        public string Message { get; init; } = "";
        public List<TestRowDto> Rows { get; init; } = new();
    }

    /// <summary>
    /// Dev-only deadlock test harness. Loads scenario JSON files, places
    /// vehicles on their start nodes, drives them with real route
    /// windows and checks the outcome. Guarded by Testing:Enabled and
    /// refuses to run while press demand or any mission is active.
    /// </summary>
    public sealed class DeadlockTestService
    {
        private readonly IServiceProvider _sp;
        private readonly VehicleRegistry _registry;
        private readonly RouteWindowManager _windows;
        private readonly ITrafficManager _traffic;
        private readonly IFleetManager _fleet;
        private readonly IHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly ILogger<DeadlockTestService> _logger;

        private readonly object _lock = new();
        private List<TestRowDto> _rows = new();
        private string _message = "";
        private CancellationTokenSource? _cts;
        private volatile bool _running;

        // After a FAIL the vehicles are left exactly as they ended so the
        // popups (route, locks, waiting on) can be inspected. They are
        // cleaned up by RESET or at the start of the next scenario.
        private List<(int Id, Vehicle V, int Home)> _leftover = new();

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public DeadlockTestService(
            IServiceProvider sp,
            VehicleRegistry registry,
            RouteWindowManager windows,
            ITrafficManager traffic,
            IFleetManager fleet,
            IHostEnvironment env,
            IConfiguration config,
            ILogger<DeadlockTestService> logger)
        {
            _sp = sp;
            _registry = registry;
            _windows = windows;
            _traffic = traffic;
            _fleet = fleet;
            _env = env;
            _config = config;
            _logger = logger;
        }

        public bool Enabled => _config.GetValue<bool>("Testing:Enabled");
        public bool IsRunning => _running;

        private ISimulationTestHooks? Hooks
            => _sp.GetService(typeof(ISimulationTestHooks)) as ISimulationTestHooks;
        private ISimulationControl? Control
            => _sp.GetService(typeof(ISimulationControl)) as ISimulationControl;

        /// <summary>Null when it is safe to run, else the reason.</summary>
        public string? Blocker()
        {
            if (!Enabled) return "Testing is disabled (set Testing:Enabled = true).";
            if (Hooks is null || Control is null)
                return "Tests need the simulated vehicle adapter (not available in MQTT mode).";
            if (Control.PressDemandEnabled)
                return "Press demand is ON. Set Simulation:EnablePressDemand = false and restart.";
            if (_fleet.PendingMissionCount > 0)
                return $"{_fleet.PendingMissionCount} mission(s) are queued. Clear the queue first.";
            if (_registry.GetAll().Any(v => v.CurrentMissionId is not null))
                return "Missions are active. Wait for them to finish first.";
            return null;
        }

        public List<TestScenarioDef> ListScenarios()
        {
            var list = new List<TestScenarioDef>();
            var dir = Path.Combine(_env.ContentRootPath, "TestScenarios");
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x))
            {
                try
                {
                    var def = JsonSerializer.Deserialize<TestScenarioDef>(
                        File.ReadAllText(f), Json);
                    if (def is not null && def.Vehicles.Count > 0) list.Add(def);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Bad test scenario file {File}", f);
                }
            }
            return list;
        }

        public TestRunDto GetRun()
        {
            lock (_lock)
            {
                if (_rows.Count == 0 && !_running)
                {
                    _rows = ListScenarios().Select(NewRow).ToList();
                }
                return new TestRunDto
                {
                    Running = _running,
                    Message = _message,
                    Rows = _rows.Select(r => new TestRowDto
                    {
                        Name = r.Name, Description = r.Description,
                        VehicleCount = r.VehicleCount, Expect = r.Expect,
                        Status = r.Status, Detail = r.Detail, Seconds = r.Seconds,
                    }).ToList(),
                };
            }
        }

        public string Run(string? name)
        {
            if (_running) return "A test run is already in progress.";
            var why = Blocker();
            if (why is not null) return why;

            var all = ListScenarios();
            var todo = string.IsNullOrEmpty(name)
                ? all : all.Where(s => s.Name == name).ToList();
            if (todo.Count == 0) return "No matching scenario.";

            lock (_lock)
            {
                _rows = all.Select(NewRow).ToList();
                _message = "";
            }
            _cts = new CancellationTokenSource();
            _running = true;
            _ = Task.Run(() => RunAllAsync(todo, _cts.Token));
            return $"Running {todo.Count} scenario(s).";
        }

        public string Reset()
        {
            if (_running) return "A test run is in progress.";
            _ = Task.Run(ReleaseLeftoverAsync);
            return "Vehicles reset.";
        }

        public string Stop()
        {
            if (!_running) return "Nothing is running.";
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            return "Stopping...";
        }

        private static TestRowDto NewRow(TestScenarioDef s) => new()
        {
            Name = s.Name,
            Description = s.Description,
            VehicleCount = s.Vehicles.Count,
            Expect = $"resolved ≥{s.ExpectedResolved}, unresolved ≥{s.ExpectedUnresolved}",
        };

        private void SetRow(string name, string status, string detail, double seconds)
        {
            lock (_lock)
            {
                var r = _rows.FirstOrDefault(x => x.Name == name);
                if (r is null) return;
                r.Status = status; r.Detail = detail; r.Seconds = seconds;
            }
        }

        private async Task RunAllAsync(List<TestScenarioDef> todo, CancellationToken ct)
        {
            var savedSpeed = Control!.SpeedFactor;
            try
            {
                foreach (var s in todo)
                {
                    if (ct.IsCancellationRequested)
                    {
                        SetRow(s.Name, "FAIL", "Stopped", 0);
                        continue;
                    }
                    await RunOneAsync(s, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Test run failed");
                lock (_lock) _message = "Run error: " + ex.Message;
            }
            finally
            {
                Control.SpeedFactor = savedSpeed;
                _running = false;
            }
        }

        private async Task RunOneAsync(TestScenarioDef s, CancellationToken ct)
        {
            var hooks = Hooks!;
            await ReleaseLeftoverAsync();
            var started = DateTime.UtcNow;
            double Elapsed() => (DateTime.UtcNow - started).TotalSeconds;
            SetRow(s.Name, "RUNNING", "", 0);

            var origins = new Dictionary<int, int>();
            var vehicles = new List<(TestVehicleDef Def, Vehicle V)>();
            foreach (var d in s.Vehicles)
            {
                var v = _registry.GetById(d.VehicleId);
                if (v is null)
                {
                    SetRow(s.Name, "FAIL", $"Vehicle {d.VehicleId} not found", 0);
                    return;
                }
                vehicles.Add((d, v));
                origins[d.VehicleId] = hooks.GetVehicleNodeId(d.VehicleId);
            }

            try
            {
                Control!.SpeedFactor = s.SpeedFactor;
                DeadlockMetrics.Reset();

                // Clean slate
                foreach (var (d, v) in vehicles)
                {
                    _windows.Cancel(d.VehicleId);
                    await _traffic.ReleaseAllLocksAsync(d.VehicleId, ct);
                    await hooks.PlaceVehicleAsync(d.VehicleId, d.Route[0], true, ct);
                    v.UpdateState(ActivityState.Idle, OrderState.Idle);
                    v.SetLoaded(false);
                    v.SetPlannedRoute(d.Route);   // popup ROUTE shows the test route
                }
                await Task.Delay(500, ct);

                // Each vehicle holds the node it stands on, as it would
                // after arriving, then all orders start together.
                foreach (var (d, _) in vehicles)
                    await _traffic.HoldNodeAsync(d.VehicleId, d.Route[0], ct);

                var stamp = DateTime.UtcNow.Ticks;
                foreach (var (d, v) in vehicles)
                {
                    var ok = await _windows.BeginAsync(
                        v.SerialNumber, d.VehicleId, $"TEST-{s.Name}-{d.VehicleId}-{stamp}",
                        d.Route, null, ct);
                    if (!ok)
                    {
                        SetRow(s.Name, "FAIL",
                            $"Could not start route for vehicle {d.VehicleId} (window disabled or route invalid)",
                            Elapsed());
                        return;
                    }
                }

                // Poll
                var deadline = started.AddSeconds(s.TimeoutSeconds);
                DateTime? stableSince = null;
                string lastSnapshot = "";
                var failed = "";

                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    await Task.Delay(250, ct);

                    bool Done((TestVehicleDef Def, Vehicle V) x) =>
                        hooks.IsVehicleAtRest(x.Def.VehicleId)
                        && hooks.GetVehicleNodeId(x.Def.VehicleId) == x.Def.Route[^1];

                    var mustFinish = vehicles.Where(x => x.Def.ExpectFinish).ToList();
                    var mustStay = vehicles.Where(x => !x.Def.ExpectFinish).ToList();

                    var snap = string.Join(",", vehicles.Select(x =>
                        hooks.GetVehicleNodeId(x.Def.VehicleId)));
                    if (snap != lastSnapshot) { lastSnapshot = snap; stableSince = null; }
                    else stableSince ??= DateTime.UtcNow;

                    var finishedOk = mustFinish.All(Done);
                    var staysOk = mustStay.All(x => !Done(x));
                    var stable = stableSince is not null
                        && (DateTime.UtcNow - stableSince.Value).TotalSeconds >= 3;

                    // Anything that must stay stuck must not finish
                    if (!staysOk) { failed = "A vehicle that should stay deadlocked reached its destination"; break; }

                    var metricsOk =
                        DeadlockMetrics.Resolved >= s.ExpectedResolved
                        && DeadlockMetrics.Unresolved >= s.ExpectedUnresolved
                        && (s.ExpectedUnresolved > 0 || DeadlockMetrics.Unresolved == 0);

                    if (finishedOk && (mustStay.Count == 0 || stable) && metricsOk)
                    {
                        // Locks should drain once everyone is done
                        if (mustStay.Count == 0)
                        {
                            var drain = DateTime.UtcNow.AddSeconds(3);
                            while ((_traffic.GetLockedNodeIds().Count > 0
                                    || _traffic.GetLockedMoveIds().Count > 0)
                                   && DateTime.UtcNow < drain)
                                await Task.Delay(250, ct);

                            var left = _traffic.GetLockedNodeIds().Count
                                     + _traffic.GetLockedMoveIds().Count;
                            if (left > 0)
                            {
                                SetRow(s.Name, "FAIL",
                                    $"Finished but {left} lock(s) were left behind", Elapsed());
                                return;
                            }
                        }
                        SetRow(s.Name, "PASS", Summary(), Elapsed());
                        return;
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    SetRow(s.Name, "FAIL", "Stopped", Elapsed());
                    return;
                }

                if (failed.Length == 0)
                {
                    var notDone = vehicles
                        .Where(x => x.Def.ExpectFinish
                            && !(hooks.IsVehicleAtRest(x.Def.VehicleId)
                                 && hooks.GetVehicleNodeId(x.Def.VehicleId) == x.Def.Route[^1]))
                        .Select(x => $"V{x.Def.VehicleId}@{hooks.GetVehicleNodeId(x.Def.VehicleId)}");
                    failed = "Timed out. " + Summary()
                        + (notDone.Any() ? " Not finished: " + string.Join(" ", notDone) : "");
                }
                SetRow(s.Name, "FAIL", failed, Elapsed());
            }
            catch (OperationCanceledException)
            {
                SetRow(s.Name, "FAIL", "Stopped", Elapsed());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Test scenario {Name} error", s.Name);
                SetRow(s.Name, "FAIL", "Error: " + ex.Message, Elapsed());
            }
            finally
            {
                var items = vehicles
                    .Select(x => (x.Def.VehicleId, x.V,
                        origins[x.Def.VehicleId] > 0 ? origins[x.Def.VehicleId] : x.Def.Route[0]))
                    .ToList();

                bool inspect;
                lock (_lock)
                {
                    var row = _rows.FirstOrDefault(r => r.Name == s.Name);
                    inspect = row is not null && row.Status == "FAIL"
                              && row.Detail != "Stopped";
                    if (inspect && row is not null)
                        row.Detail += "  [vehicles left in place - click them to inspect; RESET to clear]";
                }

                if (inspect) _leftover = items;
                else await CleanupAsync(items);
            }
        }

        private async Task ReleaseLeftoverAsync()
        {
            var items = _leftover;
            _leftover = new();
            if (items.Count > 0) await CleanupAsync(items);
        }

        private async Task CleanupAsync(List<(int Id, Vehicle V, int Home)> items)
        {
            var hooks = Hooks;
            foreach (var (id, v, home) in items)
            {
                try
                {
                    _windows.Cancel(id);
                    await _traffic.ReleaseAllLocksAsync(id);
                    if (hooks is not null) await hooks.PlaceVehicleAsync(id, home, false);
                    v.ClearPlannedRoute();
                    v.UpdateState(ActivityState.Idle, OrderState.Idle);
                    v.SetLoaded(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Test cleanup for vehicle {Id} failed", id);
                }
            }
        }

        private static string Summary()
            => $"detected {DeadlockMetrics.Detected}, resolved {DeadlockMetrics.Resolved}, unresolved {DeadlockMetrics.Unresolved}";
    }
}
