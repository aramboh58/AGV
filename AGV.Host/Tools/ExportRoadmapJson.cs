using System.Text.Json;
using AGV.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace AGV.Tools
{
    public static class ExportRoadmapJson
    {
        public static async Task ExportAsync(AgvDbContext db, string outputPath)
        {
            // Get active nodes (latest version, not deleted)
            var nodes = await db.Set<AGV.Core.Entities.Node>()
                .Where(n => !n.IsDeleted)
                .GroupBy(n => n.NodeId)
                .Select(g => g.OrderByDescending(n => n.EffectiveFromVersionId).First())
                .ToListAsync();

            // Get active moves (latest version, not deleted)
            var moves = await db.Set<AGV.Core.Entities.Move>()
                .Where(m => !m.IsDeleted)
                .GroupBy(m => m.MoveId)
                .Select(g => g.OrderByDescending(m => m.EffectiveFromVersionId).First())
                .ToListAsync();

            var roadmap = new
            {
                nodes = nodes.Select(n => new
                {
                    nodeId = n.NodeId,
                    nodeName = n.NodeName ?? $"N{n.NodeId:D4}",
                    x = n.Position.X,
                    y = n.Position.Y,
                    nodeType = n.NodeType.ToString(),
                    mapId = n.MapId
                }),
                edges = moves.Select(m => new
                {
                    edgeId = m.MoveId,
                    startNodeId = m.FromNodeId,
                    endNodeId = m.ToNodeId,
                    speed = m.Speed.DefaultSpeed
                })
            };

            var json = JsonSerializer.Serialize(roadmap, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(outputPath, json);
            Console.WriteLine($"Exported {nodes.Count} nodes and {moves.Count} edges to {outputPath}");
        }
    }
}