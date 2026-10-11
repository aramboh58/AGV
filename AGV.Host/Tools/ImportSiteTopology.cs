using System.Text;
using AGV.Core.Entities;
using AGV.Core.Enums;
using AGV.Core.ValueObjects;
using AGV.Persistence.Data;
using AGV.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using VehicleEntity = AGV.Core.Entities.Vehicle;

namespace AGV.Tools
{
    public static class ImportSiteTopology
    {
        private const int VersionId = 2;
        private const string MapId = "FLOOR_1";
        private const int RoutingTypeAgv = 2;
        private const int RoutingTypeApl = 3;

        public static async Task ImportAsync(
            AgvDbContext db,
            string nodesFilePath,
            string movesFilePath)
        {
            Console.WriteLine("Starting NYT full topology import...");

            // ── Clear existing topology ──────────────────────────────
            Console.WriteLine("Clearing existing nodes and moves...");
            await db.Set<Move>().ExecuteDeleteAsync();
            await db.Set<Node>().ExecuteDeleteAsync();

            // ── Import nodes ─────────────────────────────────────────
            Console.WriteLine("Importing nodes...");
            var nodeLines = await File.ReadAllLinesAsync(
                nodesFilePath, Encoding.UTF8);

            var seenNodeIds = new HashSet<int>();
            var nodes = new List<Node>();

            foreach (var line in nodeLines.Skip(1)) // skip header
            {
                var cols = line.Split('\t');
                if (cols.Length < 11) continue;

                if (!int.TryParse(cols[2].Trim(), out var nodeNumber)) continue;
                if (seenNodeIds.Contains(nodeNumber)) continue;
                seenNodeIds.Add(nodeNumber);

                var nodeName = cols[3].Trim();
                if (!decimal.TryParse(cols[4].Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var xMm)) continue;
                if (!decimal.TryParse(cols[5].Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var yMm)) continue;

                if (!int.TryParse(cols[8].Trim(), out var stopType)) continue;
                if (!int.TryParse(cols[10].Trim(), out var destOnly)) continue;

                // Convert mm to cm
                var xCm = xMm / 10m;
                var yCm = yMm / 10m;

                var nodeType = stopType == 0
                    ? NodeType.StopNode
                    : destOnly == 1
                        ? NodeType.DestinationOnly
                        : NodeType.NoStopNode;

                nodes.Add(new Node(
                    nodeId: nodeNumber,
                    effectiveFromVersionId: VersionId,
                    position: new Coordinate(xCm, yCm),
                    nodeType: nodeType,
                    mapId: MapId,
                    nodeName: nodeName));
            }

            await db.Set<Node>().AddRangeAsync(nodes);
            await db.SaveChangesAsync();
            Console.WriteLine($"Imported {nodes.Count} nodes.");

            // ── Import moves ─────────────────────────────────────────
            Console.WriteLine("Importing moves...");
            var moveLines = await File.ReadAllLinesAsync(
                movesFilePath, Encoding.UTF8);

            var moves = new List<Move>();
            int moveId = 1;
            int skipped = 0;

            foreach (var line in moveLines.Skip(1)) // skip header
            {
                var cols = line.Split('\t');
                if (cols.Length < 9) continue;

                if (!int.TryParse(cols[0].Trim(), out var fromNode)) continue;
                if (!int.TryParse(cols[1].Trim(), out var toNode)) continue;

                var routingTypeName = cols[2].Trim();
                var routingTypeId = routingTypeName == "APL"
                    ? RoutingTypeApl : RoutingTypeAgv;

                if (!decimal.TryParse(cols[5].Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var speedRaw)) continue;

                if (!decimal.TryParse(cols[8].Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var distanceMm)) continue;

                // Skip if either node wasn't imported
                if (!seenNodeIds.Contains(fromNode)
                    || !seenNodeIds.Contains(toNode))
                {
                    skipped++;
                    continue;
                }

                // Skip self-referential moves
                if (fromNode == toNode)
                {
                    skipped++;
                    continue;
                }

                var isReverse = speedRaw < 0;
                var speedAbs = Math.Abs(speedRaw);

                // Speed in NYT DB is mm/s — convert to m/s
                var speedMs = speedAbs / 1000m;
                if (speedMs <= 0) speedMs = 0.1m; // safety floor

                // Distance in NYT DB is mm — convert to cm
                var arcLengthCm = distanceMm / 10m;
                if (arcLengthCm <= 0) arcLengthCm = 1m; // safety floor

                var travelDirection = isReverse
                    ? TravelDirection.Reverse
                    : TravelDirection.Forward;

                // Clothoid — start/end heading not available from
                // this import; set to 0 as placeholder.
                // Full clothoid parameters require separate import
                // from the FTI road system file (Phase 3).
                var clothoid = new ClothoidParameters(
                    startHeading: 0m,
                    endHeading: 0m,
                    parameterA: 0m,
                    arcLength: arcLengthCm);

                var speed = new SpeedConstraint(
                    defaultSpeed: speedMs,
                    maxSpeed: speedMs);

                moves.Add(new Move(
                    moveId: moveId++,
                    effectiveFromVersionId: VersionId,
                    fromNodeId: fromNode,
                    toNodeId: toNode,
                    routingTypeId: routingTypeId,
                    travelDirection: travelDirection,
                    clothoid: clothoid,
                    speed: speed));
            }

            await db.Set<Move>().AddRangeAsync(moves);
            await db.SaveChangesAsync();
            Console.WriteLine(
                $"Imported {moves.Count} moves. Skipped {skipped}.");

            // Reset all vehicle positions to node 9030 (parking queue head)
            Console.WriteLine("Resetting vehicle positions...");
            var vehicles = await db.Set<VehicleEntity>().ToListAsync();
            foreach (var v in vehicles)
                v.UpdatePosition(9030, MapId);
            await db.SaveChangesAsync();
            Console.WriteLine($"Reset {vehicles.Count} vehicle positions to node 9030.");

            // ── Insert new RoadmapVersion record ─────────────────────
            var version = new RoadmapVersionRecord
            {
                VersionLabel = "NYT College Point v2.0 (full topology import)",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                CreatedByUser = "system"
            };
            await db.Set<RoadmapVersionRecord>().AddAsync(version);
            await db.SaveChangesAsync();

            Console.WriteLine("NYT full topology import complete.");
            Console.WriteLine(
                $"Summary: {nodes.Count} nodes, {moves.Count} moves, " +
                $"version {VersionId}.");
        }
    }
}
