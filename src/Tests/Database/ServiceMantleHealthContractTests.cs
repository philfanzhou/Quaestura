using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Quaestura.Database;
using Quaestura.Host;
using Quaestura.Tests.Authentication;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using Xunit;

namespace Quaestura.Tests.Database;

/// <summary>
/// The consumer-side health contract on the InMemory-backed test host: the three fixed routes
/// are anonymous and serve the library JSON, readiness fails closed for a missing, throwing,
/// unfinished, failed, or slow snapshot source, cancellation propagates on the caller's own
/// token, every request probes through its own scope, and the SPA fallback never swallows the
/// probes. The real PostgreSQL evidence lives in <see cref="ServiceMantleHealthPostgreSqlTests"/>.
/// </summary>
public sealed class ServiceMantleHealthContractTests : IClassFixture<QuaesturaApiFactory>
{
    private readonly QuaesturaApiFactory _baseFactory;

    public ServiceMantleHealthContractTests(QuaesturaApiFactory factory)
    {
        _baseFactory = factory;
    }

    // ---------- happy path and wire contract ----------

    [Fact]
    public async Task RealInitialization_ThreeAnonymousProbes_ServeLibraryJson()
    {
        using var client = _baseFactory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        live.StatusCode.Should().Be(HttpStatusCode.OK);
        var liveBody = await ReadJsonAsync(live);
        liveBody.GetProperty("status").GetString().Should().Be("live");

        foreach (var path in new[] { "/health/ready", "/health" })
        {
            using var ready = await client.GetAsync(path);
            ready.StatusCode.Should().Be(HttpStatusCode.OK);
            ready.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            var body = await ReadJsonAsync(ready);
            body.GetProperty("status").GetString().Should().Be("ready");
            body.GetProperty("phase").GetString().Should().Be("completed");
            body.GetProperty("migrationStatus").GetString().Should().Be("succeeded");
            body.GetProperty("databaseStatus").GetString().Should().Be("reachable");
            body.GetProperty("errorCode").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task ScopedResolution_NeverSharesContextOrSourceAcrossRequests()
    {
        using var scope1 = _baseFactory.Services.CreateScope();
        using var scope2 = _baseFactory.Services.CreateScope();

        var source1 = scope1.ServiceProvider.GetRequiredService<IServiceHealthSnapshotSource>();
        var source2 = scope2.ServiceProvider.GetRequiredService<IServiceHealthSnapshotSource>();
        source1.Should().BeOfType<QuaesturaHealthSnapshotSource>();
        source1.Should().NotBeSameAs(source2);

        scope1.ServiceProvider.GetRequiredService<QuaesturaDbContext>()
            .Should().NotBeSameAs(scope2.ServiceProvider.GetRequiredService<QuaesturaDbContext>());
    }

    // ---------- fail-closed readiness ----------

    [Fact]
    public async Task MissingSnapshotSource_FailsClosed_LiveStaysUp()
    {
        using var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.RemoveAll<IServiceHealthSnapshotSource>()));
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        live.StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var path in new[] { "/health/ready", "/health" })
        {
            using var ready = await client.GetAsync(path);
            ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = await ReadJsonAsync(ready);
            body.GetProperty("status").GetString().Should().Be("not_ready");
            body.GetProperty("errorCode").GetString().Should().Be("health.probe_failed");
            body.GetProperty("phase").ValueKind.Should().Be(JsonValueKind.Null);
            body.GetProperty("migrationStatus").ValueKind.Should().Be(JsonValueKind.Null);
            body.GetProperty("databaseStatus").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task ThrowingSnapshotSource_FailsClosedWithProbeFailed_LiveNeverResolvesIt()
    {
        using var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Scoped<IServiceHealthSnapshotSource>(
                    _ => new ThrowingSnapshotSource()))));
        using var client = factory.CreateClient();

        // The live route must never resolve the snapshot source.
        using var live = await client.GetAsync("/health/live");
        live.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(live)).GetProperty("status").GetString().Should().Be("live");

        using var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await ReadJsonAsync(ready);
        body.GetProperty("status").GetString().Should().Be("not_ready");
        body.GetProperty("errorCode").GetString().Should().Be("health.probe_failed");
    }

    [Theory]
    [InlineData(ServiceMigrationReadinessState.NotStarted, "notStarted", ServiceStartupPhase.BootstrapConfiguration, "bootstrapConfiguration")]
    [InlineData(ServiceMigrationReadinessState.Running, "running", ServiceStartupPhase.BootstrapConfiguration, "bootstrapConfiguration")]
    [InlineData(ServiceMigrationReadinessState.Failed, "failed", ServiceStartupPhase.Completed, "completed")]
    public async Task InjectedUnfinishedOrFailedInitialization_IsNeverReady(
        ServiceMigrationReadinessState migration,
        string migrationWire,
        ServiceStartupPhase phase,
        string phaseWire)
    {
        using var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Scoped<IServiceHealthSnapshotSource>(
                    _ => new FixedSnapshotSource(new ServiceHealthSnapshot(
                        phase,
                        migration,
                        ServiceDatabaseReadinessState.Reachable))))));
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        live.StatusCode.Should().Be(HttpStatusCode.OK);

        using var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await ReadJsonAsync(ready);
        body.GetProperty("status").GetString().Should().Be("not_ready");
        body.GetProperty("migrationStatus").GetString().Should().Be(migrationWire);
        body.GetProperty("phase").GetString().Should().Be(phaseWire);
        body.GetProperty("databaseStatus").GetString().Should().Be("reachable");
    }

    [Fact]
    public async Task InjectedUnreachableDatabase_IsNotReady_LiveStaysUp()
    {
        using var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Scoped<IServiceHealthSnapshotSource>(
                    _ => new FixedSnapshotSource(new ServiceHealthSnapshot(
                        ServiceStartupPhase.Completed,
                        ServiceMigrationReadinessState.Succeeded,
                        ServiceDatabaseReadinessState.Unreachable,
                        QuaesturaHealthSnapshotSource.DatabaseUnreachableErrorCode))))));
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        live.StatusCode.Should().Be(HttpStatusCode.OK);

        using var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await ReadJsonAsync(ready);
        body.GetProperty("status").GetString().Should().Be("not_ready");
        body.GetProperty("databaseStatus").GetString().Should().Be("unreachable");
        body.GetProperty("errorCode").GetString()
            .Should().Be(QuaesturaHealthSnapshotSource.DatabaseUnreachableErrorCode);
    }

    [Fact]
    public async Task SlowSnapshotSource_TimesOutWithProbeTimeout_BoundedFields()
    {
        using var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Scoped<IServiceHealthSnapshotSource>(
                    _ => new SlowSnapshotSource(TimeSpan.FromSeconds(15))))));
        using var client = factory.CreateClient();

        var started = DateTime.UtcNow;
        using var ready = await client.GetAsync("/health/ready");
        var elapsed = DateTime.UtcNow - started;

        // The library's default probe timeout (5 s) bounds the read; the 15 s source never
        // gets to answer.
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(12));
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await ReadJsonAsync(ready);
        body.GetProperty("status").GetString().Should().Be("not_ready");
        body.GetProperty("errorCode").GetString().Should().Be("health.probe_timeout");
        body.EnumerateObject().Select(property => property.Name).Should()
            .BeEquivalentTo("status", "phase", "migrationStatus", "databaseStatus", "errorCode");

        using var live = await client.GetAsync("/health/live");
        live.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesOriginalToken_SourceObservesIt()
    {
        var hang = new HangingSnapshotSource();
        using var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Scoped<IServiceHealthSnapshotSource>(
                    _ => hang))));
        using var client = factory.CreateClient();
        using var cts = new CancellationTokenSource();

        var call = client.GetAsync("/health/ready", cts.Token);
        await hang.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        // The request fails with the caller's own cancellation — no faked 200/503 — and the
        // source observes the cancellation on the token it received.
        await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        await hang.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var live = await client.GetAsync("/health/live");
        live.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------- real source and state, unit level ----------

    [Fact]
    public async Task RealSource_UnreachableDatabase_ClassifiesWithFixedCodeOnly()
    {
        const string passwordCanary = "synthetic-probe-password";
        var options = new DbContextOptionsBuilder<QuaesturaDbContext>()
            .UseNpgsql(
                $"Host=127.0.0.1;Port=1;Database=health_probe;Username=probe;Password={passwordCanary};Timeout=2;Pooling=false")
            .Options;
        using var context = new QuaesturaDbContext(options);
        var state = new QuaesturaStartupHealthState();
        state.RecordRunning();
        state.RecordSucceeded();
        var source = new QuaesturaHealthSnapshotSource(
            context, state, NullLogger<QuaesturaHealthSnapshotSource>.Instance);

        var snapshot = await source.GetSnapshotAsync(CancellationToken.None);

        snapshot.Phase.Should().Be(ServiceStartupPhase.Completed);
        snapshot.MigrationStatus.Should().Be(ServiceMigrationReadinessState.Succeeded);
        snapshot.DatabaseStatus.Should().Be(ServiceDatabaseReadinessState.Unreachable);
        snapshot.ErrorCode.Should().Be(QuaesturaHealthSnapshotSource.DatabaseUnreachableErrorCode);
        // The snapshot carries finite states and the safe code only — never driver details.
        snapshot.ToString().Should().NotContain(passwordCanary).And.NotContain("Npgsql");
        ServiceHealthEvaluator.Evaluate(snapshot).IsReady.Should().BeFalse();
    }

    [Fact]
    public async Task RealSource_PropagatesCallerCancellationOnOriginalToken()
    {
        using var context = new QuaesturaDbContext(
            new DbContextOptionsBuilder<QuaesturaDbContext>()
                .UseInMemoryDatabase($"health-cancel-{Guid.NewGuid():N}")
                .Options);
        var state = new QuaesturaStartupHealthState();
        var source = new QuaesturaHealthSnapshotSource(context, state);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var exception = await FluentActions
            .Awaiting(() => source.GetSnapshotAsync(cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public void StartupState_DefaultsToNotStarted_AndTerminalStatesAreFinal()
    {
        var state = new QuaesturaStartupHealthState();
        state.MigrationStatus.Should().Be(ServiceMigrationReadinessState.NotStarted);
        state.StartupGatePassed.Should().BeFalse();
        // The default enum value never counts as success.
        ServiceHealthEvaluator.Evaluate(new ServiceHealthSnapshot(
                ServiceStartupPhase.BootstrapConfiguration,
                state.MigrationStatus,
                ServiceDatabaseReadinessState.Reachable))
            .IsReady.Should().BeFalse();

        state.RecordRunning();
        state.MigrationStatus.Should().Be(ServiceMigrationReadinessState.Running);
        state.StartupGatePassed.Should().BeFalse();

        state.RecordFailed();
        state.MigrationStatus.Should().Be(ServiceMigrationReadinessState.Failed);
        state.RecordSucceeded();
        state.MigrationStatus.Should().Be(ServiceMigrationReadinessState.Failed,
            "a terminal failure is final");

        var succeeded = new QuaesturaStartupHealthState();
        succeeded.RecordRunning();
        succeeded.RecordSucceeded();
        succeeded.MigrationStatus.Should().Be(ServiceMigrationReadinessState.Succeeded);
        succeeded.StartupGatePassed.Should().BeTrue();
        succeeded.RecordFailed();
        succeeded.RecordRunning();
        succeeded.MigrationStatus.Should().Be(ServiceMigrationReadinessState.Succeeded,
            "a terminal success is final");
        ServiceHealthEvaluator.Evaluate(new ServiceHealthSnapshot(
                ServiceStartupPhase.Completed,
                succeeded.MigrationStatus,
                ServiceDatabaseReadinessState.Reachable))
            .IsReady.Should().BeTrue();
    }

    // ---------- routing coexistence ----------

    [Fact]
    public async Task SpaFallback_NeverSwallowsTheHealthJson()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"quaestura-health-spa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(
            Path.Combine(webRoot, "index.html"),
            "<!doctype html><html><head><title>__APP_TITLE__</title></head><body><div id=\"app\"></div></body></html>");
        using var factory = _baseFactory.WithWebHostBuilder(builder => builder.UseWebRoot(webRoot));
        using var client = factory.CreateClient();
        try
        {
            foreach (var path in new[] { "/health/live", "/health/ready", "/health" })
            {
                using var response = await client.GetAsync(path);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            }

            // The SPA itself still works right next to the probes.
            using var spa = await client.GetAsync("/login");
            spa.StatusCode.Should().Be(HttpStatusCode.OK);
            (await spa.Content.ReadAsStringAsync()).Should().Contain("window.__APP_TITLE__");
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private sealed class FixedSnapshotSource(ServiceHealthSnapshot snapshot)
        : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(snapshot);
    }

    private sealed class ThrowingSnapshotSource : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("health-probe stub failure");
    }

    private sealed class SlowSnapshotSource(TimeSpan delay) : IServiceHealthSnapshotSource
    {
        public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return new ServiceHealthSnapshot(
                ServiceStartupPhase.Completed,
                ServiceMigrationReadinessState.Succeeded,
                ServiceDatabaseReadinessState.Reachable);
        }
    }

    private sealed class HangingSnapshotSource : IServiceHealthSnapshotSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }
    }
}
