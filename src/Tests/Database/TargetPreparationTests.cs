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

            // The original initializer still owns the business tables and history.
            var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<QuaesturaDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            using var context = new QuaesturaDbContext(options);
            await DatabaseInitializer.InitializeAsync(context, NullLoggerFactory.Instance);

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

        var exception = await act.Should().ThrowAsync<DatabaseTargetPreparationException>();
        exception.Which.ErrorCode.Should().Be("database_target_preparation.creation_not_allowed");
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

        var exception = await act.Should().ThrowAsync<DatabaseTargetPreparationException>();
        exception.Which.ErrorCode.Should().Be("database_target_preparation.permission_denied");
        exception.Which.ToString().Should().NotContain(_fixture.PasswordCanary);
        (await _fixture.DatabaseExistsAsync(database)).Should().BeFalse();
    }

    [Fact]
    public async Task UnreachableServer_IsRefusedWithoutCreationFallback()
    {
        var provider = new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider());

        var act = () => PrepareAsync(provider, _fixture.GetUnreachableConnectionString("quaestura"));

        var exception = await act.Should().ThrowAsync<DatabaseTargetPreparationException>();
        exception.Which.ErrorCode.Should().Be("database_target_preparation.connection_failed");
        exception.Which.Message.Should().Contain("ServerUnreachable");
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

        var exception = await act.Should().ThrowAsync<DatabaseTargetPreparationException>();
        exception.Which.ErrorCode.Should().Be("database_target_preparation.invalid_target");
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

            var exception = await act.Should().ThrowAsync<DatabaseTargetPreparationException>();
            exception.Which.ErrorCode.Should().Be("database_target_preparation.permission_denied");
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

            var exception = await act.Should().ThrowAsync<DatabaseTargetPreparationException>();
            exception.Which.ErrorCode.Should().Be("database_target_preparation.target_conflict");
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

        var act = () => QuaesturaDatabaseTargetPreparer.PrepareAsync(
            provider,
            Configuration(),
            _fixture.GetConnectionString(database),
            NullLogger.Instance,
            cancelled.Token);

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

        QuaesturaDatabaseTargetPreparer.ReadAllowCreate(configuration).Should().Be(expected);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("maybe")]
    public void AllowCreate_InvalidBoolean_FailsStartup(string raw)
    {
        var configuration = Configuration(("Database:AllowCreate", raw));

        var act = () => QuaesturaDatabaseTargetPreparer.ReadAllowCreate(configuration);

        act.Should().Throw<DatabaseTargetPreparationException>()
            .Which.ErrorCode.Should().Be("database_target_preparation.invalid_configuration");
    }

    [Fact]
    public async Task NullConnectionString_SkipsPreparationWithoutTouchingAnything()
    {
        var provider = new CountingProvider(new PostgreSqlDatabaseTargetPreparationProvider());

        await PrepareAsync(provider, connectionString: null);

        provider.ObserveCalls.Should().Be(0);
        provider.PrepareCalls.Should().Be(0);
    }

    // ---------- helpers ----------

    private static Task PrepareAsync(
        IDatabaseTargetPreparationProvider provider,
        string? connectionString,
        params (string Key, string Value)[] settings) =>
        QuaesturaDatabaseTargetPreparer.PrepareAsync(
            provider,
            Configuration(settings),
            connectionString,
            NullLogger.Instance,
            CancellationToken.None);

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
