using System.Text.Json;
using AGV.Core.Messages;
using AGV.Fleet.Infrastructure;
using AGV.Fleet.Services;
using Microsoft.Extensions.Hosting;

namespace AGV.Dashboard.Services
{
    public sealed record ScenarioMissionDef(
        int OffsetSeconds, string PickLocation, string DropLocation, string Priority);

    public sealed record ScenarioDefinition(
        string Name, List<ScenarioMissionDef> Missions);

    public sealed record ScenarioRowDto(
        int Index, int OffsetSeconds, string Pick, int? PickNode,
        string Drop, int? DropNode, string Priority, string Status,
        int? VehicleId, int? MissionId, string? StagedAt);

    public sealed record ScenarioRunDto(
        string Name, bool Running, int ElapsedSeconds, string StartedAt,
        IReadOnlyList<ScenarioRowDto> Rows);

    /// <summary>
    /// SIMULATION / TEST / DEMO ONLY. Stores named, chronologically staged
    /// mission sets as JSON files and plays one back against the local
    /// wall clock (time since Run was pressed): each mission is enqueued when REAL (wall-clock) time since Run reaches
    /// its offset. Progress is tracked by tagging each mission's
    /// SourceSystemReference with "SCN:{runId}:{index}" and watching the
    /// mission queue and the active-mission registry.
    /// </summary>
    public sealed class ScenarioService
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        private readonly MissionBoardService _board;
        private readonly MissionQueueService _queue;
        private readonly VehicleRegistry _vehicles;
        private readonly ActiveMissionRegistry _active;
        private readonly string _folder;

        private readonly object _gate = new();
        private RunState? _run;
        private CancellationTokenSource? _cts;

        public ScenarioService(
            MissionBoardService board,
            MissionQueueService queue,
            VehicleRegistry vehicles,
            ActiveMissionRegistry active,
            IHostEnvironment env)
        {
            _board = board;
            _queue = queue;
            _vehicles = vehicles;
            _active = active;
            _folder = Path.Combine(env.ContentRootPath, "Scenarios");
        }

        public bool IsRunning { get { lock (_gate) return _run?.Running == true; } }

        // ------------------------------------------------------------
        // Storage (one JSON file per named simulation)
        // ------------------------------------------------------------

        public static string SafeName(string? name)
        {
            var n = (name ?? "").Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n.Length > 64 ? n[..64] : n;
        }

        private string PathFor(string safe) => Path.Combine(_folder, safe + ".json");

        public IReadOnlyList<string> ListSaved()
            => Directory.Exists(_folder)
                ? Directory.GetFiles(_folder, "*.json")
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

