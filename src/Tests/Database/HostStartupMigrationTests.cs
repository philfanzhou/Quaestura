using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Quaestura.Database;
using Xunit;

namespace Quaestura.Tests.Database;

/// <summary>
/// One end-to-end startup proof: the real Program.cs host, pointed at a real empty PostgreSQL
/// database through the supported PostgreSql:* configuration shape, applies the full migration
/// contract during startup and then serves /health. This exercises the actual wiring including
/// the cancellation token threaded from the host lifetime.
/// </summary>
[Collection(MigrationIntegrationCollection.Name)]
public sealed class HostStartupMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlMigrationFixture _fixture;
    private string _database = null!;

    public HostStartupMigrationTests(PostgreSqlMigrationFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => _database = await _fixture.CreateDatabaseAsync();

    public async Task DisposeAsync() => await _fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task Host_Startup_AgainstEmptyDatabase_AppliesSixTablesAndBothHistoryRows()
    {
        var (host, port, username) = _fixture.Server;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            // Deterministic, fast Consul fallback: a closed local port through the supported
            // configuration keys (no environment mutation, no cache writes).
            builder.UseSetting("Consul:Host", "127.0.0.1");
            builder.UseSetting("Consul:Port", "1");
            builder.UseSetting("Consul:EnableCache", "false");
            builder.UseSetting("PostgreSql:Host", host);
            builder.UseSetting("PostgreSql:Port", port);
            builder.UseSetting("PostgreSql:Username", username);
            builder.UseSetting("PostgreSql:Password", _fixture.PasswordCanary);
            builder.UseSetting("Database:Name", _database);
            // The Development environment runs the OSS connectivity check; a closed local port
            // makes it fail fast with the documented non-blocking warning.
            builder.UseSetting("Oss:InternalEndpoint", "127.0.0.1:1");
            builder.UseSetting("Oss:PublicBaseUrl", "https://oss.example.com/oss");
        });

        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        health.StatusCode.Should().Be(HttpStatusCode.OK);

        using var context = _fixture.CreateContext(_database);
        foreach (var table in QuaesturaMigrationExecutor.KnownTableNames)
        {
            (await MigrationGoldenStates.TableExistsAsync(context, table))
                .Should().BeTrue($"host startup must create table '{table}'");
        }

        (await MigrationGoldenStates.ReadAppliedHistoryAsync(context)).Should().Equal(
            MigrationGoldenStates.InitialId, MigrationGoldenStates.AddTagsId);
    }
}
