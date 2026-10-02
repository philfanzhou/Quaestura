using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using SignaCore.Client.AspNetCore;
using Xunit;

namespace Quaestura.Tests.Authentication;

[Collection(ConsoleLoggingCollection.Name)]
public sealed class RetiredPasswordPipelineTests
{
    [Theory]
    [InlineData("/admin/auth/login", "application/x-www-form-urlencoded")]
    [InlineData("/ADMIN/AUTH/LOGIN/", "application/x-www-form-urlencoded")]
    [InlineData("/admin/auth/login", "application/json")]
    [InlineData("/ADMIN/AUTH/LOGIN/", "application/json")]
    [InlineData("/admin/auth/login", "text/plain")]
    [InlineData("/ADMIN/AUTH/LOGIN/", "text/plain")]
    [InlineData("/admin/auth/login", "")]
    [InlineData("/ADMIN/AUTH/LOGIN/", "")]
    public async Task ValidOpaqueCookie_RetiredPostNeverReadsBodyDuringAuthentication(string path, string contentType)
    {
        var original = Console.Out; using var output = new StringWriter(); Console.SetOut(output);
        try
        {
            using var harness = new HostedLoginHarness();
            var session = await harness.SignedIn();
            var exchanges = harness.Authority.Exchanges; var discoveries = harness.Authority.DiscoveryRequests;
            using var body = new UnreadPasswordBody(contentType.Length == 0 ? "" : "password=synthetic-retired-canary&token=synthetic-retired-canary{malformed");
            var context = await harness.Factory.Server.SendAsync(context =>
            {
                context.Request.Scheme = "https"; context.Request.Method = "POST"; context.Request.Path = path;
                context.Request.Headers.Cookie = session.Cookie; // Contains both opaque session and antiforgery cookies.
                context.Request.ContentType = contentType; context.Request.ContentLength = body.Length; context.Request.Body = body;
            });
            Assert.Equal(410, context.Response.StatusCode); Assert.Equal(0, body.ReadCount);
            Assert.Equal(1, harness.Store.Count); Assert.Equal(exchanges, harness.Authority.Exchanges);
            Assert.Equal(discoveries, harness.Authority.DiscoveryRequests); Assert.Equal(0, harness.Authority.Prepares);
            Assert.Equal("no-store", context.Response.Headers.CacheControl);
            using var status = await harness.Send(HttpMethod.Get, "/admin/auth/oidc/session", session);
            using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.False(output.ToString().Contains("synthetic-retired-canary"), "Retired body reached a log sink.");
            // Exact route metadata cannot spread to protected business/logout or a nearby unmatched path.
            foreach (var unsafePath in new[] { "/admin/tags", "/admin/auth/oidc/logout", "/admin/auth/login/extra" })
            {
                using var response = await harness.Send(HttpMethod.Post, unsafePath, session with { Csrf = null }, new { name = "must-not-create", user_id = "synthetic-user" });
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
            Assert.Equal(1, harness.Store.Count); Assert.Equal(0, harness.Authority.Prepares);
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public async Task CancelledRetiredRequest_DoesNotReadBodyOrChangeSession()
    {
        using var harness = new HostedLoginHarness();
        var session = await harness.SignedIn();
        using var body = new UnreadPasswordBody("password=synthetic-cancelled-retired-canary");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try
        {
            await harness.Factory.Server.SendAsync(context =>
            {
                context.Request.Scheme = "https"; context.Request.Method = "POST"; context.Request.Path = "/admin/auth/login";
                context.Request.Headers.Cookie = session.Cookie; context.Request.ContentType = "application/x-www-form-urlencoded";
                context.Request.ContentLength = body.Length; context.Request.Body = body; context.RequestAborted = cancellation.Token;
            });
        }
        catch (OperationCanceledException) { /* A disconnected caller need not receive 410. */ }
        Assert.Equal(0, body.ReadCount); Assert.Equal(1, harness.Store.Count);
        Assert.Equal(1, harness.Authority.Exchanges); Assert.Equal(0, harness.Authority.Prepares);
    }

    [Fact]
    public async Task ExplicitValidBearer_WithCookieWritesWithoutCsrf_AndRetainsOwnership()
    {
        using var harness = new HostedLoginHarness();
        var session = await harness.SignedIn();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/tags");
        request.Headers.Add("Cookie", session.Cookie);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            harness.Authority.Token("teacher", "synthetic-bearer-owner", "QuantumZhou.microservices"));
        request.Content = System.Net.Http.Json.JsonContent.Create(new { name = "synthetic-bearer-cookie-tag" });
        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("data").GetProperty("id").GetString();
        using var detail = await harness.Send(HttpMethod.Get, "/admin/tags/" + id, session);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailBody = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal("synthetic-bearer-owner", detailBody.RootElement.GetProperty("data").GetProperty("createdBy").GetString());
        Assert.Equal(1, harness.Store.Count);
    }

    [Fact]
    public async Task ExplicitBearer_RetiredEndpointNeverFetchesColdMetadata_WhileBusinessStillValidates()
    {
        using var harness = new HostedLoginHarness();
        var options = harness.Factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        var configuration = options.Configuration!;
        var metadata = new CountingMetadata(configuration);
        options.Configuration = null; options.ConfigurationManager = metadata;
        foreach (var path in new[] { "/admin/auth/login", "/ADMIN/AUTH/LOGIN/" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", harness.Authority.Token(audience: "QuantumZhou.microservices"));
            using var response = await harness.Client.SendAsync(request);
            await AdminAuthEndpointsTests.AssertRetired(response);
        }
        Assert.Equal(0, metadata.Reads);
        using var protectedResponse = await harness.Bearer(HttpMethod.Get, "/admin/tags", "student");
        Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
        Assert.True(metadata.Reads > 0, "The existing resource-server metadata path must still run on business requests.");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExistingBearerMessageReceivedEvent_IsPreservedOnlyOnNonRetiredRoutes(bool hostedConfigured)
    {
        using var harness = new HostedLoginHarness("ClientSecret", hostedConfigured ? HostedLoginTestAuthority.Secret : null);
        var events = 0;
        using var factory = harness.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.Events.OnMessageReceived = context =>
                {
                    Interlocked.Increment(ref events);
                    return Task.CompletedTask;
                })));
        using var client = factory.CreateClient();
        using var retired = new HttpRequestMessage(HttpMethod.Post, "/ADMIN/AUTH/LOGIN/");
        retired.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            harness.Authority.Token(audience: "QuantumZhou.microservices"));
        using var retiredResponse = await client.SendAsync(retired);
        await AdminAuthEndpointsTests.AssertRetired(retiredResponse);
        Assert.Equal(0, events);
        using var business = new HttpRequestMessage(HttpMethod.Get, "/admin/tags");
        business.Headers.Authorization = retired.Headers.Authorization;
        using var businessResponse = await client.SendAsync(business);
        Assert.Equal(HttpStatusCode.OK, businessResponse.StatusCode);
        Assert.Equal(1, events);
    }

    private sealed class CountingMetadata(OpenIdConnectConfiguration configuration) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        internal int Reads;
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
        { Interlocked.Increment(ref Reads); return Task.FromResult(configuration); }
        public void RequestRefresh() { }
    }

    [Fact]
    public async Task JwtDisguisedAsOfficialCookie_CannotAuthenticate()
    {
        using var harness = new HostedLoginHarness();
        using var response = await harness.Send(HttpMethod.Get, "/admin/tags",
            new BrowserSession(SignaCoreHostedLoginDefaults.SessionCookieName + "=" + harness.Authority.Token(audience: "QuantumZhou.microservices"), null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal(0, harness.Store.Count);
    }
}
