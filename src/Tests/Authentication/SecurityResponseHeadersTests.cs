using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quaestura.Service.Options;
using ServiceMantle;
using Xunit;

namespace Quaestura.Tests.Authentication;

/// <summary>
/// The fixed six-header baseline the ServiceMantle security response-header middleware applies
/// to the two marked admin-auth JSON endpoints. Shared by the existing auth matrix tests and the
/// dedicated middleware-semantics tests below.
/// </summary>
internal static class SecurityResponseHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>
    /// Asserts the complete baseline with exactly one value per header (the middleware's
    /// OnStarting indexer assignment guarantees single values), on success and error alike.
    /// </summary>
    public static void AssertBaseline(HttpResponseMessage response)
    {
        Values(response, "Cache-Control").Should().Equal("no-store");
        Values(response, "Pragma").Should().Equal("no-cache");
        Values(response, "X-Content-Type-Options").Should().Equal("nosniff");
        Values(response, "X-Frame-Options").Should().Equal("DENY");
        Values(response, "Referrer-Policy").Should().Equal("no-referrer");
        Values(response, "Content-Security-Policy").Should().Equal(ContentSecurityPolicy);
    }

    /// <summary>
    /// Asserts the API-only policy is NOT applied: unmarked business endpoints, health, HTML,
    /// and static assets must never receive the destructive default-src 'none' CSP.
    /// </summary>
    public static void AssertNotApplied(HttpResponseMessage response)
    {
        Values(response, "Content-Security-Policy").Should().BeEmpty();
        Values(response, "X-Frame-Options").Should().BeEmpty();
    }

    private static IReadOnlyList<string> Values(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return values.ToList();
        }

        if (response.Content is not null && response.Content.Headers.TryGetValues(name, out values))
        {
            return values.ToList();
        }

        return [];
    }
}

/// <summary>
/// Middleware semantics of the security response-header baseline wired in Program.cs:
/// registration through the single ServiceMantle composition point, endpoint marking on exactly
/// the two admin-auth JSON routes, placement after explicit routing and outside the /admin
/// Problem Details branch. The full UseServiceMantlePipeline stack is deliberately not used;
/// only the capabilities Quaestura wires itself are registered.
/// </summary>
public sealed class SecurityResponseHeadersTests : IClassFixture<QuaesturaApiFactory>, IDisposable
{
    private readonly QuaesturaApiFactory _baseFactory;
    private readonly List<IDisposable> _disposables = [];

