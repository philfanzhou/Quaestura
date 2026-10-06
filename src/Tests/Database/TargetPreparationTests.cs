using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quaestura.Database;
using Quaestura.Host;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle;
using ServiceMantle.Migration;
using ServiceMantle.Health;

namespace Quaestura.Tests.Database;

/// <summary>
/// The target-preparation adapter against real PostgreSQL 16: an existing target is used
/// without any maintenance connection or CREATE; a verifiably missing target is created with
/// the configured name and owner only when Database:AllowCreate permits it, after which the
/// original initializer still produces the six tables and both original history rows;
/// permission, reachability, identity, and owner conflicts refuse startup without creation
/// fallbacks; concurrent preparation converges; cancellation creates nothing; and secret
/// canaries never enter diagnostics.
/// </summary>
[Collection(TargetPreparationIntegrationCollection.Name)]
public sealed class TargetPreparationTests
{
    private readonly PostgreSqlTargetPreparationFixture _fixture;

    public TargetPreparationTests(PostgreSqlTargetPreparationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void SharedDirectComposition_PreservesDefaultCreationAndBudgetsWithoutHostedGate()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQuaesturaServiceMantle();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IDatabaseDeploymentCapabilityProvider>().Should()
            .ContainSingle().Which.Should().BeOfType<PostgreSqlDatabaseDeploymentCapabilityProvider>();
        services.Should().NotContain(descriptor =>
            descriptor.ImplementationType != null && descriptor.ImplementationType.Name == "StartupDatabaseGateHostedService");
        provider.GetRequiredService<StartupDatabaseGate>().Should().NotBeNull();
        provider.GetRequiredService<StartupDatabaseReceipt>().State.Should().Be(ServiceMigrationReadinessState.NotStarted);
        var options = QuaesturaStartupDatabaseOptions.Create(Configuration(),
            "Host=localhost;Database=quaestura;Username=owner");
        options.DeploymentMode.Should().Be(DatabaseDeploymentMode.MultiInstance);
        options.LockWaitBudget.Should().Be(TimeSpan.FromSeconds(30));
        options.PreparationTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.EnableTargetPreparation.Should().BeTrue();
        options.AllowTargetCreation.Should().BeTrue();
        new Npgsql.NpgsqlConnectionStringBuilder(options.MaintenanceConnectionString)
            .Database.Should().Be("postgres");
    }

