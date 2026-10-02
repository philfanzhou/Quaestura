using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quaestura.Host;
using Quaestura.Tests.Authentication;
using ServiceMantle.Logging;
using ServiceMantle.Logging.Pipeline;
using ServiceMantle.Web.Logging;
using Xunit;

namespace Quaestura.Tests.Observability;

/// <summary>
/// Verifies the shared ServiceMantle logging pipeline (mandatory-sanitizing Serilog Console +
/// opt-in Grafana Loki) against a local fake Loki: both real sinks observe the same filtered
/// and sanitized events, the fixed <c>service=Quaestura</c> stream label is intact, synthetic
/// secrets never reach either sink, and remote failures never block business requests. All
/// canaries are synthetic; no real credential, connection string, or user identifier is used.
/// Console capture is process-wide, so this class shares the serialized console-logging
/// collection with <see cref="AdminAuthEndpointsTests"/>.
/// </summary>
[Collection(ConsoleLoggingCollection.Name)]
public sealed class ServiceMantleLoggingTests
{
    private const string PasswordCanary = "synthetic-password-canary";
    private const string ConnectionCanary = "Host=db.internal;Port=5432;Username=app;Password=synthetic-connection-canary";
    private const string AppSecretCanary = "synthetic-app-secret-canary";
    private const string AuthorizationCanary = "Bearer synthetic-authorization-canary";
    private const string CookieCanary = "session=synthetic-cookie-canary";
    private const string ExceptionCanary = "synthetic-exception-canary";

    private const string ProbeCategory = "Quaestura.LoggingProbe";