    public SecurityResponseHeadersTests(QuaesturaApiFactory factory)
    {
        _baseFactory = factory;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    // ---------- real host: marked auth endpoints ----------

    [Fact]
    public async Task MarkedRoleCallback_Handler500_ReturnsSafeProblemWithBaseline()
    {
        using var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(
                ServiceDescriptor.Singleton<IOptions<AdminPortalOptions>>(new ThrowingPortalOptions()))));
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/admin/auth/callback", Json("{\"user_id\":\"synthetic-probe-user\"}"));
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);
        body.RootElement.GetProperty("errorCode").GetString().Should().Be("quaestura.invalid_operation");
        body.RootElement.GetProperty("title").GetString().Should().Be("An unexpected error occurred.");
        raw.Should().NotContain("security-probe failure");
        SecurityResponseHeaders.AssertBaseline(response);
    }

    [Fact]
    public async Task CancelledMarkedProbe_Propagates_AndHostKeepsBaseline()
    {
        var hang = new HangingState();
        await using var app = await StartProbeHostAsync(hang);
        using var client = app.GetTestClient();
        using var cts = new CancellationTokenSource();
        var request = client.PostAsync("/probe/cancel", null, cts.Token);
        await hang.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await FluentActions.Awaiting(() => request).Should().ThrowAsync<OperationCanceledException>();
        await hang.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var next = await client.PostAsync("/probe/override", null);
        next.StatusCode.Should().Be(HttpStatusCode.OK);
        SecurityResponseHeaders.AssertBaseline(next);
    }

    // ---------- real host: unmarked surfaces keep their contract ----------

    [Fact]
    public async Task UnmarkedHealth_AndProtectedApi_KeepContract_WithoutApiOnlyPolicy()
    {
        using var client = _baseFactory.CreateClient();
        _disposables.Add(client);

        using var health = await client.GetAsync("/health");
        health.StatusCode.Should().Be(HttpStatusCode.OK);
        SecurityResponseHeaders.AssertNotApplied(health);

        // /admin/tags without a token stays 401 (not anonymous) and unmarked.
        using var tags = await client.GetAsync("/admin/tags");
        tags.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        SecurityResponseHeaders.AssertNotApplied(tags);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/login")]
    [InlineData("/questions/123")]
    [InlineData("/app.js")]
    public async Task SpaAndAssets_KeepAnonymousContract_WithoutDestructiveCsp(string path)
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"quaestura-security-spa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(
            Path.Combine(webRoot, "index.html"),
            "<!doctype html><html><head><title>__APP_TITLE__</title></head><body><div id=\"app\"></div></body></html>");
        File.WriteAllText(Path.Combine(webRoot, "app.js"), "console.log('app');");
        using var factory = _baseFactory.WithWebHostBuilder(builder => builder.UseWebRoot(webRoot));
        using var client = factory.CreateClient();

        try
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            SecurityResponseHeaders.AssertNotApplied(response);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    // ---------- library semantics on marked endpoints ----------

    /// <summary>
    /// A probe host wired exactly like Program.cs (explicit routing, the security middleware
    /// outside the Problem Details boundary, capability registered through
    /// AddSecurityResponseHeaders alone — no full management pipeline) verifies the two
    /// remaining semantics on marked endpoints: an endpoint that tries to set the same headers
    /// still ends at the fixed single-value baseline, and marked handlers that throw get the
    /// fixed safe 500 problem with the same baseline.
    /// </summary>
    [Fact]
    public async Task MarkedProbeEndpoint_OverrideAttempt_LosesToSingleValueBaseline()
    {
        await using var app = await StartProbeHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsync("/probe/override", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");
        SecurityResponseHeaders.AssertBaseline(response);
    }

    [Fact]
    public async Task MarkedProbeEndpoint_ThrowingHandler_KeepsSafeProblem500WithBaseline()
    {
        await using var app = await StartProbeHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsync("/probe/throw", null);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be("http.internal_server_error");
        SecurityResponseHeaders.AssertBaseline(response);
    }

    [Fact]
    public async Task UnmarkedProbeEndpoint_GetsNoBaseline()
    {
        await using var app = await StartProbeHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/probe/unmarked");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SecurityResponseHeaders.AssertNotApplied(response);
    }

    private static async Task<WebApplication> StartProbeHostAsync(HangingState? hang = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddServiceMantle(ServiceId.Parse("quaestura"), InstanceId.Parse("quaestura-probe"))
            .AddSecurityResponseHeaders();
        var app = builder.Build();
        // Same order as Program.cs: routing selects the endpoint, the security middleware runs
        // outside the Problem Details boundary (every probe route is a JSON endpoint, so the
        // boundary is applied without a path branch).
        app.UseRouting();
        app.UseServiceMantleSecurityResponseHeaders();
        app.UseServiceMantleProblemDetails();
        app.MapPost("/probe/override", (HttpContext context) =>
        {
            // A marked endpoint trying to write the same headers before the response starts.
            context.Response.Headers.CacheControl = "max-age=999";
            context.Response.Headers.Append("X-Frame-Options", "SAMEORIGIN");
            context.Response.Headers.ContentSecurityPolicy = "default-src *";
            return Results.Ok(new { success = true });
        }).RequireServiceMantleSecurityResponseHeaders();
        if (hang is not null)
            app.MapPost("/probe/cancel", async (HttpContext context) =>
            {
                hang.Entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
                catch (OperationCanceledException) { hang.Aborted.TrySetResult(); throw; }
            }).RequireServiceMantleSecurityResponseHeaders();
        app.MapPost("/probe/throw", ThrowProbeFailure)
            .RequireServiceMantleSecurityResponseHeaders();
        app.MapGet("/probe/unmarked", () => Results.Ok(new { success = true }));
        await app.StartAsync();
        return app;

        static IResult ThrowProbeFailure() =>
            throw new InvalidOperationException("probe failure");
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed class ThrowingPortalOptions : IOptions<AdminPortalOptions>
    {
        public AdminPortalOptions Value => throw new InvalidOperationException("security-probe failure");
    }

    private sealed class HangingState
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

}
