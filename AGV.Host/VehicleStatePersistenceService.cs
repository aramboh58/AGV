using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AGV.Host
{
    /// <summary>
    /// Simulation:PersistVehicleState = true
    ///   While running, saves each vehicle's last reported node and battery
    ///   level into the VehicleSavedState table (every 10 s and on shutdown).
    ///   On the next startup the vehicles are put back on those nodes
    ///   (RestoreAsync, called from Program.cs right after the registry is
    ///   loaded).
    ///
    /// Persistence off, or no saved state yet (first start):
    ///   vehicles start on the initialization positions in the Vehicle table.
    ///   That table is never written here.
    ///
    /// No two vehicles are saved on the same node (lowest vehicle id wins;
    /// the other keeps its previous saved row).
    /// </summary>
    public sealed class VehicleStatePersistenceService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

        private const string CreateTableSql =
            "CREATE TABLE IF NOT EXISTS VehicleSavedState (" +
            "VehicleId INTEGER NOT NULL PRIMARY KEY, " +
            "NodeId INTEGER NOT NULL, " +
            "BatteryStateOfCharge NUMERIC NOT NULL, " +
            "SavedAt TEXT NOT NULL)";

        private readonly IServiceScopeFactory _scopes;
        private readonly AGV.Fleet.Infrastructure.VehicleRegistry _registry;
        private readonly ILogger<VehicleStatePersistenceService> _logger;

        public VehicleStatePersistenceService(
            IServiceScopeFactory scopes,
            AGV.Fleet.Infrastructure.VehicleRegistry registry,
            ILogger<VehicleStatePersistenceService> logger)
        {
            _scopes = scopes;
            _registry = registry;
            _logger = logger;
        }

        // ----------------------------------------------------------------
        // Restore (startup, before the vehicle adapter starts)
        // ----------------------------------------------------------------

        /// <summary>
        /// Puts every vehicle that has a saved row back on its saved node
        /// with its saved battery level. Vehicles without a saved row keep
        /// their initialization position.
        /// </summary>
        public static async Task RestoreAsync(
            AGV.Persistence.Data.AgvDbContext db,
            AGV.Fleet.Infrastructure.VehicleRegistry registry,
            ILogger logger)
        {
            await db.Database.ExecuteSqlRawAsync(CreateTableSql);

            var rows = await db.Database
                .SqlQueryRaw<SavedRow>(
                    "SELECT VehicleId, NodeId, " +
                    "CAST(BatteryStateOfCharge AS REAL) AS Soc " +
                    "FROM VehicleSavedState")
                .ToListAsync();

            if (rows.Count == 0)
            {
                logger.LogInformation(
                    "Vehicle state restore: nothing saved yet - " +
                    "vehicles start on their initialization positions");
                return;
            }

            var restored = 0;
            foreach (var row in rows)
            {
                var v = registry.GetAll()
                    .FirstOrDefault(x => x.VehicleId == row.VehicleId);
                if (v is null || row.NodeId <= 0) continue;

                v.UpdatePosition(row.NodeId, v.CurrentMapId);
                v.UpdateBattery((decimal)row.Soc);
                restored++;
            }

            logger.LogInformation(
                "Vehicle state restore: {Count} vehicles restored to their " +
                "last reported node", restored);
        }

        private sealed class SavedRow
        {
            public int VehicleId { get; set; }
            public int NodeId { get; set; }
            public double Soc { get; set; }
        }

        // ----------------------------------------------------------------
        // Save (while running and on shutdown)
        // ----------------------------------------------------------------

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "Vehicle state persistence ON - vehicles restart on their last node");
            try
            {
                await Task.Delay(Interval, stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await SaveAsync();
                    await Task.Delay(Interval, stoppingToken);
                }
            }
            catch (OperationCanceledException) { }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            await SaveAsync();   // final save
        }

        private async Task SaveAsync()
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider
                    .GetRequiredService<AGV.Persistence.Data.AgvDbContext>();

                var now = DateTime.UtcNow.ToString("O");
                var claimed = new HashSet<int>();

                foreach (var v in _registry.GetAll().OrderBy(x => x.VehicleId))
                {
                    if (!(v.CurrentNodeId is int node) || node <= 0) continue;
                    if (!claimed.Add(node)) continue;   // one vehicle per node

                    var id = v.VehicleId;
                    var soc = v.BatteryStateOfCharge;

                    var updated = await db.Database.ExecuteSqlInterpolatedAsync(
                        $@"UPDATE VehicleSavedState
                           SET NodeId = {node}, BatteryStateOfCharge = {soc}, SavedAt = {now}
                           WHERE VehicleId = {id}");
                    if (updated == 0)
                        await db.Database.ExecuteSqlInterpolatedAsync(
                            $@"INSERT INTO VehicleSavedState
                               (VehicleId, NodeId, BatteryStateOfCharge, SavedAt)
                               VALUES ({id}, {node}, {soc}, {now})");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vehicle state save failed");
            }
        }
    }
}
