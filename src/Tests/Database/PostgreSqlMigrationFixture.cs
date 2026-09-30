using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Quaestura.Database;
using Testcontainers.PostgreSql;
using Xunit;

namespace Quaestura.Tests.Database;

/// <summary>
/// One shared PostgreSQL 16 container for the migration integration collection. Every test gets
/// its own database inside the container, so the golden states never interfere. The tests run on
/// any Docker-capable machine and in GitHub-hosted CI without being skipped by default.
/// </summary>
public sealed class PostgreSqlMigrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;

    /// <summary>
    /// A random per-run secret used as the container password. Tests assert this canary never
    /// appears in exception messages, log output, or inspection results. It is generated per
    /// fixture and is not a credential of any real environment.
    /// </summary>
    public string PasswordCanary { get; } = $"canary-{Guid.NewGuid():N}";

    public PostgreSqlMigrationFixture()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("postgres")
            .WithUsername("quaestura_migrator")
            .WithPassword(PasswordCanary)
            .Build();
    }

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// A connection string for one database inside the container, pooling disabled so drops and
    /// catalog checks are never masked by pooled physical connections.
    /// </summary>
    public string GetConnectionString(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Pooling = false,
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// The same connection string with a wrong password, for authentication-failure cases.
    /// </summary>
    public string GetInvalidPasswordConnectionString(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(GetConnectionString(database))
        {
            Password = $"wrong-{PasswordCanary}",
        };
        return builder.ConnectionString;
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"mig_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(GetConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{name}\"";
        await command.ExecuteNonQueryAsync();
        return name;
    }

    /// <summary>
    /// The container's server coordinates in the shape <c>SharedPostgreSqlConnectionStringFactory</c>
    /// reads, for host-level startup tests.
    /// </summary>
    public (string Host, string Port, string Username) Server
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
            return (
                builder.Host!,
                builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                builder.Username!);
        }
    }

    public async Task DropDatabaseAsync(string database)
    {
        await using var connection = new NpgsqlConnection(GetConnectionString("postgres"));
        await connection.OpenAsync();
        await using var terminate = connection.CreateCommand();
        terminate.CommandText = """
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = @name AND pid <> pg_backend_pid()
            """;
        terminate.Parameters.AddWithValue("@name", database);
        await terminate.ExecuteNonQueryAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{database}\"";
        await drop.ExecuteNonQueryAsync();
    }

    public QuaesturaDbContext CreateContext(string database) =>
        new(new DbContextOptionsBuilder<QuaesturaDbContext>()
            .UseNpgsql(GetConnectionString(database))
            .Options);

    public QuaesturaDbContext CreateContextWithConnectionString(string connectionString) =>
        new(new DbContextOptionsBuilder<QuaesturaDbContext>()
            .UseNpgsql(connectionString)
            .Options);
}

/// <summary>
/// All container-backed migration tests live in this single collection: xUnit runs collections in
/// parallel, and grouping them keeps concurrent hosts from racing container startup. The rest of
/// the suite still runs without Docker.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MigrationIntegrationCollection : ICollectionFixture<PostgreSqlMigrationFixture>
{
    public const string Name = "migration-integration";
}

/// <summary>
/// Builds the golden known-version database states used across the migration tests: migrated
/// (current), Initial-history-only, EnsureCreated legacy with and without the tag tables, and the
/// historical question(id)-only corruption reproduction.
/// </summary>
internal static class MigrationGoldenStates
{
    internal const string InitialId = QuaesturaMigrationExecutor.InitialCreateMigrationId;
    internal const string AddTagsId = QuaesturaMigrationExecutor.AddTagsMigrationId;
    internal const string HistoryTable = QuaesturaMigrationExecutor.HistoryTableName;

    internal static readonly Guid QuestionId = Guid.Parse("a1111111-1111-4111-8111-111111111111");
    internal static readonly Guid KnowledgeId = Guid.Parse("b2222222-2222-4222-8222-222222222222");
    internal static readonly Guid QuestionKnowledgeId = Guid.Parse("c3333333-3333-4333-8333-333333333333");
    internal static readonly Guid TagId = Guid.Parse("d4444444-4444-4444-8444-444444444444");
    internal static readonly Guid QuestionTagId = Guid.Parse("e5555555-5555-4555-8555-555555555555");
    internal const int LegacyUsageCount = 42;

    internal static Task ApplyAllMigrationsAsync(QuaesturaDbContext context) =>
        context.Database.MigrateAsync();

