using System.Globalization;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Quaestura.Tests.Database;

/// <summary>
/// One shared PostgreSQL 16 container for the target-preparation collection. Every test uses
/// unique database and role names inside the container, which is destroyed at the end of the
/// collection. The tests run on any Docker-capable machine and in GitHub-hosted CI without being
/// skipped by default.
/// </summary>
public sealed class PostgreSqlTargetPreparationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;

    /// <summary>
    /// A random per-run secret used as the superuser password. Tests assert this canary never
    /// appears in exceptions or log output. It is generated per fixture and is not a credential
    /// of any real environment.
    /// </summary>
    public string PasswordCanary { get; } = $"canary-{Guid.NewGuid():N}";

    /// <summary>The container superuser, matching the configured-username production shape.</summary>
    public string Username => "quaestura_target";

    public PostgreSqlTargetPreparationFixture()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("postgres")
            .WithUsername(Username)
            .WithPassword(PasswordCanary)
            .Build();
    }

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public (string Host, string Port) Server
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
            return (builder.Host!, builder.Port.ToString(CultureInfo.InvariantCulture));
        }
    }

    public string GetConnectionString(
        string database, string? username = null, string? password = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Username = username ?? Username,
            Password = password ?? PasswordCanary,
            Pooling = false,
        };
        return builder.ConnectionString;
    }

    /// <summary>A connection string pointing at a closed local port (fast connection refusal).</summary>
    public string GetUnreachableConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 1,
            Database = database,
            Username = Username,
            Password = PasswordCanary,
            Pooling = false,
            Timeout = 2,
        }.ConnectionString;

    public string NewDatabaseName() => $"target_{Guid.NewGuid():N}";

    public string NewRoleName(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    public async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public Task ExecuteAdminAsync(string sql) =>
        ExecuteAsync(GetConnectionString("postgres"), sql);

    public async Task<string> CreateDatabaseAsync(string? owner = null)
    {
        var name = NewDatabaseName();
        await ExecuteAdminAsync(
            owner is null
                ? $"CREATE DATABASE \"{name}\""
                : $"CREATE DATABASE \"{name}\" OWNER \"{owner}\"");
        return name;
    }

    public async Task DropDatabaseIfExistsAsync(string database)
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

    public async Task CreateRoleAsync(string name, string password, bool createDb)
    {
        var safePassword = password.Replace("'", "''");
        await ExecuteAdminAsync(
            $"CREATE ROLE \"{name}\" LOGIN {(createDb ? "CREATEDB" : "NOCREATEDB")} PASSWORD '{safePassword}'");
    }

    public async Task<bool> DatabaseExistsAsync(string database)
    {
        await using var connection = new NpgsqlConnection(GetConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name)";
        command.Parameters.AddWithValue("@name", database);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    public async Task<string?> GetDatabaseOwnerAsync(string database)
    {
        await using var connection = new NpgsqlConnection(GetConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @name";
        command.Parameters.AddWithValue("@name", database);
        var owner = await command.ExecuteScalarAsync();
        return owner?.ToString();
    }

    public async Task<long> CountRowsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{table}\"";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>
/// All container-backed target-preparation tests live in this single collection: xUnit runs
/// collections in parallel, and grouping them keeps concurrent hosts from racing container
/// startup. The rest of the suite still runs without Docker.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TargetPreparationIntegrationCollection : ICollectionFixture<PostgreSqlTargetPreparationFixture>
{
    public const string Name = "target-preparation-integration";
}
