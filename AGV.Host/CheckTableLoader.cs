using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AGV.Fleet.Infrastructure;

namespace AGV.Host
{
    /// <summary>
    /// Loads the TrafficCheck table (NYT check export) into CheckTableCache
    /// at startup. Creates the table if it does not exist yet.
    /// </summary>
    public static class CheckTableLoader
    {
        public static async Task LoadAsync(
            AGV.Persistence.Data.AgvDbContext db,
            CheckTableCache cache,
            ILogger logger)
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TABLE IF NOT EXISTS TrafficCheck (" +
                "CheckId INTEGER PRIMARY KEY AUTOINCREMENT, OwnersName TEXT NULL, " +
                "OwnersType TEXT NOT NULL, OwnersNumber INTEGER NOT NULL, " +
                "CheckType TEXT NOT NULL, CheckNumber INTEGER NOT NULL, " +
                "CheckName TEXT NULL, Enabled INTEGER NOT NULL DEFAULT 1, " +
                "DistanceMm INTEGER NULL, AplusCheckMacro TEXT NULL, " +
                "CheckOrder INTEGER NOT NULL DEFAULT 0, " +
                "ItineraryNode INTEGER NOT NULL DEFAULT 0, UserGenerated INTEGER NOT NULL DEFAULT 0)");

            var rows = await db.Database.SqlQueryRaw<Row>(
                "SELECT CheckId, OwnersName, OwnersType, OwnersNumber, CheckType, " +
                "CheckNumber, CheckName, Enabled, DistanceMm, AplusCheckMacro, " +
                "CheckOrder FROM TrafficCheck").ToListAsync();

            var records = new List<CheckRecord>();
            foreach (var r in rows)
            {
                var type = r.CheckType switch
                {
                    "Node" => CheckType.Node,
                    "Move" => CheckType.Move,
                    "NodeWithItin" or "NodeWithItinerary" => CheckType.NodeWithItinerary,
                    "Distance" => CheckType.Distance,
                    "Aplus" => CheckType.Aplus,
                    "Itinerary" => CheckType.Itinerary,
                    _ => (CheckType?)null,
                };
                // APlus (script) checks and the itinerary check types are kept
                // in the table but not evaluated yet.
                if (type is null || type == CheckType.Aplus
                    || type == CheckType.Itinerary
                    || type == CheckType.NodeWithItinerary) continue;

                records.Add(new CheckRecord
                {
                    CheckId = r.CheckId,
                    OwnersType = r.OwnersType,
                    OwnersNumber = r.OwnersNumber,
                    CheckType = type.Value,
                    CheckOrder = r.CheckOrder,
                    Enabled = r.Enabled != 0,
                    AplusMacroName = r.AplusCheckMacro,
                    GuardedResourceId = r.CheckNumber,
                    Description = r.CheckName,
                    EngineerName = r.OwnersName,
                    DistanceMm = r.DistanceMm,
                });
            }

            cache.Load(records);

            // NYT move key (from*10000+to) -> MoveId, for Move guard checks
            var moves = await db.Database.SqlQueryRaw<MoveRow>(
                "SELECT MoveId, FromNodeId, ToNodeId FROM Move WHERE IsDeleted = 0")
                .ToListAsync();
            var map = new Dictionary<int, int>();
            foreach (var m in moves)
                map[m.FromNodeId * 10000 + m.ToNodeId] = m.MoveId;
            cache.SetMoveKeys(map);
            logger.LogInformation("Check table: {N} move keys mapped", map.Count);
            logger.LogInformation(
                "Check table: {Rows} rows read, {Types}",
                rows.Count,
                string.Join(", ", records.GroupBy(x => x.CheckType)
                    .Select(g => g.Key + "=" + g.Count())));
        }

        private sealed class MoveRow
        {
            public int MoveId { get; set; }
            public int FromNodeId { get; set; }
            public int ToNodeId { get; set; }
        }

        private sealed class Row
        {
            public int CheckId { get; set; }
            public string? OwnersName { get; set; }
            public string OwnersType { get; set; } = "";
            public int OwnersNumber { get; set; }
            public string CheckType { get; set; } = "";
            public int CheckNumber { get; set; }
            public string? CheckName { get; set; }
            public int Enabled { get; set; }
            public int? DistanceMm { get; set; }
            public string? AplusCheckMacro { get; set; }
            public int CheckOrder { get; set; }
        }
    }
}