    [Fact]
    public async Task RealHost_ConsoleAndLokiShareFilteredSanitizedEvents()
    {
        var (loki, address, batches, _) = await StartFakeLokiAsync();
        await using (loki)
        {
            var originalOut = Console.Out;
            using var output = new StringWriter();
            Console.SetOut(output);
            try
            {
                using var factory = new QuaesturaApiFactory()
                    .WithWebHostBuilder(builder =>
                    {
                        builder.UseSetting("Loki:Uri", address);
                        builder.UseSetting("AdminPortal:AdminUserIds:0", "synthetic-logging-user");
                    });
                using var client = factory.CreateClient();

                // The retained role callback logs its grant without personal identifiers.
                // Both real sinks must receive this event in the correlation/identity scope.
                using var callbackResponse = await client.PostAsJsonAsync(
                    "/admin/auth/callback", new { user_id = "synthetic-logging-user" });
                Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);
                var correlation = Assert.Single(
                    callbackResponse.Headers.GetValues(ObservabilityTestHelpers.CorrelationHeaderName));

                using var retired = await client.PostAsync("/admin/auth/login",
                    Json("{\"password\":\"synthetic-retired-log-canary\",\"secret\":\"synthetic-retired-log-canary\",\"token\":\"synthetic-retired-log-canary\"}"));
                await AdminAuthEndpointsTests.AssertRetired(retired);

                // Synthetic secret-bearing events through the real MEL entry point.
                var loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();
                var probe = loggerFactory.CreateLogger(ProbeCategory);
                probe.LogInformation("probe.application {Password}", PasswordCanary);
                probe.LogInformation(
                    "probe.connection {Connection}",
                    new DbConnectionStringBuilder { ConnectionString = ConnectionCanary });
                probe.LogInformation(
                    "probe.secrets {XAdminAppSecret} {Authorization} {Cookie}",
                    AppSecretCanary,
                    AuthorizationCanary,
                    CookieCanary);
                probe.LogError(
                    new HttpRequestException($"upstream https://user:{ExceptionCanary}@identity.internal refused"),
                    "probe.exception");

                // Category level overrides apply to both sinks: Warning passes, Information
                // from the overridden categories does not.
                loggerFactory.CreateLogger("Microsoft.AspNetCore.Hosting")
                    .LogInformation("probe.aspnet.information");
                loggerFactory.CreateLogger("Microsoft.AspNetCore.Hosting")
                    .LogWarning("probe.aspnet.warning");
                loggerFactory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
                    .LogInformation("probe.ef.information");
                loggerFactory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
                    .LogWarning("probe.ef.warning");
                probe.LogInformation("probe.completed");

                await WaitForRemoteAsync(batches, "probe.completed");

                var logContext = factory.Services.GetRequiredService<ServiceLogContext>();
                var remote = RemoteLines(batches);
                var console = output.ToString();
                foreach (var actual in new[] { console, remote })
                {
                    Assert.Contains("probe.application", actual);
                    Assert.Contains("probe.aspnet.warning", actual);
                    Assert.Contains("probe.ef.warning", actual);
                    Assert.DoesNotContain("probe.aspnet.information", actual);
                    Assert.DoesNotContain("probe.ef.information", actual);

                    // Sanitization is mandatory in the shared pipeline: no synthetic secret
                    // survives into either sink, and denied fields keep the redaction marker.
                    Assert.DoesNotContain(PasswordCanary, actual);
                    Assert.DoesNotContain("synthetic-retired-log-canary", actual);
                    Assert.DoesNotContain(ConnectionCanary, actual);
                    Assert.DoesNotContain(AppSecretCanary, actual);
                    Assert.DoesNotContain(AuthorizationCanary, actual);
                    Assert.DoesNotContain(CookieCanary, actual);
                    Assert.DoesNotContain(ExceptionCanary, actual);
                    Assert.DoesNotContain("synthetic-logging-user", actual);
                    Assert.Contains(StructuredLogSanitizer.RedactedValue, actual);

                    // The request-scoped event carries the shared structured identity and the
                    // caller correlation id; the legacy MachineName/ThreadId enrichers are gone.
                    var requestLine = actual
                        .Split('\n')
                        .First(line => line.Contains("Identity callback: granted the admin role"));
                    Assert.Contains(correlation, requestLine);
                    Assert.Contains(logContext.ServiceName, requestLine);
                    Assert.Contains(logContext.ServiceVersion, requestLine);
                    Assert.Contains(logContext.InstanceId, requestLine);
                    Assert.DoesNotContain("MachineName", requestLine);
                    Assert.DoesNotContain("ThreadId", requestLine);
                }

                // Loki stream labels: the fixed service label plus the sink-owned level label,
                // never request/user/instance values promoted to labels.
                foreach (var batch in batches)
                {
                    foreach (var stream in JsonDocument.Parse(batch).RootElement
                                 .GetProperty("streams").EnumerateArray())
                    {
                        var labels = stream.GetProperty("stream");
                        Assert.Equal("Quaestura", labels.GetProperty("service").GetString());
                        Assert.True(labels.TryGetProperty("level", out _));
                        Assert.Equal(2, labels.EnumerateObject().Count());
                    }
                }
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    [Fact]
    public async Task EmptyLokiUri_KeepsConsoleOnlyAndIgnoresLegacyWriteToConfiguration()
    {
        var (loki, address, _, requests) = await StartFakeLokiAsync();
        await using (loki)
        {
            var originalOut = Console.Out;
            using var output = new StringWriter();
            Console.SetOut(output);
            try
            {
                using var factory = new QuaesturaApiFactory().WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("Loki:Uri", " ");
                    // A stale legacy Serilog WriteTo argument must never re-enable the sink.
                    builder.UseSetting("Serilog:WriteTo:1:Args:uri", address);
                });
                using var client = factory.CreateClient();
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
                factory.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(ProbeCategory)
                    .LogInformation("probe.console.only");

                // Wait beyond the remote flush period: nothing is ever queued or pushed.
                await Task.Delay(TimeSpan.FromSeconds(3));
                Assert.Equal(0, requests());
                Assert.Contains("probe.console.only", output.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    [Theory]
    [InlineData("relative/canary")]
    [InlineData("http://user:canary@127.0.0.1:3100")]
    [InlineData("https://example.test?token=canary")]
    [InlineData("https://example.test#canary")]
    public async Task InvalidLokiUri_FailsStartupWithSafeCode(string uri)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration["Loki:Uri"] = uri;
        builder.AddQuaesturaLogging();
        using var host = builder.Build();

        var failure = await Assert.ThrowsAsync<SerilogConfigurationException>(() => host.StartAsync());
        Assert.Equal("loki.invalid_endpoint", failure.ErrorCode);
        Assert.DoesNotContain("canary", failure.ToString());
    }

    [Fact]
    public async Task PlainHttpLoki_AllowedByDefault_RejectedWhenExplicitlyDisabled()
    {
        // Default: AllowInsecureHttp is true, the explicit continuation of the existing
        // trusted-network HTTP deployment contract.
        var allowed = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        allowed.Configuration["Loki:Uri"] = "http://127.0.0.1:3100";
        allowed.AddQuaesturaLogging();
        using (var host = allowed.Build())
        {
            await host.StartAsync();
            await host.StopAsync();
        }

        // Explicit false requires HTTPS; the failure never echoes the submitted value.
        var denied = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        denied.Configuration["Loki:Uri"] = "http://insecure-canary.test:3100";
        denied.Configuration["Loki:AllowInsecureHttp"] = "false";
        denied.AddQuaesturaLogging();
        using (var host = denied.Build())
        {
            var failure = await Assert.ThrowsAsync<SerilogConfigurationException>(() => host.StartAsync());
            Assert.Equal("loki.invalid_endpoint", failure.ErrorCode);
            Assert.DoesNotContain("insecure-canary", failure.ToString());
        }
    }

    [Fact]
    public async Task FailingLoki_RetriesWithoutBlockingBusinessRequests()
    {
        var (loki, address, _, requests) = await StartFakeLokiAsync(statusCode: 503);
        await using (loki)
        {
            using var factory = new QuaesturaApiFactory()
                .WithWebHostBuilder(builder => builder.UseSetting("Loki:Uri", address));
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

            // The sink keeps retrying the failing endpoint asynchronously.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (requests() < 2)
            {
                await Task.Delay(20, timeout.Token);
            }

            // Business requests stay responsive while the remote sink is failing.
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        }
    }

    [Fact]
    public async Task HangingLoki_ShutdownDrainStaysBounded()
    {
        var (loki, address, _, requests) = await StartFakeLokiAsync(hang: true);
        try
        {
            var factory = new QuaesturaApiFactory()
                .WithWebHostBuilder(builder => builder.UseSetting("Loki:Uri", address));
            try
            {
                using var client = factory.CreateClient();
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
                factory.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(ProbeCategory)
                    .LogInformation("probe.drain");

                // Wait until one push is actually pending against the hanging endpoint.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (requests() < 1)
                {
                    await Task.Delay(20, timeout.Token);
                }

                var stopwatch = Stopwatch.StartNew();
                factory.Dispose();
                stopwatch.Stop();

                // The drain never waits for the hanging request: shutdown stays bounded by the
                // sink drain timeout and the host shutdown timeout (both 5 s by default).
                Assert.True(
                    stopwatch.Elapsed < TimeSpan.FromSeconds(30),
                    $"Shutdown drain took {stopwatch.Elapsed}, which is not bounded.");
            }
            finally
            {
                factory.Dispose();
            }
        }
        finally
        {
            await loki.DisposeAsync();
        }
    }

    private static async Task WaitForRemoteAsync(ConcurrentQueue<string> batches, string marker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!batches.Any(batch => batch.Contains(marker)))
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static string RemoteLines(ConcurrentQueue<string> batches) =>
        string.Join(
            '\n',
            batches.SelectMany(batch => JsonDocument.Parse(batch).RootElement
                .GetProperty("streams").EnumerateArray()
                .SelectMany(stream => stream.GetProperty("values").EnumerateArray()
                    .Select(value => value[1].GetString()!))));

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// Starts a minimal local Loki stand-in on a loopback port that records raw push batches.
    /// <paramref name="statusCode"/> is returned for every push; <paramref name="hang"/> keeps
    /// push requests pending to exercise bounded shutdown draining.
    /// </summary>
    private static async Task<(WebApplication Host, string Address, ConcurrentQueue<string> Batches, Func<int> Requests)>
        StartFakeLokiAsync(int statusCode = 204, bool hang = false)
    {
        var batches = new ConcurrentQueue<string>();
        var requests = 0;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var host = builder.Build();
        host.MapPost("/loki/api/v1/push", async context =>
        {
            Interlocked.Increment(ref requests);
            batches.Enqueue(await new StreamReader(context.Request.Body).ReadToEndAsync());
            if (hang)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), context.RequestAborted);
            }

            context.Response.StatusCode = statusCode;
        });
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        return (host, address, batches, () => Volatile.Read(ref requests));
    }
}
