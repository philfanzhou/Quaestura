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

    private QuaesturaHealthSnapshotSource CreateSucceededSource(string? connectionString = null)
    {
        var context = connectionString is null
            ? _fixture.CreateContext(_database)
            : _fixture.CreateContextWithConnectionString(connectionString);
        var state = new QuaesturaStartupHealthState();
        state.RecordRunning();
        state.RecordSucceeded();
        return new QuaesturaHealthSnapshotSource(context, state);
    }

    private async Task SetAllowConnectionsAsync(bool allowed)
    {
        await using var connection = new NpgsqlConnection(_fixture.GetConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER DATABASE \"{_database}\" WITH ALLOW_CONNECTIONS {(allowed ? "true" : "false")}";
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task MigratedDatabase_SnapshotIsReadyWithFullEvidence_AndWritesNothing()
    {
        using var context = _fixture.CreateContext(_database);
        var state = new QuaesturaStartupHealthState();
        state.RecordRunning();
        state.RecordSucceeded();
        var source = new QuaesturaHealthSnapshotSource(context, state);

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

        await SetAllowConnectionsAsync(false);
        ServiceHealthSnapshot disconnected;
        try
        {
            // A brand-new evaluation (new context, new connection) must observe the failure:
            // no stale readiness is cached across requests.
            disconnected = await CreateSucceededSource().GetSnapshotAsync();
        }
        finally
        {
            await SetAllowConnectionsAsync(true);
        }

        disconnected.DatabaseStatus.Should().Be(ServiceDatabaseReadinessState.Unreachable);
        disconnected.ErrorCode.Should().Be(QuaesturaHealthSnapshotSource.DatabaseUnreachableErrorCode);
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
        var state = new QuaesturaStartupHealthState();
        state.RecordRunning();
        state.RecordSucceeded();
        using var context = _fixture.CreateContextWithConnectionString(
            _fixture.GetInvalidPasswordConnectionString(_database));
        var source = new QuaesturaHealthSnapshotSource(
            context, state, loggerFactory.CreateLogger<QuaesturaHealthSnapshotSource>());

        var snapshot = await source.GetSnapshotAsync();

        // Auth failures collapse into the same fixed safe classification as any other
        // unreachable state; the snapshot never carries driver details.
        snapshot.DatabaseStatus.Should().Be(ServiceDatabaseReadinessState.Unreachable);
        snapshot.ErrorCode.Should().Be(QuaesturaHealthSnapshotSource.DatabaseUnreachableErrorCode);
        ServiceHealthEvaluator.Evaluate(snapshot).IsReady.Should().BeFalse();
        snapshot.ToString().Should()
            .NotContain(_fixture.PasswordCanary)
            .And.NotContain("Npgsql")
            .And.NotContain("Postgres");

        // The local warning log records the exception type name only, never the secret.
        loggerProvider.Messages.Should().ContainSingle();
        loggerProvider.Messages[0].Should().Contain("Health database probe failed");
        loggerProvider.Messages[0].Should().NotContain(_fixture.PasswordCanary);
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
        var state = new QuaesturaStartupHealthState();
        state.RecordRunning();
        state.RecordSucceeded();
        var source = new QuaesturaHealthSnapshotSource(probeContext, state);
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
        var state = new QuaesturaStartupHealthState();
        state.RecordRunning();
        state.RecordSucceeded();
        var contexts = Enumerable.Range(0, parallelism)
            .Select(_ => _fixture.CreateContext(_database))
            .ToArray();
        var sources = contexts
            .Select(context => new QuaesturaHealthSnapshotSource(context, state))
            .ToArray();

        try
        {
            var stateBefore =
                await MigrationGoldenStates.CaptureStateAsync(_fixture.CreateContext(_database));

            var snapshots = await Task.WhenAll(Enumerable.Range(0, parallelism).Select(async i =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(30));
                // Each concurrent evaluation goes through its own context and connection.
                return await sources[i].GetSnapshotAsync();
            }));

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
