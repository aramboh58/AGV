using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using AGV.Core.Entities;
using AGV.Core.Enums;
using AGV.Core.ValueObjects;
using AGV.Persistence.Data;
using AGV.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AGV.Persistence.Services
{
    /// <summary>
    /// Seeds the database with topology data from nyt_agv_roadmap.json
    /// if no active RoadmapVersion exists.
    /// Runs once at startup before TopologyBackgroundService polls.
    /// </summary>
    public sealed class TopologySeedService
    {
        private readonly AgvDbContext _db;
        private readonly ILogger<TopologySeedService> _logger;

        private const decimal FeetToCm = 30.48m;
        private const int DefaultRoutingTypeId = 1;
        private const int SeedVersionId = 1;
        private const string MapId = "NYT_COLLEGE_POINT_V1";

        // Operation type IDs
        private const int OpTypePick = 1;
        private const int OpTypeDrop = 2;
        private const int OpTypeCharge = 3;
        private const int OpTypePark = 4;

        // Location type IDs
        private const int LocTypeRoll = 1;
        private const int LocTypeWasteBin = 2;
        private const int LocTypeCharge = 3;

        private const int LocationVersionId = 1;

        public TopologySeedService(
            AgvDbContext db,
            ILogger<TopologySeedService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task SeedIfEmptyAsync(
            string jsonFilePath,
            CancellationToken cancellationToken = default)
        {
            var alreadySeeded = await _db.Set<RoadmapVersionRecord>()
                .AnyAsync(cancellationToken);

            if (alreadySeeded)
            {
                _logger.LogInformation(
                    "Topology already seeded — skipping.");
                return;
            }

            _logger.LogInformation(
                "No topology found — seeding from {Path}", jsonFilePath);

            var json = await File.ReadAllTextAsync(
                jsonFilePath, cancellationToken);

            var roadmap = JsonSerializer.Deserialize<RoadmapJson>(json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })
                ?? throw new InvalidOperationException(
                    "Failed to deserialize roadmap JSON.");

            // Build string→int node ID mapping
            var nodeIdMap = new Dictionary<string, int>();
            int nextId = 1;
            foreach (var n in roadmap.Nodes)
                nodeIdMap[n.NodeId] = nextId++;

            // Insert nodes
            var nodes = roadmap.Nodes.Select(n => new Node(
                nodeId: nodeIdMap[n.NodeId],
                effectiveFromVersionId: SeedVersionId,
                position: new Coordinate(
                    n.X * FeetToCm,
                    n.Y * FeetToCm),
                nodeType: MapNodeType(n.NodeType),
                mapId: MapId,
                nodeName: n.NodeId))
            .ToList();

            await _db.Set<Node>().AddRangeAsync(nodes, cancellationToken);

            // Insert moves — skip edges where either node is unmapped
            int moveId = 1;
            var moves = new List<Move>();

            foreach (var e in roadmap.Edges)
            {
                if (!nodeIdMap.TryGetValue(e.StartNodeId, out var fromId)
                 || !nodeIdMap.TryGetValue(e.EndNodeId, out var toId))
                {
                    _logger.LogWarning(
                        "Skipping edge {EdgeId} — unmapped node reference",
                        e.EdgeId);
                    continue;
                }

                var heading = MapDirectionToHeading(e.Direction);
                var arcLengthCm = e.Length * FeetToCm;
                var maxSpeed = (decimal)e.MaxSpeed;

                var clothoid = new ClothoidParameters(
                    startHeading: heading,
                    endHeading: heading,
                    parameterA: 0m,
                    arcLength: arcLengthCm);

                var speed = new SpeedConstraint(
                    defaultSpeed: maxSpeed * 0.8m,
                    maxSpeed: maxSpeed);

                moves.Add(new Move(
                    moveId: moveId++,
                    effectiveFromVersionId: SeedVersionId,
                    fromNodeId: fromId,
                    toNodeId: toId,
                    routingTypeId: DefaultRoutingTypeId,
                    travelDirection: TravelDirection.Forward,
                    clothoid: clothoid,
                    speed: speed));
            }

            await _db.Set<Move>().AddRangeAsync(moves, cancellationToken);

            // Insert RoadmapVersion record
            var version = new RoadmapVersionRecord
            {
                VersionLabel = "NYT College Point v1.0 (seeded)",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                CreatedByUser = "system"
            };
            await _db.Set<RoadmapVersionRecord>()
                .AddAsync(version, cancellationToken);

            // Insert locations and assignments
            var locationData = BuildLocations(nodeIdMap);
            foreach (var (loc, asgn) in locationData)
            {
                await _db.Set<Location>().AddAsync(loc, cancellationToken);
                await _db.Set<LocationAssignment>()
                    .AddAsync(asgn, cancellationToken);
            }

            _logger.LogInformation(
                "Locations seeded: {Count} locations with assignments.",
                locationData.Count);

            // Insert vehicle fleet
            var vehicles = BuildFleet();
            await _db.Set<Vehicle>().AddRangeAsync(vehicles, cancellationToken);

            _logger.LogInformation(
                "Fleet seeded: {Count} vehicles.", vehicles.Count);

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Topology seeded: {Nodes} nodes, {Moves} moves.",
                nodes.Count, moves.Count);
        }

        private static List<(Location location, LocationAssignment assignment)>
            BuildLocations(IReadOnlyDictionary<string, int> nodeIdMap)
        {
            var result = new List<(Location, LocationAssignment)>();
            int locationId = 1;
            int assignmentId = 1;

            // Staging pickup positions (STG01-STG03) — Roll pick
            foreach (var key in new[] { "STG01", "STG02", "STG03" })
            {
                if (!nodeIdMap.TryGetValue(key, out var nodeId)) continue;

                var loc = new Location(
                    locationId,
                    LocationVersionId,
                    $"Roll Staging Pickup {key}",
                    $"Roll pickup staging position {key}");

                var asgn = new LocationAssignment(
                    assignmentId,
                    LocationVersionId,
                    locationId,
                    nodeId,
                    OpTypePick,
                    LocTypeRoll);

                result.Add((loc, asgn));
                locationId++;
                assignmentId++;
            }

            // Lower press stands LPS01-LPS18 approach nodes — Roll drop
            for (int i = 1; i <= 18; i++)
            {
                var key = $"LPS{i:D2}A";
                if (!nodeIdMap.TryGetValue(key, out var nodeId)) continue;

                var loc = new Location(
                    locationId,
                    LocationVersionId,
                    $"Lower Press Stand {i}",
                    $"Lower corridor press stand {i} delivery position");

                var asgn = new LocationAssignment(
                    assignmentId,
                    LocationVersionId,
                    locationId,
                    nodeId,
                    OpTypeDrop,
                    LocTypeRoll);

                result.Add((loc, asgn));
                locationId++;
                assignmentId++;
            }

            // Upper press stands UPS01-UPS12 approach nodes — Roll drop
            for (int i = 1; i <= 12; i++)
            {
                var key = $"UPS{i:D2}A";
                if (!nodeIdMap.TryGetValue(key, out var nodeId)) continue;

                var loc = new Location(
                    locationId,
                    LocationVersionId,
                    $"Upper Press Stand {i}",
                    $"Upper corridor press stand {i} delivery position");

                var asgn = new LocationAssignment(
                    assignmentId,
                    LocationVersionId,
                    locationId,
                    nodeId,
                    OpTypeDrop,
                    LocTypeRoll);

                result.Add((loc, asgn));
                locationId++;
                assignmentId++;
            }

            return result;
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static NodeType MapNodeType(string nodeType)
            => nodeType.ToLowerInvariant() switch
            {
                "waypoint" => NodeType.StopNode,
                "fork_pickup" => NodeType.StopNode,
                "fork_dropoff" => NodeType.StopNode,
                "charge" => NodeType.StopNode,
                "staging" => NodeType.StopNode,
                "mailroom" => NodeType.StopNode,
                _ => NodeType.StopNode
            };

        private static decimal MapDirectionToHeading(string direction)
            => direction.ToLowerInvariant() switch
            {
                "east" => 0m,
                "west" => 180m,
                "north" => 90m,
                "south" => -90m,
                _ => 0m
            };

        // ----------------------------------------------------------------
        // JSON deserialization models
        // ----------------------------------------------------------------

        private sealed class RoadmapJson
        {
            public List<NodeJson> Nodes { get; set; } = new();
            public List<EdgeJson> Edges { get; set; } = new();
        }

        private sealed class NodeJson
        {
            public string NodeId { get; set; } = string.Empty;
            public decimal X { get; set; }
            public decimal Y { get; set; }
            public string NodeType { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
        }

        private sealed class EdgeJson
        {
            public string EdgeId { get; set; } = string.Empty;
            public string StartNodeId { get; set; } = string.Empty;
            public string EndNodeId { get; set; } = string.Empty;
            public decimal Length { get; set; }
            public string Direction { get; set; } = string.Empty;
            public double MaxSpeed { get; set; }
            public bool Bidirectional { get; set; }
        }
        private static List<Vehicle> BuildFleet()
        {
            var vehicles = new List<Vehicle>();

            // 16 fork vehicles: F01-F16
            for (int i = 1; i <= 16; i++)
            {
                vehicles.Add(new Vehicle(
                    vehicleId: i,
                    vehicleName: $"F{i:D2}",
                    serialNumber: $"SN-F{i:D2}",
                    vehicleType: VehicleType.Fork,
                    initialMapId: "NYT_COLLEGE_POINT_V1"));
            }

            // 4 waste vehicles: W01-W04
            for (int i = 1; i <= 4; i++)
            {
                vehicles.Add(new Vehicle(
                    vehicleId: 16 + i,
                    vehicleName: $"W{i:D2}",
                    serialNumber: $"SN-W{i:D2}",
                    vehicleType: VehicleType.WasteBin,
                    initialMapId: "NYT_COLLEGE_POINT_V1"));
            }

            return vehicles;
        }
    }
}