        public void Save(ScenarioDefinition def)
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(PathFor(SafeName(def.Name)), JsonSerializer.Serialize(def, Json));
        }

        public ScenarioDefinition? Load(string name)
        {
            var path = PathFor(SafeName(name));
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ScenarioDefinition>(File.ReadAllText(path))
                : null;
        }

        public bool Delete(string name)
        {
            var path = PathFor(SafeName(name));
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        // ------------------------------------------------------------
        // Playback
        // ------------------------------------------------------------

        public string Run(ScenarioDefinition def)
        {
            lock (_gate)
            {
                if (_run?.Running == true) return "A simulation is already running. Stop it first.";
                if (def.Missions.Count == 0) return "Nothing to run: add missions first.";

                var run = new RunState
                {
                    Name = def.Name,
                    RunId = Guid.NewGuid().ToString("N")[..6],
                    Clock = System.Diagnostics.Stopwatch.StartNew(),
                    StartedAt = DateTime.Now.ToString("HH:mm:ss"),
                    Running = true,
                    Rows = def.Missions
                        .Select((m, i) => new Row
                        {
                            Index = i,
                            Def = m,
                            PickNode = _board.ResolveNode(m.PickLocation, AGV.Core.Enums.OperationType.Pick),
                            DropNode = _board.ResolveNode(m.DropLocation, AGV.Core.Enums.OperationType.Drop),
                        })
                        .ToList(),
                };
                _run = run;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => LoopAsync(run, _cts.Token));
                return $"Running '{def.Name}' ({def.Missions.Count} missions).";
            }
        }

        public string Stop()
        {
            RunState? run;
            lock (_gate)
            {
                run = _run;
                if (run is null || !run.Running) return "Nothing is running.";
                _cts?.Cancel();
                run.Running = false;
            }

            // Pull back this run's missions that are still waiting in the queue.
            var prefix = $"SCN:{run.RunId}:";
            var cancelled = 0;
            foreach (var m in _queue.GetSnapshot())
                if (m.SourceSystemReference?.StartsWith(prefix) == true && _queue.Cancel(m.MissionId))
                    cancelled++;
            return $"Stopped. {cancelled} queued mission(s) cancelled; active missions finish normally.";
        }

        public ScenarioRunDto? GetRun()
        {
            lock (_gate)
            {
                if (_run is null) return null;
                var elapsed = _run.Running
                    ? (int)_run.Clock.Elapsed.TotalSeconds
                    : _run.FinalElapsed;
                return new ScenarioRunDto(
                    _run.Name, _run.Running, elapsed, _run.StartedAt,
                    _run.Rows.Select(r => new ScenarioRowDto(
                        r.Index, r.Def.OffsetSeconds, r.Def.PickLocation, r.PickNode,
                        r.Def.DropLocation, r.DropNode, r.Def.Priority, r.Status,
                        r.VehicleId, r.MissionId, r.StagedAt)).ToList());
            }
        }

        public void ClearRun()
        {
            lock (_gate)
            {
                if (_run?.Running == true) return;
                _run = null;
            }
        }

        private async Task LoopAsync(RunState run, CancellationToken ct)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                while (await timer.WaitForNextTickAsync(ct))
                {
                    var elapsed = (int)run.Clock.Elapsed.TotalSeconds;

                    // 1) Release missions whose staging time has arrived.
                    List<Row> due;
                    lock (_gate)
                        due = run.Rows.Where(r => r.Status == "Pending"
                                               && r.Def.OffsetSeconds <= elapsed).ToList();
                    foreach (var r in due)
                    {
                        var (ok, msg) = await _board.TryCreateMissionAsync(
                            r.Def.PickLocation, r.Def.DropLocation, r.Def.Priority,
                            $"SCN:{run.RunId}:{r.Index}");
                        lock (_gate)
                        {
                            r.Status = ok ? "Queued" : "Failed";
                            r.StagedAt = DateTime.Now.ToString("HH:mm:ss");
                        }
                    }

                    // 2) Refresh Queued / Active / Done from the live queue + registry.
                    var prefix = $"SCN:{run.RunId}:";
                    var queued = new Dictionary<string, int>();
                    foreach (var qm in _queue.GetSnapshot())
                        if (qm.SourceSystemReference is { } qs && qs.StartsWith(prefix))
                            queued[qs] = qm.MissionId;
                    var activeBy = new Dictionary<string, (int VehicleId, int MissionId)>();
                    foreach (var v in _vehicles.GetAll())
                    {
                        var m = _active.GetByVehicle(v.VehicleId);
                        if (m?.SourceSystemReference is { } s && s.StartsWith(prefix))
                            activeBy[s] = (v.VehicleId, m.MissionId);
                    }

                    var finished = false;
                    lock (_gate)
                    {
                        foreach (var r in run.Rows)
                        {
                            if (r.Status is "Pending" or "Failed") continue;
                            var tag = $"{prefix}{r.Index}";
                            if (activeBy.TryGetValue(tag, out var act))
                            { r.Status = "Active"; r.VehicleId = act.VehicleId; r.MissionId = act.MissionId; r.Seen = true; r.Absent = 0; }
                            else if (queued.TryGetValue(tag, out var qid))
                            { r.Status = "Queued"; r.MissionId = qid; r.Seen = true; r.Absent = 0; }
                            else if (r.Seen && r.Status != "Done" && ++r.Absent >= 3)
                            { r.Status = "Done"; }
                        }

                        if (run.Rows.All(r => r.Status is "Done" or "Failed"))
                        {
                            run.FinalElapsed = elapsed;
                            run.Running = false;
                            finished = true;
                        }
                    }
                    if (finished) return;
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (_gate)
                    if (run.FinalElapsed == 0)
                        run.FinalElapsed = (int)run.Clock.Elapsed.TotalSeconds;
            }
        }

        private sealed class RunState
        {
            public string Name = "";
            public string RunId = "";
            public System.Diagnostics.Stopwatch Clock = null!;
            public string StartedAt = "";
            public bool Running;
            public int FinalElapsed;
            public List<Row> Rows = new();
        }

        private sealed class Row
        {
            public int Index;
            public ScenarioMissionDef Def = null!;
            public string Status = "Pending";   // Pending | Queued | Active | Done | Failed
            public int? VehicleId;
            public int? MissionId;
            public int? PickNode;
            public int? DropNode;
            public string? StagedAt;
            public bool Seen;
            public int Absent;
        }
    }
}
