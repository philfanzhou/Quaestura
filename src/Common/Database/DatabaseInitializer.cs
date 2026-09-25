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
                logger.LogInformation("数据库不存在，且当前上下文未配置迁移，正在使用 EnsureCreated 自动创建");
                var created = await context.Database.EnsureCreatedAsync();
                logger.LogInformation(created ? "数据库和表已自动创建" : "数据库已存在，无需创建");
                return;
            }

            logger.LogInformation("数据库已是最新状态，无需迁移");
            if (getTableCreationSql != null)
            {
                await EnsureSchemaIntegrityAsync(context, logger, getTableCreationSql);
            }
            return;
        }

        if (!canConnect)
        {
            logger.LogInformation("数据库不存在，将通过迁移自动创建");
            await context.Database.MigrateAsync();
            logger.LogInformation("数据库创建并迁移完成");
            return;
        }

        var appliedMigrations = (await context.Database.GetAppliedMigrationsAsync()).ToList();

        if (appliedMigrations.Count == 0)
        {
            logger.LogInformation("检测到遗留数据库（由 EnsureCreated 创建），正在迁移到 Migrations 模式");

            try
            {
                await context.Database.MigrateAsync();
                logger.LogInformation("遗留数据库迁移完成");
            }
            catch (Exception ex) when (IsDuplicateTableError(ex))
            {
                logger.LogWarning("遗留数据库中已存在部分表，切换到兼容模式");
                await StampMigrationsAsync(context, pendingMigrations, logger);
                if (getTableCreationSql != null)
                {
                    await EnsureSchemaIntegrityAsync(context, logger, getTableCreationSql);
                }
            }
        }
        else
        {
            logger.LogInformation("发现 {Count} 个待应用的数据库迁移：{Migrations}",
                pendingMigrations.Count, string.Join(", ", pendingMigrations));
            await context.Database.MigrateAsync();
            logger.LogInformation("数据库迁移完成");
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

            logger.LogInformation("遗留数据库已标记所有迁移为已应用");
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

        // 按 FK 依赖关系做拓扑排序，被依赖的父表先创建，避免 FK 约束失败（修复 N6）
        var orderedMissingTables = TopologicalSortTables(entityTypes, missingTables);

        logger.LogWarning("检测到缺失的数据库表：{Tables}，正在按 FK 依赖顺序自动修复：{OrderedTables}",
            string.Join(", ", missingTables), string.Join(", ", orderedMissingTables));

        foreach (var tableName in orderedMissingTables)
        {
            var sql = getTableCreationSql(tableName);
            if (sql != null)
            {
                await context.Database.ExecuteSqlRawAsync(sql);
                logger.LogInformation("已创建缺失的表：{Table}", tableName);
            }
            else
            {
                logger.LogError("无法自动创建表 {Table}：缺少建表SQL定义，请手动创建", tableName);
            }
        }
    }

    /// <summary>
    /// 对缺失的表按外键依赖关系做拓扑排序，被依赖的父表排在前面先创建。
    /// 解决 EnsureSchemaIntegrityAsync 按实体类型任意顺序建表导致 FK 约束失败的问题（N6）。
    /// </summary>
    private static List<string> TopologicalSortTables(
        System.Collections.Generic.IEnumerable<Microsoft.EntityFrameworkCore.Metadata.IEntityType> entityTypes,
        List<string> missingTables)
    {
        var missingSet = new HashSet<string>(missingTables);
        var entityTypeByTable = entityTypes
            .Where(e => !string.IsNullOrEmpty(e.GetTableName()))
            .ToDictionary(e => e.GetTableName()!, StringComparer.OrdinalIgnoreCase);

        // 依赖图：tableName -> 依赖的 missing 父表名列表
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
            if (visiting.Contains(table)) return; // 循环依赖，跳过避免死循环
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