    [Fact]
    public async Task ExistingDatabase_IsUsedWithoutPreparation_DataAndOwnerUnchanged()
    {
        var database = await _fixture.CreateDatabaseAsync();
        try
        {
            var connectionString = _fixture.GetConnectionString(database);
            await _fixture.ExecuteAsync(connectionString, """
                CREATE TABLE probe (id integer PRIMARY KEY, note text NULL);
                INSERT INTO probe (id, note) VALUES (1, 'pre-existing data');
                """);
            var ownerBefore = await _fixture.GetDatabaseOwnerAsync(database);
            var provider = new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider());

            await PrepareAsync(provider, connectionString);

            provider.PrepareCalls.Should().Be(0, "an existing target must never be prepared or modified");
            provider.ObserveCalls.Should().Be(1);
            (await _fixture.GetDatabaseOwnerAsync(database)).Should().Be(ownerBefore);
            (await _fixture.CountRowsAsync(connectionString, "probe")).Should().Be(1);
        }
        finally
        {
            await _fixture.DropDatabaseIfExistsAsync(database);
        }
    }

    [Fact]
    public async Task MissingDatabase_DefaultAllowCreate_CreatesTargetAndInitializerProducesContract()
    {
        var database = _fixture.NewDatabaseName();
        try
        {
            var connectionString = _fixture.GetConnectionString(database);
            var provider = new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider());

            await PrepareAsync(provider, connectionString);

            provider.PrepareCalls.Should().Be(1);
            (await _fixture.DatabaseExistsAsync(database)).Should().BeTrue();
            (await _fixture.GetDatabaseOwnerAsync(database)).Should().Be(_fixture.Username);

            // The migration orchestration after preparation owns the business tables and history.
            var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<QuaesturaDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            using var context = new QuaesturaDbContext(options);
            var migration = await MigrationOrchestration.OrchestrateAsync(context, connectionString);
            migration.Succeeded.Should().BeTrue();

            var expectedTables = new[]
            {
                "knowledge", "question", "question_content", "question_knowledge", "tag", "question_tag",
            };
            foreach (var table in expectedTables)
            {
                (await _fixture.CountRowsAsync(connectionString, table)).Should().Be(0, $"table '{table}' must exist");
            }

            var history = await ReadHistoryAsync(connectionString);
            history.Should().Equal("20260504115924_InitialCreate", "20260926094240_AddTags");
        }
        finally
        {
            await _fixture.DropDatabaseIfExistsAsync(database);
        }
    }

    [Fact]
    public async Task MissingDatabase_AllowCreateFalse_RefusesWithZeroCreation()
    {
        var database = _fixture.NewDatabaseName();
        var provider = new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider());

        var act = () => PrepareAsync(
            provider,
            _fixture.GetConnectionString(database),
            ("Database:AllowCreate", "false"));

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("database_target_preparation.creation_not_allowed");
        provider.PrepareCalls.Should().Be(0);
        (await _fixture.DatabaseExistsAsync(database)).Should().BeFalse();
    }

    [Fact]
    public async Task ExistingDatabase_WithNoCreateDbRole_IsAcceptedWithoutCreationPrivileges()
    {
        var role = _fixture.NewRoleName("nocreate_existing");
        await _fixture.CreateRoleAsync(role, _fixture.PasswordCanary, createDb: false);
        var database = await _fixture.CreateDatabaseAsync(owner: role);
        try
        {
            var provider = new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider());

            await PrepareAsync(provider, _fixture.GetConnectionString(database, role, _fixture.PasswordCanary));

            provider.PrepareCalls.Should().Be(0, "no maintenance database connection may be needed");
        }
        finally
        {
            await _fixture.DropDatabaseIfExistsAsync(database);
        }
    }

    [Fact]
    public async Task MissingDatabase_WithNoCreateDbRole_FailsStablyWithoutSecretLeak()
    {
        var role = _fixture.NewRoleName("nocreate_missing");
        await _fixture.CreateRoleAsync(role, _fixture.PasswordCanary, createDb: false);
        var database = _fixture.NewDatabaseName();

        var act = () => PrepareAsync(
            new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider()),
            _fixture.GetConnectionString(database, role, _fixture.PasswordCanary));

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("database_target_preparation.permission_denied");
        exception.Which.ToString().Should().NotContain(_fixture.PasswordCanary);
        (await _fixture.DatabaseExistsAsync(database)).Should().BeFalse();
    }

    [Fact]
    public async Task UnreachableServer_IsRefusedWithoutCreationFallback()
    {
        var provider = new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider());

        var act = () => PrepareAsync(provider, _fixture.GetUnreachableConnectionString("quaestura"));

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("database_target_preparation.connection_failed");
        exception.Which.Message.Should().NotContain(_fixture.PasswordCanary);
        provider.PrepareCalls.Should().Be(0, "an unreachable server must never trigger creation");
    }

    [Fact]
    public async Task InvalidTarget_IsRefused()
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.GetConnectionString("placeholder"))
        {
            Database = "   ", // whitespace-only database names are invalid targets
        };
        var provider = new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider());

        var act = () => PrepareAsync(provider, builder.ConnectionString);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("database_target_preparation.invalid_target");
        provider.PrepareCalls.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentDualInstancePreparation_ConvergesToCreatedAndAlreadyExists()
    {
        var database = _fixture.NewDatabaseName();
        try
        {
            var connectionString = _fixture.GetConnectionString(database);

            // Provider level: two concurrent preparations of the same missing target converge to
            // exactly one Created and one AlreadyExists, both successful.
            var provider = new PostgreSqlDatabaseTargetPreparationProvider();
            var target = new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql, serverVersion: null, connectionString);
            var request = new DatabaseTargetPreparationRequest(target, MaintenanceConnectionString(connectionString));
            var results = await Task.WhenAll(
                provider.PrepareAsync(request, TimeSpan.FromSeconds(30), default).AsTask(),
                provider.PrepareAsync(request, TimeSpan.FromSeconds(30), default).AsTask());

            results.Should().OnlyContain(result => result.Succeeded);
            results.Select(result => result.Outcome).Should().BeEquivalentTo(
                new DatabaseTargetPreparationOutcome?[]
                {
                    DatabaseTargetPreparationOutcome.Created,
                    DatabaseTargetPreparationOutcome.AlreadyExists,
                });

            // Adapter level: two concurrent hosts both pass preparation and both re-observe the
            // target as connectable.
            await Task.WhenAll(
                PrepareAsync(new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider()), connectionString),
                PrepareAsync(new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider()), connectionString));

            var observation = await new PostgreSqlDatabaseTargetPreparationProvider()
                .ObserveAsync(target, default);
            observation.IsTargetConnectable.Should().BeTrue();
        }
        finally
        {
            await _fixture.DropDatabaseIfExistsAsync(database);
        }
    }

    [Fact]
    public async Task OwnerConflict_OnObserve_IsRefusedWithoutOwnerChange()
    {
        // The database belongs to the superuser; a different non-superuser identity has no
        // CONNECT privilege. This must be refused as an unreachable target, never answered with
        // creation or ownership changes, and never accessed with an administrative identity.
        var role = _fixture.NewRoleName("conflicted_user");
        await _fixture.CreateRoleAsync(role, _fixture.PasswordCanary, createDb: false);
        var database = await _fixture.CreateDatabaseAsync(); // owned by the container superuser
        try
        {
            await _fixture.ExecuteAdminAsync($"REVOKE CONNECT ON DATABASE \"{database}\" FROM PUBLIC");
            var ownerBefore = await _fixture.GetDatabaseOwnerAsync(database);
            var provider = new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider());

            var act = () => PrepareAsync(
                provider, _fixture.GetConnectionString(database, role, _fixture.PasswordCanary));

            var exception = await act.Should().ThrowAsync<InvalidOperationException>();
            exception.Which.Message.Should().Contain("database_target_preparation.permission_denied");
            provider.PrepareCalls.Should().Be(0);
            (await _fixture.GetDatabaseOwnerAsync(database)).Should().Be(ownerBefore);
        }
        finally
        {
            await _fixture.DropDatabaseIfExistsAsync(database);
        }
    }

    [Fact]
    public async Task OwnerConflict_DuringPreparationRace_IsRefusedAsTargetConflict()
    {
        // Simulates the race window: the observation still reports TargetMissing although a
        // differently-owned database appeared. The shared provider detects the foreign owner and
        // refuses with target_conflict; the adapter surfaces it and nothing is re-owned.
        var database = await _fixture.CreateDatabaseAsync(); // owned by the container superuser
        try
        {
            var role = _fixture.NewRoleName("racing_user");
            await _fixture.CreateRoleAsync(role, _fixture.PasswordCanary, createDb: true);
            var ownerBefore = await _fixture.GetDatabaseOwnerAsync(database);
            var provider = new CountingProvider(
                new PostgreSqlDatabaseTargetPreparationProvider(),
                forcedObservation: DatabaseTargetObservation.TargetMissing());

            var act = () => PrepareAsync(
                provider, _fixture.GetConnectionString(database, role, _fixture.PasswordCanary));

            var exception = await act.Should().ThrowAsync<InvalidOperationException>();
            exception.Which.Message.Should().Contain("database_target_preparation.target_conflict");
            (await _fixture.GetDatabaseOwnerAsync(database)).Should().Be(ownerBefore);
        }
        finally
        {
            await _fixture.DropDatabaseIfExistsAsync(database);
        }
    }

    [Fact]
    public async Task Cancellation_ThrowsBeforePreparation_AndCreatesNothing()
    {
        var database = _fixture.NewDatabaseName();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var provider = new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider());

        var act = () => RunGateAsync(provider, Configuration(), _fixture.GetConnectionString(database), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        provider.PrepareCalls.Should().Be(0);
        (await _fixture.DatabaseExistsAsync(database)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void AllowCreate_DefaultsToTrue_AndParsesBooleans(string? raw, bool expected)
    {
        var configuration = raw is null ? Configuration() : Configuration(("Database:AllowCreate", raw));

        QuaesturaStartupDatabaseOptions.ReadAllowCreate(configuration).Should().Be(expected);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("maybe")]
    public void AllowCreate_InvalidBoolean_FailsStartup(string raw)
    {
        var configuration = Configuration(("Database:AllowCreate", raw));

        var act = () => QuaesturaStartupDatabaseOptions.ReadAllowCreate(configuration);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("database_target_preparation.invalid_target");
    }

    [Fact]
    public async Task NullConnectionString_RejectsBeforeTouchingAnything()
    {
        var provider = new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider());

        await FluentActions.Awaiting(() => PrepareAsync(provider, connectionString: null))
            .Should().ThrowAsync<InvalidOperationException>();

        provider.ObserveCalls.Should().Be(0);
        provider.PrepareCalls.Should().Be(0);
    }

    [Fact]
    public async Task TwoMissingTargetGates_ConvergeWithOneActualMigration()
    {
        var database = _fixture.NewDatabaseName();
        try
        {
            var target = MigrationOrchestration.Target(_fixture.GetConnectionString(database));
            var gates = new[] { new StartupGateHarness(), new StartupGateHarness() };
            var results = await Task.WhenAll(gates.Select(gate => gate.OrchestrateMigrationAsync(
                MigrationOrchestration.ServiceId, target, TimeSpan.FromSeconds(30)).AsTask()));
            results.Should().OnlyContain(result => result.Succeeded);
            results.Count(result => result.ExecutorWasCalled).Should().Be(1);
            (await ReadHistoryAsync(_fixture.GetConnectionString(database)))
                .Should().Equal("20260504115924_InitialCreate", "20260926094240_AddTags");
        }
        finally { await _fixture.DropDatabaseIfExistsAsync(database); }
    }

    [Fact]
    public async Task CancellationAfterCommittedPreparation_KeepsDatabaseAndStopsBeforeMigration()
    {
        var database = _fixture.NewDatabaseName();
        using var caller = new CancellationTokenSource();
        var preparation = new CancelAfterPreparation(caller);
        var gate = new StartupGateHarness(preparation: preparation);
        try
        {
            var run = gate.OrchestrateMigrationAsync(MigrationOrchestration.ServiceId,
                MigrationOrchestration.Target(_fixture.GetConnectionString(database)), TimeSpan.FromSeconds(30), caller.Token);
            var assertion = await FluentActions.Awaiting(() => run.AsTask()).Should().ThrowAsync<OperationCanceledException>();
            assertion.Which.CancellationToken.Should().Be(caller.Token);
            gate.Receipt.State.Should().Be(ServiceMantle.Health.ServiceMigrationReadinessState.Running);
            (await _fixture.DatabaseExistsAsync(database)).Should().BeTrue();
            preparation.Observations.Should().Be(1, "no re-observation or migration follows cancellation");
            await using var connection = new Npgsql.NpgsqlConnection(_fixture.GetConnectionString(database));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NULL";
            ((bool)(await command.ExecuteScalarAsync())!).Should().BeTrue();
        }
        finally { await _fixture.DropDatabaseIfExistsAsync(database); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("synthetic-credential=invalid")]
    [InlineData("Host=localhost;Port=invalid;Password=synthetic-credential")]
    public void InvalidConnectionConfiguration_RefusesSafelyBeforeIo(string? input)
    {
        var exception = Record.Exception(() => QuaesturaStartupDatabaseOptions.Create(Configuration(), input));
        exception.Should().BeOfType<InvalidOperationException>();
        exception!.ToString().Should().Contain("database_target_preparation.invalid_target")
            .And.NotContain("synthetic-credential");
        exception.InnerException.Should().BeNull();
    }

    private sealed class CancelAfterPreparation(CancellationTokenSource caller) : IDatabaseTargetPreparationProvider
    {
        private readonly PostgreSqlDatabaseTargetPreparationProvider inner = new();
        internal int Observations { get; private set; }
        public string ProviderId => inner.ProviderId;
        public BootstrapDatabaseTargetKind TargetKind => inner.TargetKind;
        public ValueTask<DatabaseTargetObservation> ObserveAsync(BootstrapDatabaseConfiguration target, CancellationToken token)
        {
            token.Should().Be(caller.Token);
            Observations++;
            return inner.ObserveAsync(target, token);
        }
        public async ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request, TimeSpan timeout, CancellationToken token)
        {
            token.Should().Be(caller.Token);
            var result = await inner.PrepareAsync(request, timeout, token);
            result.Succeeded.Should().BeTrue();
            await caller.CancelAsync();
            return result;
        }
    }

    // ---------- helpers ----------

    private static Task PrepareAsync(
        IDatabaseTargetPreparationProvider provider,
        string? connectionString,
        params (string Key, string Value)[] settings) =>
        RunGateAsync(provider, Configuration(settings), connectionString);

    private static async Task RunGateAsync(IDatabaseTargetPreparationProvider provider,
        IConfiguration configuration, string? connectionString, CancellationToken token = default)
    {
        var options = QuaesturaStartupDatabaseOptions.Create(configuration, connectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<QuaesturaDbContext>(builder => builder.UseNpgsql(connectionString));
        services.AddQuaesturaServiceMantle();
        services.RemoveAll<IDatabaseTargetPreparationProvider>();
        services.AddSingleton(provider);
        await using var container = services.BuildServiceProvider();
        var result = await container.GetRequiredService<StartupDatabaseGate>().RunAsync(
            options, new StartupDatabaseReceipt(), ServiceId.Parse("quaestura"), token);
        if (!result.Succeeded) throw new InvalidOperationException($"Database startup failed ({result.ErrorCode}).");
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(
                setting => setting.Key,
                setting => (string?)setting.Value))
            .Build();

    private static string MaintenanceConnectionString(string connectionString) =>
        new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;

    private static async Task<List<string>> ReadHistoryAsync(string connectionString)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>
    /// Counts provider calls and can force a fixed observation to simulate race windows while
    /// delegating every other call to the real shared provider.
    /// </summary>
    private sealed class CountingProvider : IDatabaseTargetPreparationProvider
    {
        private readonly IDatabaseTargetPreparationProvider _inner;
        private readonly DatabaseTargetObservation? _forcedObservation;
        private int _observeCalls;
        private int _prepareCalls;

        internal CountingProvider(
            IDatabaseTargetPreparationProvider inner,
            DatabaseTargetObservation? forcedObservation = null)
        {
            _inner = inner;
            _forcedObservation = forcedObservation;
        }

        internal int ObserveCalls => _observeCalls;

        internal int PrepareCalls => _prepareCalls;

        public string ProviderId => _inner.ProviderId;

        public BootstrapDatabaseTargetKind TargetKind => _inner.TargetKind;

        public ValueTask<DatabaseTargetObservation> ObserveAsync(
            BootstrapDatabaseConfiguration target, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _observeCalls);
            return _forcedObservation is not null && !cancellationToken.IsCancellationRequested
                ? new ValueTask<DatabaseTargetObservation>(_forcedObservation)
                : _inner.ObserveAsync(target, cancellationToken);
        }

        public ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _prepareCalls);
            return _inner.PrepareAsync(request, timeout, cancellationToken);
        }
    }
}
