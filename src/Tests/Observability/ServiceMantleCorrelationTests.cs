using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Quaestura.Domain.Services;
using Quaestura.Service.Options;
using Quaestura.Tests.Authentication;
using ServiceMantle.Web.Logging;
using Xunit;

namespace Quaestura.Tests.Observability;

/// <summary>
/// Per-request behavior of the correlation middleware wired as the first HTTP middleware in
/// Program.cs: a single shape-valid inbound x-correlation-id is echoed verbatim; missing, empty,
/// whitespace, overlong, illegal, comma-joined, or repeated inputs are discarded whole and
/// replaced by a generated 32-character lowercase hex id; the response header and the request
/// log scope carry the same id alongside the identity fields; and every pre-existing route
/// contract (SPA, health, dev Swagger, JWT 401/403, business error envelopes) is unchanged
/// while gaining the header.
/// </summary>
public sealed partial class ServiceMantleCorrelationTests : IClassFixture<QuaesturaApiFactory>, IDisposable
{
    private const string ValidValue = "e2e-correlation-0123456789abcdef";
    private const string IndexHtml =
        "<!doctype html><html><head><title>__APP_TITLE__</title></head><body><div id=\"app\"></div></body></html>";

    private readonly QuaesturaApiFactory _baseFactory;
    private readonly List<IDisposable> _disposables = [];

    public ServiceMantleCorrelationTests(QuaesturaApiFactory factory)
    {
        _baseFactory = factory;
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex GeneratedIdPattern();

    // ---------- acceptance/rejection rules ----------

    [Fact]
    public async Task ValidHeader_IsEchoedVerbatim()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationHeader, ValidValue);

