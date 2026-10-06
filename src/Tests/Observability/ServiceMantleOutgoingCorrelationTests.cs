using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Quaestura.Host.Authentication;
using Quaestura.Tests.Authentication;
using SignaCore.Client.AspNetCore;
using Xunit;

namespace Quaestura.Tests.Observability;

[Collection(ConsoleLoggingCollection.Name)]
public sealed class ServiceMantleOutgoingCorrelationTests
{
    private const string CorrelationHeaderName = "x-correlation-id";
    private const string ProtectedApiRoute = "/admin/tags";

    private static WebApplicationFactory<Program> CreateFactory(Action<IServiceCollection> configureTestServices)
    {
        return new QuaesturaApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AdminOidc:Authority", HostedLoginTestAuthority.Issuer);
            builder.UseSetting("AdminOidc:ClientId", HostedLoginTestAuthority.ClientId);
            builder.UseSetting("AdminOidc:ClientSecret", HostedLoginTestAuthority.Secret);
            builder.UseSetting("AdminOidc:RedirectUri", "https://consumer.test/admin/auth/oidc/callback");
            builder.UseSetting("AdminOidc:PostLogoutRedirectUri", "https://consumer.test/admin/auth/oidc/logout/return");
            builder.ConfigureTestServices(configureTestServices);
        });
    }

    [Fact]
    public async Task ProductionClient_UsesResolvedSlot_PreservesExplicitHeader_AndIsolatesPooledRequests()
    {
        var capture = new CaptureHandler();
        await using var factory = CreateFactory(configureTestServices: services =>
        {
            services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => capture);
            services.AddHttpClient("external").ConfigurePrimaryHttpMessageHandler(() => capture);
            services.AddSingleton<IAuthorizationMiddlewareResultHandler, OutgoingProbe>();
        });
        using var client = factory.CreateClient();
        var tasks = Enumerable.Range(0, 16).Select(async index =>
        {
            var id = $"incoming-correlation-{index:D2}";
            using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute + $"?probe={index}");
            request.Headers.Add(CorrelationHeaderName, id);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(id, response.Headers.GetValues(CorrelationHeaderName).Single());
            Assert.Equal(new[] { id }, capture.Headers[index.ToString()]);
            Assert.Empty(capture.Headers[$"external-{index}"]);
        });
        await Task.WhenAll(tasks);

        using var explicitRequest = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute + "?probe=explicit&explicit=true");
        explicitRequest.Headers.Add(CorrelationHeaderName, "incoming-explicit-probe");
        using var explicitResponse = await client.SendAsync(explicitRequest);
        Assert.Equal(HttpStatusCode.OK, explicitResponse.StatusCode);
        Assert.Equal(new[] { "outgoing-explicit-value" }, capture.Headers["explicit"]);

        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute + "?probe=invalid");
        invalidRequest.Headers.TryAddWithoutValidation(CorrelationHeaderName, "rejected, input");
        using var invalidResponse = await client.SendAsync(invalidRequest);
        var resolved = invalidResponse.Headers.GetValues(CorrelationHeaderName).Single();
        Assert.Matches("^[0-9a-f]{32}$", resolved);
        Assert.Equal(new[] { resolved }, capture.Headers["invalid"]);

        using var background = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(SignaCoreHostedLoginDefaults.HttpClientName);
        using var backgroundResponse = await background.GetAsync("https://authority.test/background");
        Assert.Empty(capture.Headers["background"]);
    }

    [Fact]
    public async Task ProductionClient_CallerCancellationAtCompletion_DisposesResponse_AndKeepsToken()
    {
        using var cancellation = new CancellationTokenSource();
        var capture = new CaptureHandler(cancellation);
        await using var factory = CreateFactory(configureTestServices: services =>
        {
            services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => capture);
            services.AddSingleton(cancellation);
            services.AddSingleton<IAuthorizationMiddlewareResultHandler, CancellationProbe>();
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        request.Headers.Add(CorrelationHeaderName, "completion-cancel-probe");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cancelled", await response.Content.ReadAsStringAsync());
        Assert.True(capture.Content!.Disposed);
        Assert.Equal(new[] { "completion-cancel-probe" }, capture.Headers["cancel"]);
        using var selected = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(SignaCoreHostedLoginDefaults.HttpClientName);
        var before = capture.Headers.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selected.GetAsync("https://authority.test/entry", cancellation.Token));
        Assert.Equal(before, capture.Headers.Count);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => selected.GetAsync("https://authority.test/failure"));
        Assert.Equal("synthetic inner failure", failure.Message);
    }

    [Fact]
    public async Task ActualHostedLogin_DiscoveryAndTokenExchange_ReceiveTheirRequestCorrelation()
    {
        using var harness = new HostedLoginHarness();
        var capture = new ForwardingCapture(harness.Authority);
        using var factory = harness.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => capture)));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://consumer.test"), AllowAutoRedirect = false, HandleCookies = false });
        using var start = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/oidc/start?returnUrl=%2Fquestions");
        start.Headers.Add(CorrelationHeaderName, "actual-signacore-start");
        using var startResponse = await client.SendAsync(start);
        Assert.Equal(HttpStatusCode.Found, startResponse.StatusCode);
        Assert.Equal(new[] { "actual-signacore-start" }, capture.Headers["/.well-known/openid-configuration"]);
        var query = QueryHelpers.ParseQuery(startResponse.Headers.Location!.Query);
        harness.Authority.Nonce = query["nonce"]!;
        using var callback = new HttpRequestMessage(HttpMethod.Get,
            "/admin/auth/oidc/callback?code=synthetic-code-canary&state=" + Uri.EscapeDataString(query["state"]!)
            + "&iss=" + Uri.EscapeDataString(harness.Authority.Origin));
        callback.Headers.Add(CorrelationHeaderName, "actual-signacore-callback");
        using var callbackResponse = await client.SendAsync(callback);
        Assert.Equal(HttpStatusCode.Found, callbackResponse.StatusCode);
        Assert.Equal(new[] { "actual-signacore-callback" }, capture.Headers["/oauth2/token"]);
        Assert.Equal(1, harness.Authority.Exchanges);
    }

    private sealed class ForwardingCapture(HostedLoginTestAuthority authority) : HttpMessageHandler
    {
        internal ConcurrentDictionary<string, string[]> Headers { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(request.Options.TryGetValue(AdminOidcBackchannelMarker.SensitiveRequest, out var sensitive) && sensitive);
            Headers[request.RequestUri!.AbsolutePath] = request.Headers.GetValues(CorrelationHeaderName).ToArray();
            return authority.Forward(request, cancellationToken);
        }
    }

    private sealed class OutgoingProbe : IAuthorizationMiddlewareResultHandler
    {
        public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            var probe = context.Request.Query["probe"].ToString();
            // Mutating the inbound header after middleware resolution must not replace its private slot.
            context.Request.Headers[CorrelationHeaderName] = "downstream-mutated-inbound";
            var factory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
            using var selected = factory.CreateClient(SignaCoreHostedLoginDefaults.HttpClientName);
            using var outgoing = new HttpRequestMessage(HttpMethod.Get, "https://authority.test/" + probe);
            if (context.Request.Query.ContainsKey("explicit")) outgoing.Headers.Add(CorrelationHeaderName, "outgoing-explicit-value");
            using var response = await selected.SendAsync(outgoing, context.RequestAborted);
            using var external = factory.CreateClient("external");
            using var externalResponse = await external.GetAsync("http://external.test/external-" + probe, context.RequestAborted);
            await context.Response.WriteAsync("sent");
        }
    }

    private sealed class CancellationProbe : IAuthorizationMiddlewareResultHandler
    {
        public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            var cancellation = context.RequestServices.GetRequiredService<CancellationTokenSource>();
            using var selected = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(SignaCoreHostedLoginDefaults.HttpClientName);
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selected.GetAsync("https://authority.test/cancel", cancellation.Token));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            await context.Response.WriteAsync("cancelled");
        }
    }

    private sealed class CaptureHandler(CancellationTokenSource? cancellation = null) : HttpMessageHandler
    {
        public ConcurrentDictionary<string, string[]> Headers { get; } = new();
        public TrackedContent? Content { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (request.RequestUri!.AbsolutePath == "/failure") throw new InvalidOperationException("synthetic inner failure");
            if (request.RequestUri.Host == "authority.test")
                Assert.True(request.Options.TryGetValue(AdminOidcBackchannelMarker.SensitiveRequest, out var sensitive) && sensitive);
            Headers[request.RequestUri!.AbsolutePath.TrimStart('/')] = request.Headers.TryGetValues(CorrelationHeaderName, out var values) ? values.ToArray() : [];
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Content = new TrackedContent() };
            cancellation?.Cancel();
            return response;
        }
    }

    private sealed class TrackedContent : StringContent
    {
        public TrackedContent() : base("synthetic") { }
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
}
