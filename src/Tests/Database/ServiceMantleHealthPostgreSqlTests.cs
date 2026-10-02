using System.Net;
using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Quaestura.Host;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Migration;
using ServiceMantle.Persistence.Relational;
using ServiceMantle.Database.PostgreSql;
using Quaestura.Database;

namespace Quaestura.Tests.Database;

/// <summary>
/// The real PostgreSQL evidence for the ServiceMantle health probes: against a fully migrated
/// database the readiness snapshot is ready with the complete evidence tuple and performs zero
/// writes; a server-side disconnection makes every new probe not-ready with the fixed safe
/// error code while recovery restores readiness; a blocked real query observes the caller's
/// cancellation on the original token; concurrent evaluations through independent contexts all
/// stay ready; and the real host serves the three anonymous probe routes with the library JSON.
/// </summary>
[Collection(MigrationIntegrationCollection.Name)]
public sealed class ServiceMantleHealthPostgreSqlTests : IAsyncLifetime
{
    private readonly PostgreSqlMigrationFixture _fixture;
    private string _database = null!;

    public ServiceMantleHealthPostgreSqlTests(PostgreSqlMigrationFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _database = await _fixture.CreateDatabaseAsync();
        await using var context = _fixture.CreateContext(_database);
        await MigrationGoldenStates.ApplyAllMigrationsAsync(context);
    }

    public async Task DisposeAsync() => await _fixture.DropDatabaseAsync(_database);

    private EfCoreHealthSnapshotSource<QuaesturaDbContext> CreateSucceededSource(string? connectionString = null)
    {
        var context = connectionString is null
            ? _fixture.CreateContext(_database)
            : _fixture.CreateContextWithConnectionString(connectionString);
        var state = new StartupDatabaseReceipt();
        state.TryMarkRunning();
        state.TryCompleteSucceeded();
        return new EfCoreHealthSnapshotSource<QuaesturaDbContext>(state, context, new PostgreSqlDatabaseProbeFailureClassifier(), "health");
    }

    [Fact]
    public async Task MigratedDatabase_SnapshotIsReadyWithFullEvidence_AndWritesNothing()
    {
        using var context = _fixture.CreateContext(_database);
        var state = new StartupDatabaseReceipt();
        state.TryMarkRunning();
        state.TryCompleteSucceeded();
        var source = new EfCoreHealthSnapshotSource<QuaesturaDbContext>(state, context, new PostgreSqlDatabaseProbeFailureClassifier(), "health");

        var stateBefore = await MigrationGoldenStates.CaptureStateAsync(context);
        var snapshot = await source.GetSnapshotAsync();
        var stateAfter = await MigrationGoldenStates.CaptureStateAsync(context);

        snapshot.Phase.Should().Be(ServiceStartupPhase.Completed);
        snapshot.MigrationStatus.Should().Be(ServiceMigrationReadinessState.Succeeded);
        snapshot.DatabaseStatus.Should().Be(ServiceDatabaseReadinessState.Reachable);
        snapshot.ErrorCode.Should().BeNull();
        ServiceHealthEvaluator.Evaluate(snapshot).IsReady.Should().BeTrue();

        // The per-request evidence is strictly read-only: no DDL, no DML, no history writes.
        stateAfter.Should().Be(stateBefore);
    }

