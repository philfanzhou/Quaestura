using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Migration;

namespace Quaestura.Tests.Database;

/// <summary>
/// End-to-end startup behavior of the target-preparation stage in the real Program.cs host: a
/// missing database is created when allowed and the existing initializer then produces the six
/// tables and both original history rows; Database:AllowCreate=false refuses startup with zero
/// creation; an invalid boolean fails startup fast. The existing InMemory-based
/// QuaesturaApiFactory and all non-relational Testing paths remain untouched.
/// </summary>
[Collection(TargetPreparationIntegrationCollection.Name)]
public sealed class HostStartupTargetPreparationTests
{
    private readonly PostgreSqlTargetPreparationFixture _fixture;

    public HostStartupTargetPreparationTests(PostgreSqlTargetPreparationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Host_Startup_CreatesMissingDatabase_AndAppliesSixTablesAndBothHistoryRows()
    {
        var database = _fixture.NewDatabaseName();
        try
        {
            using var factory = CreateHostFactory(database);

            using var client = factory.CreateClient();
            using var health = await client.GetAsync("/health");
            health.StatusCode.Should().Be(HttpStatusCode.OK);

            (await _fixture.DatabaseExistsAsync(database)).Should().BeTrue();
            (await _fixture.GetDatabaseOwnerAsync(database)).Should().Be(_fixture.Username);
            foreach (var table in new[]
                     {
                         "knowledge", "question", "question_content",
                         "question_knowledge", "tag", "question_tag",
                     })
            {
                (await _fixture.CountRowsAsync(_fixture.GetConnectionString(database), table))
                    .Should().Be(0, $"table '{table}' must exist after startup");
            }

            (await ReadHistoryAsync(database)).Should().Equal(
                "20260504115924_InitialCreate", "20260926094240_AddTags");
        }
        finally
        {
            await _fixture.DropDatabaseIfExistsAsync(database);
        }
    }

    [Fact]
    public async Task Host_Startup_RefusesMissingDatabase_WhenAllowCreateIsFalse()
    {
        var database = _fixture.NewDatabaseName();
        try
        {
            using var factory = CreateHostFactory(database, ("Database:AllowCreate", "false"));

            // The refusal surfaces while the host is being built; the exact wrapper around the
            // startup exception is host infrastructure, so the assertion walks the exception
            // chain for the safe error code.
            var exception = Record.Exception(() => factory.CreateClient());
            exception.Should().NotBeNull();
            exception!.ToString().Should().Contain("database_target_preparation.creation_not_allowed");

            (await _fixture.DatabaseExistsAsync(database)).Should().BeFalse();
        }
        finally
        {
            await _fixture.DropDatabaseIfExistsAsync(database);
        }
    }

    [Fact]
    public void Host_Startup_InvalidAllowCreateBoolean_FailsFast()
    {
        var database = _fixture.NewDatabaseName();
        using var factory = CreateHostFactory(database, ("Database:AllowCreate", "maybe"));

        var exception = Record.Exception(() => factory.CreateClient());

        exception.Should().NotBeNull();
        exception!.ToString().Should().Contain("database_target_preparation.invalid_target");
    }

    [Fact]
    public async Task Host_ExistingDatabaseWithNoCreatedbRole_StartsSuccessfully()
    {
        var role = _fixture.NewRoleName("host_existing");
        await _fixture.CreateRoleAsync(role, _fixture.PasswordCanary, createDb: false);
        var database = await _fixture.CreateDatabaseAsync(owner: role);
        try
        {
            using var factory = CreateHostFactory(database, ("PostgreSql:Username", role));
            using var client = factory.CreateClient();
            using var health = await client.GetAsync("/health/ready");
            health.StatusCode.Should().Be(HttpStatusCode.OK);
            (await _fixture.GetDatabaseOwnerAsync(database)).Should().Be(role);
        }
        finally { await _fixture.DropDatabaseIfExistsAsync(database); }
    }

    [Theory]
    [InlineData("unreachable", "database_target_preparation.connection_failed")]
    [InlineData("authentication", "database_target_preparation.authentication_failed")]
    [InlineData("permission", "database_target_preparation.permission_denied")]
    [InlineData("identity", "database_target_preparation.permission_denied")]
    public async Task Host_TargetFailureNeverStartsOrCreates(string failure, string code)
    {
        var database = _fixture.NewDatabaseName();
        var settings = new List<(string Key, string Value)>();
        string? role = null;
        if (failure is "permission" or "identity")
        {
            role = _fixture.NewRoleName("host_refused");
            await _fixture.CreateRoleAsync(role, _fixture.PasswordCanary, createDb: false);
            settings.Add(("PostgreSql:Username", role));
        }
        if (failure == "unreachable") settings.Add(("PostgreSql:Port", "1"));
        if (failure == "authentication") settings.Add(("PostgreSql:Password", "synthetic-invalid-password"));
        if (failure == "identity")
        {
            database = await _fixture.CreateDatabaseAsync();
            await _fixture.ExecuteAdminAsync($"REVOKE CONNECT ON DATABASE \"{database}\" FROM PUBLIC");
        }
        try
        {
            using var factory = CreateHostFactory(database, settings.ToArray());
            var exception = Record.Exception(() => factory.CreateClient());
            exception.Should().NotBeNull("the real Program must fail before the server starts");
            exception!.ToString().Should().Contain(code).And.NotContain(_fixture.PasswordCanary)
                .And.NotContain("synthetic-invalid-password").And.NotContain("password authentication failed");
            (await _fixture.DatabaseExistsAsync(database)).Should().Be(failure == "identity");
            if (failure == "identity") (await _fixture.GetDatabaseOwnerAsync(database)).Should().Be(_fixture.Username);
        }
        finally { await _fixture.DropDatabaseIfExistsAsync(database); }
    }

    [Theory]
    [InlineData(false, "migration.execution_failed")]
    [InlineData(true, "migration.final_state_invalid")]
    public async Task Host_MigrationOrFinalInspectionFailureNeverStarts(bool mismatch, string code)
    {
        var database = await _fixture.CreateDatabaseAsync();
        try
        {
            using var factory = CreateHostFactory(database).WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IDatabaseMigrationExecutor>();
                    services.AddSingleton<IDatabaseMigrationExecutor>(new FailingExecutor(mismatch));
                }));
            var exception = Record.Exception(() => factory.CreateClient());
            exception.Should().NotBeNull();
            exception!.ToString().Should().Contain(code).And.NotContain("synthetic-driver-error");
        }
        finally { await _fixture.DropDatabaseIfExistsAsync(database); }
    }

    private sealed class FailingExecutor(bool mismatch) : IDatabaseMigrationExecutor
    {
        private bool executed;
        public ValueTask<MigrationObservationState> InspectAsync(CancellationToken token = default) =>
            ValueTask.FromResult(executed ? MigrationObservationState.PendingMigration : MigrationObservationState.Empty);
        public ValueTask ExecuteAsync(CancellationToken token = default)
        {
            executed = true;
            if (!mismatch) throw new InvalidOperationException("synthetic-driver-error");
            return ValueTask.CompletedTask;
        }
    }

    // ---------- helpers ----------

    private WebApplicationFactory<Program> CreateHostFactory(
        string database, params (string Key, string Value)[] extraSettings)
    {
        var (host, port) = _fixture.Server;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            // Deterministic, fast Consul fallback: a closed local port through the supported
            // configuration keys (no environment mutation, no cache writes).
            builder.UseSetting("Consul:Host", "127.0.0.1");
            builder.UseSetting("Consul:Port", "1");
            builder.UseSetting("Consul:EnableCache", "false");
            builder.UseSetting("PostgreSql:Host", host);
            builder.UseSetting("PostgreSql:Port", port);
            builder.UseSetting("PostgreSql:Username", _fixture.Username);
            builder.UseSetting("PostgreSql:Password", _fixture.PasswordCanary);
            builder.UseSetting("Database:Name", database);
            // The Development environment runs the OSS connectivity check; a closed local port
            // makes it fail fast with the documented non-blocking warning.
            builder.UseSetting("Oss:InternalEndpoint", "127.0.0.1:1");
            builder.UseSetting("Oss:PublicBaseUrl", "https://oss.example.com/oss");
            foreach (var (key, value) in extraSettings)
            {
                builder.UseSetting(key, value);
            }
        });
    }

    private async Task<List<string>> ReadHistoryAsync(string database)
    {
        await using var connection = new NpgsqlConnection(_fixture.GetConnectionString(database));
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

}
