using AGV.Core.Enums;
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

        public MissionBoardService(
            MissionQueueService queue,
            ActiveMissionRegistry active,
            VehicleRegistry vehicles,
            LocationCatalog locations,
            RoadMapGraphHolder graphHolder)
        {
            _queue = queue;
            _active = active;
            _vehicles = vehicles;
            _locations = locations;
            _graphHolder = graphHolder;
        }

        public bool Cancel(int missionId) => _queue.Cancel(missionId);

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