    internal static async Task ApplyInitialOnlyAsync(QuaesturaDbContext context)
    {
        var migrator = ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)context)
            .Instance
            .GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync(InitialId);
    }

    internal static Task EnsureCreatedLegacyAsync(QuaesturaDbContext context) =>
        context.Database.EnsureCreatedAsync();

    internal static Task DropHistoryAsync(QuaesturaDbContext context) =>
        context.Database.ExecuteSqlRawAsync($"DROP TABLE IF EXISTS \"{HistoryTable}\"");

    internal static Task ExecuteRawAsync(QuaesturaDbContext context, string sql) =>
        context.Database.ExecuteSqlRawAsync(sql);

    /// <summary>The historical corruption reproduction: a lone question(id) table.</summary>
    internal static Task CreateQuestionIdOnlyAsync(QuaesturaDbContext context) =>
        ExecuteRawAsync(context, "CREATE TABLE question (id uuid PRIMARY KEY)");

    /// <summary>Synthetic business rows for the four initial tables.</summary>
    internal static Task SeedInitialEraRowsAsync(QuaesturaDbContext context) =>
        ExecuteRawAsync(context, $"""
            INSERT INTO question (id, created_at, updated_at, level, type, width, height,
                                  picture_paths, user_id, student_id, mistake_id, subject, grade)
            VALUES ('{QuestionId}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 1, 2, 640, 480,
                    NULL, 'legacy-user', NULL, NULL, 2, 3);
            INSERT INTO question_content (question_id, content, correct_answer, analysis)
            VALUES ('{QuestionId}', 'legacy question content', 'legacy answer', NULL);
            INSERT INTO knowledge (id, parent_id, name, description, created_by, created_at,
                                   is_referenced, subject, grade, updated_by, updated_at)
            VALUES ('{KnowledgeId}', NULL, 'legacy knowledge', NULL, 'legacy-user',
                    '2026-01-01T00:00:00Z', false, 2, 3, NULL, NULL);
            INSERT INTO question_knowledge (id, question_id, knowledge_id, weight)
            VALUES ('{QuestionKnowledgeId}', '{QuestionId}', '{KnowledgeId}', 0.75);
            """);

    /// <summary>Synthetic tag-era rows (requires the tag tables to exist).</summary>
    internal static Task SeedTagEraRowsAsync(QuaesturaDbContext context) =>
        ExecuteRawAsync(context, $"""
            INSERT INTO tag (id, name, color, description, created_by, created_at, usage_count)
            VALUES ('{TagId}', 'legacy-tag', '#FF6B6B', NULL, 'legacy-user',
                    '2026-01-01T00:00:00Z', {LegacyUsageCount});
            INSERT INTO question_tag (id, question_id, tag_id, created_at)
            VALUES ('{QuestionTagId}', '{QuestionId}', '{TagId}', '2026-01-01T00:00:00Z');
            """);

    /// <summary>
    /// A canonical, secret-free snapshot of the database state (tables, history rows, and row
    /// counts) used to prove rejected classifications performed zero writes.
    /// </summary>
    internal static async Task<string> CaptureStateAsync(QuaesturaDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            var state = new List<string>();

            await using (var tables = connection.CreateCommand())
            {
                tables.CommandText = """
                    SELECT c.relname
                    FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public' AND c.relkind = 'r'
                    ORDER BY c.relname
                    """;
                await using var reader = await tables.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    state.Add($"table:{reader.GetString(0)}");
                }
            }

            if (state.Contains($"table:{HistoryTable}"))
            {
                await using var history = connection.CreateCommand();
                history.CommandText =
                    $"SELECT \"MigrationId\" FROM \"{HistoryTable}\" ORDER BY \"MigrationId\"";
                await using var reader = await history.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    state.Add($"history:{reader.GetString(0)}");
                }
            }

            foreach (var table in QuaesturaMigrationExecutor.KnownTableNames)
            {
                if (!state.Contains($"table:{table}"))
                {
                    continue;
                }

                await using var count = connection.CreateCommand();
                count.CommandText = $"SELECT count(*) FROM \"{table}\"";
                var rows = await count.ExecuteScalarAsync();
                state.Add($"rows:{table}={rows}");
            }

            return string.Join("|", state);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    internal static async Task<List<string>> ReadAppliedHistoryAsync(QuaesturaDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT \"MigrationId\" FROM \"{HistoryTable}\" ORDER BY \"MigrationId\"";
            var ids = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetString(0));
            }

            return ids;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    internal static async Task<long> CountRowsAsync(QuaesturaDbContext context, string table)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM \"{table}\"";
            return (long)(await command.ExecuteScalarAsync())!;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    internal static async Task<T?> ScalarAsync<T>(QuaesturaDbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync();
            return value is null or DBNull ? default : (T)value;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    internal static async Task<bool> TableExistsAsync(QuaesturaDbContext context, string table)
    {
        var exists = await ScalarAsync<bool>(
            context,
            $"SELECT to_regclass('public.\"{table}\"') IS NOT NULL");
        return exists;
    }
}

/// <summary>
/// Captures formatted log messages so tests can assert secret canaries never enter log output.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = [];

    internal IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (owner._messages)
            {
                owner._messages.Add(formatter(state, exception) + " " + exception);
            }
        }
    }
}