        using var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(CorrelationHeader).Single().Should().Be(ValidValue);
    }

    [Theory]
    [InlineData("a")]                       // minimum length
    [InlineData("Z9._-mixed.CASE-0123456789")] // all legal separator characters
    public async Task ValidShapes_AreEchoedVerbatim(string value)
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationHeader, value);

        using var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(CorrelationHeader).Single().Should().Be(value);
    }

    [Fact]
    public async Task Exactly64Characters_IsEchoedVerbatim()
    {
        var value = new string('a', 64);
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationHeader, value);

        using var response = await client.GetAsync("/health");

        response.Headers.GetValues(CorrelationHeader).Single().Should().Be(value);
    }

    [Fact]
    public async Task MissingHeader_GeneratesNewId_PerRequest()
    {
        using var client = CreateClient();

        using var first = await client.GetAsync("/health");
        using var second = await client.GetAsync("/health");

        var firstId = first.Headers.GetValues(CorrelationHeader).Single();
        var secondId = second.Headers.GetValues(CorrelationHeader).Single();
        firstId.Should().MatchRegex(GeneratedIdPattern());
        secondId.Should().MatchRegex(GeneratedIdPattern());
        firstId.Should().NotBe(secondId);
    }

    [Theory]
    [InlineData("")]          // empty
    [InlineData(" ")]         // whitespace-only is rejected whole
    [InlineData("a b")]       // illegal character
    [InlineData("a,b")]       // comma-joined value
    [InlineData(".leading")]  // first character is not alphanumeric
    [InlineData("ill\u00e9gal")] // non-ASCII
    public async Task RejectedValue_IsReplacedByGeneratedId(string value)
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(CorrelationHeader, value);

        using var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(CorrelationHeader).Single().Should().MatchRegex(GeneratedIdPattern());
    }

    [Fact]
    public async Task OverlongValue_IsReplacedByGeneratedId()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(CorrelationHeader, new string('a', 65));

        using var response = await client.GetAsync("/health");

        response.Headers.GetValues(CorrelationHeader).Single().Should().MatchRegex(GeneratedIdPattern());
    }

    [Fact]
    public async Task RepeatedHeader_IsDiscardedWhole()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(CorrelationHeader, ValidValue);
        client.DefaultRequestHeaders.TryAddWithoutValidation(CorrelationHeader, "another-valid-id-1234");

        using var response = await client.GetAsync("/health");

        response.Headers.GetValues(CorrelationHeader).Single().Should().MatchRegex(GeneratedIdPattern());
    }

    // ---------- scope consistency, concurrency, cancellation ----------

    [Fact]
    public async Task ResponseHeader_AndLogScope_CarrySameCorrelationId()
    {
        var capture = new RequestScopeCapture();
        using var factory = CreateScopeCapturingFactory(capture);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationHeader, ValidValue);

        using var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var correlationId = response.Headers.GetValues(CorrelationHeader).Single();
        correlationId.Should().Be(ValidValue);

        // The scope opened by the middleware during this request carries the same id, together
        // with the identity fields of the host's ServiceLogContext.
        var scope = capture.Scopes.Single(fields =>
            ObservabilityTestHelpers.Field(fields, "CorrelationId") == correlationId);
        var logContext = factory.Services.GetRequiredService<ServiceLogContext>();
        ObservabilityTestHelpers.Field(scope, "ServiceName").Should().Be(logContext.ServiceName);
        ObservabilityTestHelpers.Field(scope, "ServiceVersion").Should().Be(logContext.ServiceVersion);
        ObservabilityTestHelpers.Field(scope, "InstanceId").Should().Be(logContext.InstanceId);
    }

    [Fact]
    public async Task ConcurrentRequests_HaveIsolatedScopes()
    {
        var capture = new RequestScopeCapture();
        using var factory = CreateScopeCapturingFactory(capture);
        using var client = factory.CreateClient();

        var echoed = Enumerable.Range(0, 4)
            .Select(i => $"conc-{i}-{Guid.NewGuid():N}")
            .ToList();
        var requests = echoed
            .Select(value => SendWithHeaderAsync(client, value))
            .Concat(Enumerable.Range(0, 4).Select(_ => SendWithHeaderAsync(client, headerValue: null)))
            .ToList();

        var responses = await Task.WhenAll(requests);

        // Every caller value is echoed on exactly its own response; the headerless requests each
        // receive a distinct generated id.
        responses.Take(4).Select(r => r.Header).Should().BeEquivalentTo(echoed);
        var generated = responses.Skip(4).Select(r => r.Header).ToList();
        generated.Should().OnlyContain(id => GeneratedIdPattern().IsMatch(id));
        var allIds = responses.Select(r => r.Header).ToList();
        allIds.Should().OnlyHaveUniqueItems();

        // The scopes opened during the eight requests carry exactly the eight response ids: no
        // scope leaked across requests.
        var scopeIds = capture.Scopes
            .Select(fields => ObservabilityTestHelpers.Field(fields, "CorrelationId"))
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct()
            .ToList();
        scopeIds.Should().BeEquivalentTo(allIds);
    }

    [Fact]
    public async Task DownstreamCancellation_Propagates_AndHostKeepsServing()
    {
        var hang = new HangingIdentityService();
        using var factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["IdentityService:AppId"] = "correlation-test-app",
                    ["IdentityService:AppSecret"] = "correlation-test-secret",
                }));
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(IdentityServiceClientOptions.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new HangingHandler(hang)));
        });
        Track(factory);
        using var client = factory.CreateClient();
        using var cts = new CancellationTokenSource();

        var login = client.PostAsync(
            "/admin/auth/login",
            Json("{\"username\":\"admin\",\"password\":\"pw\"}"),
            cts.Token);
        await hang.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        // The middleware never swallows cancellation: the aborted request surfaces as
        // OperationCanceledException on the client, and no response header is promised for an
        // aborted transport.
        await FluentActions.Awaiting(() => login).Should().ThrowAsync<OperationCanceledException>();
        await hang.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The released scope leaves the host fully functional for the next request.
        using var health = await client.GetAsync("/health");
        health.StatusCode.Should().Be(HttpStatusCode.OK);
        health.Headers.GetValues(CorrelationHeader).Single().Should().MatchRegex(GeneratedIdPattern());
    }

    // ---------- pre-existing route contracts gain the header ----------

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/login")]
    [InlineData("/questions/123")]
    [InlineData("/app.js")]
    public async Task SpaAndAssetPaths_KeepAnonymousContract_AndGainCorrelationHeader(string path)
    {
        using var spa = CreateSpaEnvironment();
        using var client = spa.Client;

        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(CorrelationHeader).Single().Should().MatchRegex(GeneratedIdPattern());
    }

    [Fact]
    public async Task SpaIndexHtml_StillInjectsAppTitle_WithCorrelationHeader()
    {
        using var spa = CreateSpaEnvironment();

        using var response = await spa.Client.GetAsync("/login");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("window.__APP_TITLE__");
        response.Headers.GetValues(CorrelationHeader).Single().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DevSwagger_KeepsContract_AndGainsCorrelationHeader()
    {
        using var factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            // The Development environment runs the OSS connectivity check; point it at a closed
            // local port so the check fails fast instead of resolving the dev container host.
            builder.UseSetting("Oss:InternalEndpoint", "127.0.0.1:1");
        });
        Track(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationHeader, ValidValue);

        using var response = await client.GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Quaestura WebAPI");
        response.Headers.GetValues(CorrelationHeader).Single().Should().Be(ValidValue);
    }

    [Fact]
    public async Task ProtectedApi_ValidAndInvalidJwt_Keep401And200Contracts_WithCorrelationHeader()
    {
        using var client = CreateClient();

        using var anonymous = await client.GetAsync("/admin/tags");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        anonymous.Headers.GetValues(CorrelationHeader).Single().Should().MatchRegex(GeneratedIdPattern());

        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/tags");
        invalidRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", QuaesturaApiFactory.CreateToken("https://untrusted-issuer.invalid"));
        using var invalid = await client.SendAsync(invalidRequest);
        invalid.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        invalid.Headers.GetValues(CorrelationHeader).Single().Should().MatchRegex(GeneratedIdPattern());

        using var validRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/tags");
        validRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study"));
        validRequest.Headers.Add(CorrelationHeader, ValidValue);
        using var valid = await client.SendAsync(validRequest);
        valid.StatusCode.Should().Be(HttpStatusCode.OK);
        valid.Headers.GetValues(CorrelationHeader).Single().Should().Be(ValidValue);
    }

    [Fact]
    public async Task Forbidden_Keeps403Envelope_WithCorrelationHeader()
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/tags")
        {
            Content = Json("{\"name\":\"correlation-probe\"}"),
        };
        // The factory token carries the student role; tag writes require a staff role, so the
        // endpoint throws ForbiddenException before any storage access.
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study"));
        request.Headers.Add(CorrelationHeader, ValidValue);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertEnvelope(response, "QUAESTURA_FORBIDDEN");
        response.Headers.GetValues(CorrelationHeader).Single().Should().Be(ValidValue);
    }

    [Fact]
    public async Task NotFound_Keeps404Envelope_WithCorrelationHeader()
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/admin/questions/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study"));
        request.Headers.Add(CorrelationHeader, ValidValue);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertEnvelope(response, "QUAESTURA_QUESTION_NOT_FOUND");
        response.Headers.GetValues(CorrelationHeader).Single().Should().Be(ValidValue);
    }

    [Fact]
    public async Task Validation400_KeepsEnvelope_WithCorrelationHeader()
    {
        using var client = CreateClient();

        using var response = await client.PostAsync("/admin/auth/login", Json("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertEnvelope(response, message: "Username and password are required.");
        response.Headers.GetValues(CorrelationHeader).Single().Should().MatchRegex(GeneratedIdPattern());
    }

    [Fact]
    public async Task Unhandled500_KeepsEnvelope_WithCorrelationHeader()
    {
        var throwingTags = new Mock<ITagService>();
        throwingTags
            .Setup(service => service.ListAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("correlation-probe stub failure"));
        using var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Scoped(_ => throwingTags.Object))));
        Track(factory);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/tags");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study"));
        request.Headers.Add(CorrelationHeader, ValidValue);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await AssertEnvelope(response, "QUAESTURA_INTERNAL_ERROR");
        response.Headers.GetValues(CorrelationHeader).Single().Should().Be(ValidValue);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private const string CorrelationHeader = ObservabilityTestHelpers.CorrelationHeaderName;

    private HttpClient CreateClient()
    {
        var client = _baseFactory.CreateClient();
        _disposables.Add(client);
        return client;
    }

    private void Track(IDisposable disposable) => _disposables.Add(disposable);

    private WebApplicationFactory<Program> CreateScopeCapturingFactory(RequestScopeCapture capture)
    {
        var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                ObservabilityTestHelpers.CaptureRequestScopes(services, capture)));
        Track(factory);
        return factory;
    }

    private SpaEnvironment CreateSpaEnvironment()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"quaestura-correlation-spa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), IndexHtml);
        File.WriteAllText(Path.Combine(webRoot, "app.js"), "console.log('app');");
        var factory = _baseFactory.WithWebHostBuilder(builder => builder.UseWebRoot(webRoot));
        Track(factory);
        return new SpaEnvironment(factory.CreateClient(), webRoot);
    }

    private sealed class SpaEnvironment : IDisposable
    {
        private readonly string _webRoot;

        public SpaEnvironment(HttpClient client, string webRoot)
        {
            Client = client;
            _webRoot = webRoot;
        }

        public HttpClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            Directory.Delete(_webRoot, recursive: true);
        }
    }

    private static async Task<(string Header, HttpResponseMessage Response)> SendWithHeaderAsync(
        HttpClient client, string? headerValue)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        if (headerValue is not null)
        {
            request.Headers.Add(CorrelationHeader, headerValue);
        }

        var response = await client.SendAsync(request);
        return (response.Headers.GetValues(CorrelationHeader).Single(), response);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task AssertEnvelope(
        HttpResponseMessage response, string? errorCode = null, string? message = null)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        if (errorCode is not null)
        {
            body.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
        }

        if (message is not null)
        {
            body.RootElement.GetProperty("message").GetString().Should().Be(message);
        }
    }

    private sealed class HangingIdentityService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class HangingHandler(HangingIdentityService state) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            state.Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                state.Aborted.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }
    }
}
