using AGV.Core.Enums;
using AGV.Core.Interfaces;
using AGV.Core.Messages;
using AGV.Fleet.Infrastructure;
using AGV.Fleet.Services;
using AGV.Topology.Services;

namespace AGV.Dashboard.Services
{
    public sealed record MissionPartRowDto(string Operation, string Label);

    public sealed record MissionRowDto(
        int MissionId,
        string Status,            // "ACTIVE" | "QUEUED"
        string Priority,
        int? VehicleId,
        string? VehicleState,
        int CurrentIndex,         // -1 for queued missions
        IReadOnlyList<MissionPartRowDto> Parts,
        int AgeSeconds,
        bool CanCancel);

    public sealed record MissionBuilderOptions(
        IReadOnlyList<string> PickLocations,
        IReadOnlyList<string> DropLocations,
        IReadOnlyList<string> Priorities);

    public sealed record MissionBoardDto(
        IReadOnlyList<MissionRowDto> Active,
        IReadOnlyList<MissionRowDto> Queued,
        int QueuedTotal);

    /// <summary>
    /// Read-only snapshot of the mission queue and the active missions
    /// (one per vehicle), for the dashboard's mission queue viewer (#4).
    /// Pull model: the Dashboard polls GetSnapshot while its panel is open.
    /// </summary>
    public sealed class MissionBoardService
    {
        private const int MaxQueuedRows = 100;

        private readonly MissionQueueService _queue;
        private readonly ActiveMissionRegistry _active;
        private readonly VehicleRegistry _vehicles;
        private readonly LocationCatalog _locations;
        private readonly RoadMapGraphHolder _graphHolder;
        private readonly IFleetManager _fleet;

        public MissionBoardService(
            MissionQueueService queue,
            ActiveMissionRegistry active,
            VehicleRegistry vehicles,
            LocationCatalog locations,
            RoadMapGraphHolder graphHolder,
            IFleetManager fleet)
        {
            _queue = queue;
            _active = active;
            _vehicles = vehicles;
            _locations = locations;
            _graphHolder = graphHolder;
            _fleet = fleet;
        }

        public bool Cancel(int missionId) => _queue.Cancel(missionId);

        // ------------------------------------------------------------
        // Mission builder (#3). Two parts for now (Pick then Drop),
        // because that is all the dispatcher/simulator execute.
        // ------------------------------------------------------------

        public int? ResolveNode(string locationName, OperationType op)
        {
            var nodes = _locations.ResolveNodes(locationName, op);
            return nodes.Count > 0 ? nodes[0] : null;
        }

        public MissionBuilderOptions GetBuilderOptions()
        {
            static List<string> Names(IEnumerable<LocationEntry> e)
                => e.Select(x => x.LocationName).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();

            var priorities = Enum.GetNames(typeof(MissionPriority))
                .Where(n => n != "ParkOrCharge")
                .ToList();

            return new MissionBuilderOptions(
                Names(_locations.GetEntries(OperationType.Pick)),
                Names(_locations.GetEntries(OperationType.Drop)),
                priorities);
        }

        public async Task<string> CreateMissionAsync(
            string pickName, string dropName, string priorityName)
            => (await TryCreateMissionAsync(pickName, dropName, priorityName, null)).Message;

        public async Task<(bool Ok, string Message)> TryCreateMissionAsync(
            string pickName, string dropName, string priorityName, string? sourceRef)
        {
            var pickNodes = _locations.ResolveNodes(pickName, OperationType.Pick);
            if (pickNodes.Count == 0) return (false, $"Unknown pick location '{pickName}'.");
            var dropNodes = _locations.ResolveNodes(dropName, OperationType.Drop);
            if (dropNodes.Count == 0) return (false, $"Unknown drop location '{dropName}'.");

            if (!Enum.TryParse<MissionPriority>(priorityName, out var priority))
                priority = MissionPriority.Normal;

            var context = new MissionContext
            {
                MissionId = 0, // assigned by the fleet manager
                CurrentOrderId = $"ORD{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
                PickupNodeId = pickNodes[0],
                DropNodeId = dropNodes[0],
                Parts = new[]
                {
                    new MissionPart(pickName, OperationType.Pick, pickNodes[0]),
                    new MissionPart(dropName, OperationType.Drop, dropNodes[0]),
                },
                Priority = priority,
                SourceSystemReference = sourceRef,
                CreatedAt = DateTime.UtcNow,
            };

            try
            {
                await _fleet.EnqueueMissionAsync(context);
                return (true, $"Enqueued {pickName} → {dropName} ({priority}).");
            }
            catch (Exception ex)
            {
                return (false, $"Enqueue failed: {ex.Message}");
            }
        }

        public MissionBoardDto GetSnapshot()
        {
            var graph = _graphHolder.Graph;
            var now = DateTime.UtcNow;

            var active = new List<MissionRowDto>();
            foreach (var v in _vehicles.GetAll().OrderBy(v => v.VehicleId))
            {
                var m = _active.GetByVehicle(v.VehicleId);
                if (m is null) continue;

                var parts = BuildParts(m, graph);
                var idx = parts.Count == 0
                    ? -1
                    : v.IsLoaded ? Math.Min(1, parts.Count - 1) : 0;

                active.Add(new MissionRowDto(
                    m.MissionId, "ACTIVE", m.Priority.ToString(),
                    v.VehicleId, v.ActivityState.ToString(),
                    idx, parts, AgeSeconds(m.CreatedAt, now), false));
            }

            var snapshot = _queue.GetSnapshot();
            var queued = snapshot
                .Take(MaxQueuedRows)
                .Select(m => new MissionRowDto(
                    m.MissionId, "QUEUED", m.Priority.ToString(),
                    null, null, -1, BuildParts(m, graph),
                    AgeSeconds(m.CreatedAt, now), true))
                .ToList();

            return new MissionBoardDto(active, queued, snapshot.Count);
        }

        private List<MissionPartRowDto> BuildParts(MissionContext m, RoadMapGraph? graph)
        {
            var result = new List<MissionPartRowDto>();
            if (m.Parts.Count > 0)
            {
                foreach (var p in m.Parts)
                    result.Add(new MissionPartRowDto(
                        p.Operation.ToString(),
                        Label(graph, p.NodeId, p.Operation, p.LocationName)));
            }
            else
            {
                result.Add(new MissionPartRowDto("Pick",
                    Label(graph, m.PickupNodeId, OperationType.Pick, null)));
                result.Add(new MissionPartRowDto("Drop",
                    Label(graph, m.DropNodeId, OperationType.Drop, null)));
            }
            return result;
        }

        private string Label(RoadMapGraph? graph, int nodeId, OperationType op, string? name)
        {
            var text = !string.IsNullOrWhiteSpace(name)
                ? name
                : _locations.GetLabel(nodeId, op)
                  ?? graph?.GetNode(nodeId)?.NodeName
                  ?? nodeId.ToString();
            return $"{text} ({nodeId})";
        }

        private static int AgeSeconds(DateTime created, DateTime nowUtc)
        {
            var c = created.Kind == DateTimeKind.Local ? created.ToUniversalTime() : created;
            return (int)Math.Max(0, (nowUtc - c).TotalSeconds);
        }
    }
}
