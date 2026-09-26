using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Quaestura.Common.Database;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync<TDbContext>(
        TDbContext context,
        ILoggerFactory loggerFactory,
        Func<string, string?>? getTableCreationSql = null)
        where TDbContext : DbContext
    {
        var logger = loggerFactory.CreateLogger("DatabaseInitializer");

        var pendingMigrations = (await context.Database.GetPendingMigrationsAsync()).ToList();

        var canConnect = await context.Database.CanConnectAsync();
        if (!pendingMigrations.Any())
        {
            if (!canConnect)
            {
                logger.LogInformation("Database does not exist and the current context has no migrations; creating it with EnsureCreated");
                var created = await context.Database.EnsureCreatedAsync();
                logger.LogInformation(created ? "Database and tables created automatically" : "Database already exists; nothing to create");
                return;
            }

            logger.LogInformation("Database is up to date; no migrations to apply");
            if (getTableCreationSql != null)
            {
                await EnsureSchemaIntegrityAsync(context, logger, getTableCreationSql);
            }
            return;
        }

        if (!canConnect)
        {
            logger.LogInformation("Database does not exist; creating it through migrations");
            await context.Database.MigrateAsync();
            logger.LogInformation("Database created and migrated");
            return;
        }

        var appliedMigrations = (await context.Database.GetAppliedMigrationsAsync()).ToList();

        if (appliedMigrations.Count == 0)
        {
            logger.LogInformation("Detected a legacy database (created by EnsureCreated); switching it to migrations");

            try
            {
                await context.Database.MigrateAsync();
                logger.LogInformation("Legacy database migration completed");
            }
            catch (Exception ex) when (IsDuplicateTableError(ex))
            {
                logger.LogWarning("Some tables already exist in the legacy database; switching to compatibility mode");
                await StampMigrationsAsync(context, pendingMigrations, logger);
                if (getTableCreationSql != null)
                {
                    await EnsureSchemaIntegrityAsync(context, logger, getTableCreationSql);
                }
            }
        }
        else
        {
            logger.LogInformation("Found {Count} pending database migrations: {Migrations}",
                pendingMigrations.Count, string.Join(", ", pendingMigrations));
            await context.Database.MigrateAsync();
            logger.LogInformation("Database migration completed");
        }
    }

    private static bool IsDuplicateTableError(Exception ex)
    {
        var typeName = ex.GetType().FullName;
        if (typeName == "Npgsql.PostgresException" && ex.GetType().GetProperty("SqlState")?.GetValue(ex) is string sqlState)
            return sqlState == "42P07";
        return false;
    }

    private static async Task StampMigrationsAsync<TDbContext>(
        TDbContext context, List<string> pendingMigrations, ILogger logger)
        where TDbContext : DbContext
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = context.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId"" TEXT NOT NULL,
                    ""ProductVersion"" TEXT NOT NULL,
                    CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
                )";
            await cmd.ExecuteNonQueryAsync();

            var productVersion = typeof(DbContext).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "8.0.4";

            foreach (var migration in pendingMigrations)
            {
                await using var insertCmd = context.Database.GetDbConnection().CreateCommand();
                insertCmd.CommandText = @"
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES (@MigrationId, @ProductVersion)";

                var migrationIdParam = insertCmd.CreateParameter();
                migrationIdParam.ParameterName = "@MigrationId";
                migrationIdParam.Value = migration;
                insertCmd.Parameters.Add(migrationIdParam);

                var versionParam = insertCmd.CreateParameter();
                versionParam.ParameterName = "@ProductVersion";
                versionParam.Value = productVersion;
                insertCmd.Parameters.Add(versionParam);

                await insertCmd.ExecuteNonQueryAsync();
            }

            logger.LogInformation("Marked all migrations as applied for the legacy database");
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureSchemaIntegrityAsync<TDbContext>(
        TDbContext context, ILogger logger, Func<string, string?> getTableCreationSql)
        where TDbContext : DbContext
    {
        var entityTypes = context.Model.GetEntityTypes();
        var missingTables = new List<string>();

        await context.Database.OpenConnectionAsync();
        try
        {
            foreach (var entityType in entityTypes)
            {
                var tableName = entityType.GetTableName();
                if (string.IsNullOrEmpty(tableName)) continue;

                await using var cmd = context.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = @"
                    SELECT EXISTS (
                        SELECT FROM information_schema.tables
                        WHERE table_schema = 'public'
                        AND table_name = @tableName
                    )";

                var param = cmd.CreateParameter();
                param.ParameterName = "@tableName";
                param.Value = tableName;
                cmd.Parameters.Add(param);

                var result = await cmd.ExecuteScalarAsync();
                var exists = (bool)result!;
                if (!exists)
                {
                    missingTables.Add(tableName);
                }
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        if (!missingTables.Any())
        {
            return;
        }

        // Topologically sort by FK dependencies so referenced parent tables are created first and FK constraints do not fail (fixes N6)
        var orderedMissingTables = TopologicalSortTables(entityTypes, missingTables);

        logger.LogWarning("Detected missing database tables: {Tables}; repairing them in FK dependency order: {OrderedTables}",
            string.Join(", ", missingTables), string.Join(", ", orderedMissingTables));

        foreach (var tableName in orderedMissingTables)
        {
            var sql = getTableCreationSql(tableName);
            if (sql != null)
            {
                await context.Database.ExecuteSqlRawAsync(sql);
                logger.LogInformation("Created missing table: {Table}", tableName);
            }
            else
            {
                logger.LogError("Cannot create table {Table} automatically: no CREATE TABLE SQL is defined; create it manually", tableName);
            }
        }
    }

    /// <summary>
    /// Topologically sorts missing tables by foreign key dependencies so referenced parent tables come first.
    /// Fixes FK constraint failures caused by EnsureSchemaIntegrityAsync creating tables in arbitrary entity-type order (N6).
    /// </summary>
    private static List<string> TopologicalSortTables(
        System.Collections.Generic.IEnumerable<Microsoft.EntityFrameworkCore.Metadata.IEntityType> entityTypes,
        List<string> missingTables)
    {
        var missingSet = new HashSet<string>(missingTables);
        var entityTypeByTable = entityTypes
            .Where(e => !string.IsNullOrEmpty(e.GetTableName()))
            .ToDictionary(e => e.GetTableName()!, StringComparer.OrdinalIgnoreCase);

        // Dependency graph: tableName -> names of the missing parent tables it depends on
        var dependencies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in missingTables)
        {
            var deps = new List<string>();
            if (entityTypeByTable.TryGetValue(table, out var entityType))
            {
                foreach (var fk in entityType.GetForeignKeys())
                {
                    var principalTable = fk.PrincipalEntityType.GetTableName();
                    if (!string.IsNullOrEmpty(principalTable)
                        && missingSet.Contains(principalTable)
                        && !principalTable.Equals(table, StringComparison.OrdinalIgnoreCase))
                    {
                        deps.Add(principalTable);
                    }
                }
            }
            dependencies[table] = deps;
        }

        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string table)
        {
            if (visited.Contains(table)) return;
            if (visiting.Contains(table)) return; // Circular dependency; skip to avoid an infinite loop
            visiting.Add(table);

            if (dependencies.TryGetValue(table, out var deps))
            {
                foreach (var dep in deps)
                {
                    Visit(dep);
                }
            }

            visiting.Remove(table);
            visited.Add(table);
            result.Add(table);
        }

        foreach (var table in missingTables)
        {
            Visit(table);
        }

        return result;
    }
}
