using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Dapper;

namespace AGV.Persistence.Data
{
    /// <summary>
    /// Registers the AgvDbContext with the appropriate database provider
    /// based on the DatabaseProvider configuration key.
    ///
    /// Supported providers:
    ///   "SqlServer" — Microsoft SQL Server (development + production)
    ///   "MySql"     — MySQL via Pomelo (NYT demo/sandbox on Linux)
    ///   "Sqlite"    — SQLite (laptop PoC, zero install required)
    ///
    /// Configuration in appsettings.json:
    /// {
    ///   "DatabaseProvider": "SqlServer",
    ///   "ConnectionStrings": {
    ///     "AgvDatabase": "Server=...;Database=AgvHost;..."
    ///   }
    /// }
    ///
    /// Called once from AGV.Host Program.cs / startup wiring.
    /// Nothing else in the codebase needs to know which provider
    /// is active — EF Core abstracts all differences.
    /// </summary>
    public static class DatabaseProviderRegistration
    {
        public static IServiceCollection AddAgvDatabase(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var provider = configuration["DatabaseProvider"]
                ?? throw new InvalidOperationException(
                    "DatabaseProvider configuration key is required. " +
                    "Valid values: SqlServer, MySql, Sqlite");

            var connectionString = configuration
                .GetConnectionString("AgvDatabase")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:AgvDatabase is required.");

            services.AddDbContext<AgvDbContext>(options =>
            {
                switch (provider.Trim())
                {
                    case "SqlServer":
                        options.UseSqlServer(
                            connectionString,
                            sql => sql.CommandTimeout(60));
                        break;

                    case "MySql":
                        options.UseMySql(
                            connectionString,
                            ServerVersion.AutoDetect(connectionString),
                            mysql => mysql.CommandTimeout(60));
                        break;

                    case "Sqlite":
                        options.UseSqlite(
                            connectionString,
                            sqlite => sqlite.CommandTimeout(60));
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unknown DatabaseProvider: '{provider}'. " +
                            $"Valid values: SqlServer, MySql, Sqlite");
                }

                // Development helpers — disable in production
#if DEBUG
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
#endif
            });

            return services;
        }

        /// <summary>
        /// Applies pending migrations and ensures the database exists.
        /// Called at host startup after the DI container is built.
        /// Safe to call on every startup — EF Core tracks applied
        /// migrations and only applies new ones.
        /// </summary>
        public static async Task ApplyMigrationsAsync(
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken = default)
        {
            using var scope = serviceProvider.CreateScope();
            var context = scope.ServiceProvider
                .GetRequiredService<AgvDbContext>();

            await context.Database.MigrateAsync(cancellationToken);

            // Register Dapper type handlers for SQLite numeric affinity.
            // SQLite stores whole-number decimals as INTEGER (Int64) —
            // these handlers coerce them back to decimal/bool correctly.
            SqlMapper.AddTypeHandler(new SqliteDecimalTypeHandler());
            SqlMapper.AddTypeHandler(new SqliteBoolTypeHandler());
        }

        private sealed class SqliteDecimalTypeHandler
            : SqlMapper.TypeHandler<decimal>
        {
            public override decimal Parse(object value)
                => Convert.ToDecimal(value);

            public override void SetValue(
                System.Data.IDbDataParameter parameter, decimal value)
                => parameter.Value = value;
        }

        private sealed class SqliteBoolTypeHandler
            : SqlMapper.TypeHandler<bool>
        {
            public override bool Parse(object value)
                => Convert.ToBoolean(value);

            public override void SetValue(
                System.Data.IDbDataParameter parameter, bool value)
                => parameter.Value = value ? 1 : 0;
        }
    }
}