    [Fact]
    public async Task DisconnectedDatabase_NewProbesNotReady_RecoveryRestartsReady_StateUntouched()
    {
        using var baseline = _fixture.CreateContext(_database);
        var stateBefore = await MigrationGoldenStates.CaptureStateAsync(baseline);

        var connectionString = _fixture.GetConnectionString(_database);
        await _fixture.StopServerAsync();
        ServiceHealthSnapshot disconnected;
        try
        {
            // A brand-new evaluation (new context, new connection) must observe the failure:
            // no stale readiness is cached across requests.
            disconnected = await CreateSucceededSource(connectionString).GetSnapshotAsync();
        }
        finally
        {
            await _fixture.RestartServerAsync();
        }

        disconnected.DatabaseStatus.Should().Be(ServiceDatabaseReadinessState.Unreachable);
        disconnected.ErrorCode.Should().Be("health.database_unreachable");
        ServiceHealthEvaluator.Evaluate(disconnected).IsReady.Should().BeFalse();
        disconnected.Phase.Should().Be(ServiceStartupPhase.Completed);
        disconnected.MigrationStatus.Should().Be(ServiceMigrationReadinessState.Succeeded);

        // After recovery, the next request — again a new context and connection — is ready.
        var recovered = await CreateSucceededSource().GetSnapshotAsync();
        recovered.DatabaseStatus.Should().Be(ServiceDatabaseReadinessState.Reachable);
        recovered.ErrorCode.Should().BeNull();
        ServiceHealthEvaluator.Evaluate(recovered).IsReady.Should().BeTrue();

        using var verify = _fixture.CreateContext(_database);
        var stateAfter = await MigrationGoldenStates.CaptureStateAsync(verify);
        stateAfter.Should().Be(stateBefore, "the probes never write DDL, DML, or history rows");
    }

