using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

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
            var preparationException = FindPreparationException(exception!);
            preparationException.Should().NotBeNull();
            preparationException!.ErrorCode
                .Should().Be("database_target_preparation.creation_not_allowed");

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
        var preparationException = FindPreparationException(exception!);
        preparationException.Should().NotBeNull();
        preparationException!.ErrorCode
            .Should().Be("database_target_preparation.invalid_configuration");
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

    private static Quaestura.Host.DatabaseTargetPreparationException? FindPreparationException(
        Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Quaestura.Host.DatabaseTargetPreparationException preparationException)
            {
                return preparationException;
            }
        }

        return null;
    }
}