    [Fact]
    public async Task FailedAuthentication_FixedSafeCodeOnly_NoSecretsInSnapshotOrLog()
    {
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));
        var state = new StartupDatabaseReceipt();
        state.TryMarkRunning();
        state.TryCompleteSucceeded();
        using var context = _fixture.CreateContextWithConnectionString(
            _fixture.GetInvalidPasswordConnectionString(_database));
        var source = new EfCoreHealthSnapshotSource<QuaesturaDbContext>(state, context, new PostgreSqlDatabaseProbeFailureClassifier(), "health");

        // SQLSTATE class 28 is deliberately unclassified by the shared source. The HTTP
        // endpoint projects health.probe_failed with null evidence (covered below).
        await FluentActions.Awaiting(() => source.GetSnapshotAsync().AsTask())
            .Should().ThrowAsync<PostgresException>();
        loggerProvider.Messages.Should().BeEmpty("the shared source does not log driver details");
    }

    [Fact]
    public async Task CallerCancellation_DuringBlockedRealQuery_PropagatesOnOriginalToken()
    {
        // Hold an ACCESS EXCLUSIVE lock on the probe's business table so the real read-only
        // query genuinely blocks inside PostgreSQL.
        var lockHolder = _fixture.CreateContext(_database);
        await lockHolder.Database.OpenConnectionAsync();
        await using var transaction = await lockHolder.Database.BeginTransactionAsync();
        await lockHolder.Database.ExecuteSqlRawAsync("LOCK TABLE tag IN ACCESS EXCLUSIVE MODE");

        using var probeContext = _fixture.CreateContext(_database);
        var state = new StartupDatabaseReceipt();
        state.TryMarkRunning();
        state.TryCompleteSucceeded();
        var source = new EfCoreHealthSnapshotSource<QuaesturaDbContext>(state, probeContext, new PostgreSqlDatabaseProbeFailureClassifier(), "health");
        using var cts = new CancellationTokenSource();

        var probe = source.GetSnapshotAsync(cts.Token).AsTask();
        try
        {
            // Observe the real server state instead of guessing timing: wait until the probe
            // query is registered as actively waiting on the lock.
            var observedBlocked = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!timeout.IsCancellationRequested)
            {
                await using var watcher =
                    new NpgsqlConnection(_fixture.GetConnectionString("postgres"));
                await watcher.OpenAsync(timeout.Token);
                await using var command = watcher.CreateCommand();
                command.CommandText = """
                    SELECT count(*)
                    FROM pg_stat_activity
                    WHERE datname = @database AND wait_event_type = 'Lock' AND state = 'active'
                    """;
                command.Parameters.AddWithValue("@database", _database);
                var waiting = (long)(await command.ExecuteScalarAsync(timeout.Token))!;
                if (waiting > 0)
                {
                    observedBlocked = true;
                    break;
                }

                await Task.Delay(50, timeout.Token);
            }

            observedBlocked.Should().BeTrue("the probe query must be blocked on the table lock");
            cts.Cancel();

            var assertion = await FluentActions.Awaiting(() => probe).Should()
                .ThrowAsync<OperationCanceledException>();
            assertion.Which.CancellationToken.Should().Be(cts.Token,
                "cancellation must propagate on the caller's own token, not a faked 200/503");
        }
        finally
        {
            await transaction.RollbackAsync();
            await lockHolder.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentEvaluations_IndependentContexts_AllReady_NoWrites()
    {
        const int parallelism = 8;
        using var barrier = new Barrier(parallelism);
        var state = new StartupDatabaseReceipt();
        state.TryMarkRunning();
        state.TryCompleteSucceeded();
        var contexts = Enumerable.Range(0, parallelism)
            .Select(_ => _fixture.CreateContext(_database))
            .ToArray();
        var sources = contexts
            .Select(context => new EfCoreHealthSnapshotSource<QuaesturaDbContext>(state, context, new PostgreSqlDatabaseProbeFailureClassifier(), "health"))
            .ToArray();

        try
        {
            var stateBefore =
                await MigrationGoldenStates.CaptureStateAsync(_fixture.CreateContext(_database));

            var snapshots = await Task.WhenAll(Enumerable.Range(0, parallelism).Select(i => Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(30)).Should().BeTrue();
                // Each concurrent evaluation goes through its own context and connection.
                return await sources[i].GetSnapshotAsync();
            })));

            foreach (var snapshot in snapshots)
            {
                snapshot.Phase.Should().Be(ServiceStartupPhase.Completed);
                snapshot.MigrationStatus.Should().Be(ServiceMigrationReadinessState.Succeeded);
                snapshot.DatabaseStatus.Should().Be(ServiceDatabaseReadinessState.Reachable);
                snapshot.ErrorCode.Should().BeNull();
                ServiceHealthEvaluator.Evaluate(snapshot).IsReady.Should().BeTrue();
            }

            var stateAfter =
                await MigrationGoldenStates.CaptureStateAsync(_fixture.CreateContext(_database));
            stateAfter.Should().Be(stateBefore);
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task EndpointBudget_CancelsBlockedMappedSchemaQuery_WithProbeTimeout()
    {
        await using var holder = _fixture.CreateContext(_database);
        await holder.Database.OpenConnectionAsync();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync("LOCK TABLE tag IN ACCESS EXCLUSIVE MODE");
        try
        {
            using var baseFactory = new Quaestura.Tests.Authentication.QuaesturaApiFactory();
            using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Scoped<IServiceHealthSnapshotSource>(_ => CreateSucceededSource()))));
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/health/ready");
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            json.GetProperty("errorCode").GetString().Should().Be("health.probe_timeout");
            foreach (var field in new[] { "phase", "migrationStatus", "databaseStatus" })
                json.GetProperty(field).ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        }
        finally { await transaction.RollbackAsync(); }
    }

    [Theory]
    [InlineData("ALTER TABLE question_content RENAME TO unavailable_content")]
    [InlineData("ALTER TABLE tag RENAME COLUMN name TO unavailable_name")]
    public async Task MappedSchema_MissingTableOrColumn_IsReachableButNotReady(string sql)
    {
        using var context = _fixture.CreateContext(_database);
        await context.Database.ExecuteSqlRawAsync(sql);
        var receipt = new StartupDatabaseReceipt();
        receipt.TryMarkRunning(); receipt.TryCompleteSucceeded();
        var source = new EfCoreHealthSnapshotSource<QuaesturaDbContext>(receipt, context,
            new PostgreSqlDatabaseProbeFailureClassifier(), "health");
        var snapshot = await source.GetSnapshotAsync();
        snapshot.ErrorCode.Should().Be("health.schema_unavailable");
        snapshot.Phase.Should().Be(ServiceStartupPhase.Completed);
        snapshot.MigrationStatus.Should().Be(ServiceMigrationReadinessState.Failed);
        snapshot.DatabaseStatus.Should().Be(ServiceDatabaseReadinessState.Reachable);
        receipt.State.Should().Be(ServiceMigrationReadinessState.Succeeded);
        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task MappedSchema_RevokedSelect_IsSchemaUnavailable()
    {
        var role = $"health_read_{Guid.NewGuid():N}";
        using var admin = _fixture.CreateContext(_database);
        // Synthetic role, per-run fixture credentials. SQL and credentials are never logged.
        await using var connection = new NpgsqlConnection(_fixture.GetConnectionString(_database));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE ROLE \"{role}\" LOGIN PASSWORD '{_fixture.PasswordCanary}'; GRANT CONNECT ON DATABASE \"{_database}\" TO \"{role}\"; GRANT USAGE ON SCHEMA public TO \"{role}\"; GRANT SELECT ON ALL TABLES IN SCHEMA public TO \"{role}\"; REVOKE SELECT ON question_content FROM \"{role}\";";
        await command.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(_fixture.GetConnectionString(_database)) { Username = role, Pooling = false };
        var snapshot = await CreateSucceededSource(builder.ConnectionString).GetSnapshotAsync();
        snapshot.ErrorCode.Should().Be("health.schema_unavailable");
        snapshot.DatabaseStatus.Should().Be(ServiceDatabaseReadinessState.Reachable);
        snapshot.MigrationStatus.Should().Be(ServiceMigrationReadinessState.Failed);
    }

    [Fact]
    public async Task RealAuthenticationFailure_EndpointReturnsOnlySafeNullEvidence()
    {
        using var factory = new Quaestura.Tests.Authentication.QuaesturaApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<IServiceHealthSnapshotSource>(
                _ => CreateSucceededSource(_fixture.GetInvalidPasswordConnectionString(_database))))));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(_fixture.PasswordCanary).And.NotContain("password authentication failed");
        var json = System.Text.Json.JsonDocument.Parse(body).RootElement;
        json.GetProperty("errorCode").GetString().Should().Be("health.probe_failed");
        foreach (var field in new[] { "phase", "migrationStatus", "databaseStatus" })
            json.GetProperty(field).ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task RealHost_ThreeProbesServeLibraryJsonAfterRealInitialization()
    {
        // A second, untouched migrated copy is not needed here: the host target preparation
        // skips an existing database, so pointing the real Program.cs host at the already
        // migrated database exercises the startup gate with Succeeded and serves the probes.
        var (host, port, username) = _fixture.Server;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Consul:Host", "127.0.0.1");
            builder.UseSetting("Consul:Port", "1");
            builder.UseSetting("Consul:EnableCache", "false");
            builder.UseSetting("PostgreSql:Host", host);
            builder.UseSetting("PostgreSql:Port", port);
            builder.UseSetting("PostgreSql:Username", username);
            builder.UseSetting("PostgreSql:Password", _fixture.PasswordCanary);
            builder.UseSetting("Database:Name", _database);
            builder.UseSetting("Oss:InternalEndpoint", "127.0.0.1:1");
            builder.UseSetting("Oss:PublicBaseUrl", "https://oss.example.com/oss");
        });

        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        live.StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var path in new[] { "/health/ready", "/health" })
        {
            using var ready = await client.GetAsync(path);
            ready.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = System.Text.Json.JsonDocument
                .Parse(await ready.Content.ReadAsStringAsync()).RootElement.Clone();
            body.GetProperty("status").GetString().Should().Be("ready");
            body.GetProperty("phase").GetString().Should().Be("completed");
            body.GetProperty("migrationStatus").GetString().Should().Be("succeeded");
            body.GetProperty("databaseStatus").GetString().Should().Be("reachable");
            body.GetProperty("errorCode").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        }
    }
}
