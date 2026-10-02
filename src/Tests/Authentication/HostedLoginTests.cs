using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Quaestura.Host.Authentication;
using SignaCore.Client.AspNetCore;
using Xunit;

namespace Quaestura.Tests.Authentication;

[Collection(ConsoleLoggingCollection.Name)]
public sealed class HostedLoginTests
{
    [Fact]
    public async Task Disabled_NewRoutesAreUnavailable_WithoutPackageState()
    {
        using var factory = new QuaesturaApiFactory();
        using var client = factory.CreateClient();
        Assert.Null(factory.Services.GetService<ITicketStore>());
        foreach (var path in new[] { "start?returnUrl=%2Fadmin%2Fauth%2Foidc%2Fstart", "callback?code=synthetic-disabled-code&state=synthetic-disabled-state", "session", "csrf", "signin-failed", "logout/return", "other" })
        {
            using var response = await client.GetAsync(AdminOidcComposition.Prefix + "/" + path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            SecurityResponseHeaders.AssertBaseline(response);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        using var logout = await client.PostAsync(AdminOidcComposition.Prefix + "/logout", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, logout.StatusCode);
        using var bearer = new HttpRequestMessage(HttpMethod.Get, "/admin/tags");
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study"));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(bearer)).StatusCode);
    }

    [Theory]
    [InlineData("ClientSecret", "")]
    [InlineData("Authority", "https://authority.test/synthetic-invalid-config-canary")]
    [InlineData("RedirectUri", "https://consumer.test/wrong")]
    [InlineData("PostLogoutRedirectUri", "https://consumer.test/wrong")]
    [InlineData("ClientId", "")]
    [InlineData("TicketCapacity", "synthetic-invalid-config-canary")]
    [InlineData("Enabled", "synthetic-invalid-config-canary")]
    public void Enabled_InvalidConfigurationFailsStartup_WithoutValue(string key, string value)
    {
        using var harness = new HostedLoginHarness(key, value);
        var error = Assert.ThrowsAny<Exception>(() => harness.CreateClient());
        Assert.Contains(key, error.ToString());
        Assert.DoesNotContain("synthetic-invalid-config-canary", error.ToString());
    }

    [Theory]
    [InlineData("nonadmin")]
    [InlineData("bad-signature")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-issuer")]
    [InlineData("expired")]
    [InlineData("subject-mismatch")]
    [InlineData("missing-subject")]
    [InlineData("duplicate-subject")]
    [InlineData("empty-subject")]
    [InlineData("missing-role")]
    [InlineData("wrong-algorithm")]
    [InlineData("missing-issuer")]
    [InlineData("duplicate-issuer")]
    public async Task AccessTokenAdmission_RejectsBeforeOfficialStorage(string mode)
    {
        using var harness = new HostedLoginHarness();
        using var response = await harness.Login(mode);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, harness.Store.Count);
        SecurityResponseHeaders.AssertBaseline(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("QUAESTURA_ADMIN_DENIED", body.RootElement.GetProperty("quaesturaErrorCode").GetString());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task AdminSession_UsesVerifiedIdentityForRealTagBusiness_AndCsrf()
    {
        using var harness = new HostedLoginHarness();
        var session = await harness.SignedIn();
        var key = session.Cookie.Split(';')[0].Split('=')[1];
        var ticket = await harness.Store.RetrieveAsync(key, default);
        Assert.NotNull(ticket);
        Assert.Equal(HostedLoginTestAuthority.Subject, ticket.Principal.FindFirst("sub")!.Value);
        Assert.True(ticket.Principal.IsInRole("admin"));
        Assert.True(ticket.ExpiresUtc < ticket.IssuedUtc.AddMinutes(20)); // expires_in was 3600, token exp is 900.
        using var status = await harness.Send(HttpMethod.Get, "/admin/auth/oidc/session", session);
        using var statusBody = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.True(statusBody.RootElement.GetProperty("authenticated").GetBoolean());
        SecurityResponseHeaders.AssertBaseline(status);
        foreach (var csrf in new string?[] { null, "synthetic-wrong-csrf" })
        {
            using var denied = await harness.Send(HttpMethod.Post, "/admin/tags", session with { Csrf = csrf }, new { name = "csrf-denied" });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var empty = await harness.Send(HttpMethod.Get, "/admin/tags", session);
        using var emptyBody = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
        Assert.Equal(0, emptyBody.RootElement.GetProperty("total").GetInt32());
        using var created = await harness.Send(HttpMethod.Post, "/admin/tags", session, new { name = "hosted-tag" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdBody.RootElement.GetProperty("data").GetProperty("id").GetString();
        using var updated = await harness.Send(HttpMethod.Post, "/admin/tags", session, new { id, name = "updated-hosted-tag" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var tag = await harness.Send(HttpMethod.Get, "/admin/tags/" + id, session);
        using var tagBody = JsonDocument.Parse(await tag.Content.ReadAsStringAsync());
        Assert.Equal(HostedLoginTestAuthority.Subject, tagBody.RootElement.GetProperty("data").GetProperty("createdBy").GetString());
    }

    [Fact]
    public async Task Bearer_RemainsHeaderFirst_AndRetainsRolesOwnershipAndNoCsrfWrites()
    {
        using var harness = new HostedLoginHarness();
        var session = await harness.SignedIn();
        foreach (var header in new[] { "Bearer invalid", "Basic invalid" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/tags");
            request.Headers.Add("Cookie", session.Cookie);
            request.Headers.TryAddWithoutValidation("Authorization", header);
            using var invalid = await harness.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
            Assert.Null(invalid.Headers.Location);
        }
        // HttpClient omits an empty value during transfer; exercise an explicitly present empty
        // header on the actual server request so cookie fallback cannot conceal it.
        var emptyHeader = await harness.Factory.Server.SendAsync(context =>
        {
            context.Request.Method = "GET"; context.Request.Path = "/admin/tags";
            context.Request.Headers["Cookie"] = session.Cookie;
            context.Request.Headers["Authorization"] = new Microsoft.Extensions.Primitives.StringValues("");
        });
        Assert.Equal(401, emptyHeader.Response.StatusCode);
        using var studentRead = await harness.Bearer(HttpMethod.Get, "/admin/tags", "student");
        Assert.Equal(HttpStatusCode.OK, studentRead.StatusCode);
        using var studentWrite = await harness.Bearer(HttpMethod.Post, "/admin/tags", "student", new { name = "student-write" });
        Assert.Equal(HttpStatusCode.Forbidden, studentWrite.StatusCode);
        using var teacherWrite = await harness.Bearer(HttpMethod.Post, "/admin/tags", "teacher", new { name = "teacher-write" });
        Assert.Equal(HttpStatusCode.OK, teacherWrite.StatusCode);
        using var body = JsonDocument.Parse(await teacherWrite.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("data").GetProperty("id").GetString();
        using var other = await harness.Bearer(HttpMethod.Post, "/admin/tags", "admin", new { id, name = "stolen-tag" }, "synthetic-other-admin");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        using var owner = await harness.Bearer(HttpMethod.Post, "/admin/tags", "teacher", new { id, name = "own-tag" });
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        // The existing resource-server audience is unchanged, and distinct from the hosted ClientId.
        using var wrongAudience = new HttpRequestMessage(HttpMethod.Get, "/admin/tags");
        wrongAudience.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.Authority.Token());
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.SendAsync(wrongAudience)).StatusCode);
    }

    [Theory]
    [InlineData("bad-state")]
    [InlineData("bad-issuer")]
    [InlineData("bad-nonce")]
    [InlineData("exchange-failed")]
    [InlineData("denied")]
    [InlineData("duplicate-state")]
    public async Task OfficialCallbackFailures_NeverCreateSession(string mode)
    {
        using var harness = new HostedLoginHarness();
        var callback = await harness.Start(mode == "bad-nonce" || mode == "exchange-failed" ? mode : "admin");
        if (mode == "bad-state") callback = callback.Replace("state=", "state=synthetic-wrong-state");
        if (mode == "bad-issuer") callback = callback.Replace(Uri.EscapeDataString(HostedLoginTestAuthority.Issuer), Uri.EscapeDataString("https://other.test"));
        if (mode == "denied") callback = callback.Replace("code=synthetic-code-canary", "error=access_denied");
        if (mode == "duplicate-state") callback += "&state=synthetic-duplicate-state";
        using var response = await harness.Client.GetAsync(callback);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, harness.Store.Count);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("synthetic-code-canary", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OfficialCallback_IsSingleUse_AndCancellationDoesNotSignIn()
    {
        using var harness = new HostedLoginHarness();
        var callback = await harness.Start("admin");
        using var first = await harness.Client.GetAsync(callback);
        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        using var replay = await harness.Client.GetAsync(callback);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal(1, harness.Authority.Exchanges);
        Assert.Equal(1, harness.Store.Count);
        var canceledCallback = await harness.Start("cancel-exchange");
        using var cancellation = new CancellationTokenSource();
        harness.Authority.ExchangeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = harness.Client.GetAsync(canceledCallback, cancellation.Token);
        await harness.Authority.ExchangeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, harness.Store.Count);
    }

    [Theory]
    [InlineData("https://evil.test")]
    [InlineData("//evil.test")]
    [InlineData("/\\evil.test")]
    [InlineData("/admin/auth/oidc/start")]
    [InlineData("/admin/auth/oidc/callback")]
    public async Task Start_RejectsUnsafeOrLoopingReturnUrl(string returnUrl)
    {
        using var harness = new HostedLoginHarness();
        using var response = await harness.Client.GetAsync("/admin/auth/oidc/start?returnUrl=" + Uri.EscapeDataString(returnUrl));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, harness.Store.Count);
        Assert.Equal(0, harness.Authority.DiscoveryRequests);
    }

    [Fact]
    public async Task Start_DuplicateReturnUrlAndUnavailableAuthorityFailClosed()
    {
        using var harness = new HostedLoginHarness();
        using var duplicate = await harness.Client.GetAsync("/admin/auth/oidc/start?returnUrl=%2F&returnUrl=%2Fquestions");
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        harness.Authority.Mode = "unreachable";
        using var unavailable = await harness.Client.GetAsync("/admin/auth/oidc/start");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal(0, harness.Store.Count);
    }

    [Fact]
    public async Task UnknownExpiredAndRestartedSessions_RequireReauthentication_WithoutApiRedirect()
    {
        using var harness = new HostedLoginHarness();
        var session = await harness.SignedIn();
        harness.Clock.Advance(TimeSpan.FromMinutes(20));
        foreach (var candidate in new[] { session, new BrowserSession(SignaCoreHostedLoginDefaults.SessionCookieName + "=unknown", null) })
        {
            using var status = await harness.Send(HttpMethod.Get, "/admin/auth/oidc/session", candidate);
            using var body = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.True(body.RootElement.GetProperty("requiresReauthentication").GetBoolean());
            using var protectedResponse = await harness.Send(HttpMethod.Get, "/admin/tags", candidate);
            Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
            Assert.Null(protectedResponse.Headers.Location);
        }
        Assert.Equal(0, harness.Store.Count);
        using var restarted = new HostedLoginHarness();
        using var afterRestart = await restarted.Send(HttpMethod.Get, "/admin/tags", session);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRestart.StatusCode);
    }

    [Theory]
    [InlineData("/admin/auth/oidc/logout")]
    [InlineData("/ADMIN/AUTH/OIDC/LOGOUT/")]
    public async Task Logout_CsrfRejectsWithoutSideEffects_ConcurrentPrepareOnce_AndReturnIsSingleUse(string logoutPath)
    {
        using var harness = new HostedLoginHarness();
        var session = await harness.SignedIn();
        foreach (var csrf in new string?[] { null, "synthetic-wrong-csrf" })
        {
            using var denied = await harness.Send(HttpMethod.Post, logoutPath, session with { Csrf = csrf });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(1, harness.Store.Count);
            Assert.Equal(0, harness.Authority.Prepares);
        }
        var responses = await Task.WhenAll(harness.Send(HttpMethod.Post, logoutPath, session), harness.Send(HttpMethod.Post, logoutPath, session));
        try
        {
            Assert.Equal(1, harness.Authority.Prepares);
            Assert.Equal(0, harness.Store.Count);
            var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
            var preparedIndex = Array.FindIndex(bodies, body => body.Contains("logoutUrl"));
            Assert.True(preparedIndex >= 0);
            Assert.Single(bodies, body => body.Contains("local_only"));
            foreach (var response in responses) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Null(response.Headers.Location); SecurityResponseHeaders.AssertBaseline(response); }
            using var preparedBody = JsonDocument.Parse(bodies[preparedIndex]);
            Assert.Equal(2, preparedBody.RootElement.EnumerateObject().Count());
            Assert.Equal(harness.Authority.Origin + "/oauth2/logout?logout_handle=" + new string('a', 43),
                preparedBody.RootElement.GetProperty("logoutUrl").GetString());
            var cookies = responses[preparedIndex].Headers.GetValues("Set-Cookie").ToArray();
            Assert.Contains(cookies, cookie => cookie.StartsWith(SignaCoreHostedLoginDefaults.SessionCookieName + "=;") && cookie.Contains("1970"));
            var correlation = cookies.Single(cookie => cookie.StartsWith(SignaCoreHostedLoginDefaults.SessionCookieName + SignaCoreHostedLoginDefaults.LogoutReturnCookieSuffix + "="));
            var returnedSession = new BrowserSession(correlation.Split(';')[0], null);
            var returnUrl = "/admin/auth/oidc/logout/return?state=" + Uri.EscapeDataString(harness.Authority.LogoutState);
            using var returned = await harness.Send(HttpMethod.Get, returnUrl, returnedSession);
            Assert.Equal(HttpStatusCode.Found, returned.StatusCode);
            Assert.Equal("/login", returned.Headers.Location!.OriginalString);
            using var replay = await harness.Send(HttpMethod.Get, returnUrl, returnedSession);
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
            Assert.DoesNotContain(harness.Authority.LogoutState, await replay.Content.ReadAsStringAsync());
        }
        finally { foreach (var response in responses) response.Dispose(); }
        using var oldSession = await harness.Send(HttpMethod.Get, "/admin/tags", session);
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("forged")]
    [InlineData("timeout")]
    public async Task Logout_UpstreamFailuresKeepLocalRevocation_WithoutRetry(string mode)
    {
        using var harness = new HostedLoginHarness();
        var session = await harness.SignedIn(); harness.Authority.LogoutMode = mode;
        using var logout = await harness.Send(HttpMethod.Post, "/admin/auth/oidc/logout", session);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Contains("local_only", await logout.Content.ReadAsStringAsync());
        Assert.Equal(1, harness.Authority.Prepares); Assert.Equal(0, harness.Store.Count);
        using var oldSession = await harness.Send(HttpMethod.Get, "/admin/tags", session);
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
    }

    [Fact]
    public async Task Logout_CancellationAfterRevoke_DoesNotRestoreSessionOrRetry()
    {
        using var harness = new HostedLoginHarness();
        var session = await harness.SignedIn(); harness.Authority.LogoutMode = "cancel";
        using var cancellation = new CancellationTokenSource();
        var pending = harness.Send(HttpMethod.Post, "/admin/auth/oidc/logout", session, cancellationToken: cancellation.Token);
        await harness.Authority.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, harness.Store.Count); Assert.Equal(1, harness.Authority.Prepares);
        using var oldSession = await harness.Send(HttpMethod.Get, "/admin/tags", session);
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
    }

    [Fact]
    public async Task OfficialFactory_RetainsCapacityAndCleanupService()
    {
        using var harness = new HostedLoginHarness("TicketCapacity", "1");
        using var first = await harness.Login("admin");
        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        using var full = await harness.Login("admin");
        Assert.Equal(HttpStatusCode.BadRequest, full.StatusCode);
        Assert.False(full.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, harness.Store.Count);
        Assert.Contains(harness.Factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            service => service.GetType().Name == "TicketStoreCleanupService");
    }

    [Fact]
    public async Task Logout_CancellationBeforeDispatchLeavesSessionUntouched()
    {
        using var harness = new HostedLoginHarness();
        var session = await harness.SignedIn();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Send(
            HttpMethod.Post, "/admin/auth/oidc/logout", session, cancellationToken: cancellation.Token));
        Assert.Equal(1, harness.Store.Count); Assert.Equal(0, harness.Authority.Prepares);
    }

    [Fact]
    public async Task Admission_CancellationBeforeStore_DoesNotDelegate()
    {
        using var harness = new HostedLoginHarness();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var admission = harness.Factory.Services.GetRequiredService<ITicketStore>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admission.StoreAsync(
            new SignaCoreSessionTicket(new ClaimsPrincipal(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "invalid", "invalid"), cancellation.Token));
        Assert.Equal(0, harness.Store.Count);
    }

    [Fact]
    public async Task ResponseWriter_DoesNotRewriteStartedResponse_AndPropagatesCancellation()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new AlreadyStartedResponseFeature { StatusCode = 202 });
        using var body = new MemoryStream(); context.Response.Body = body;
        var writer = new AdminOidcResponseWriter();
        await writer.WriteSessionStatusAsync(context, SignaCoreSessionStatus.Expired, default);
        await writer.WriteSignInFailureAsync(context, SignaCoreSignInReason.InvalidToken, default);
        Assert.Equal(202, context.Response.StatusCode); Assert.Equal(0, body.Length);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteSessionStatusAsync(
            context, SignaCoreSessionStatus.Expired, cancellation.Token));
        Assert.Equal(0, body.Length);
    }

    private sealed class AlreadyStartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    [Fact]
    public async Task RealHost_LogsAndExportedSpansNeverContainProtocolCanaries()
    {
        var original = Console.Out; using var output = new StringWriter(); Console.SetOut(output);
        try
        {
            var spans = new ConcurrentQueue<string>();
            var authority = new HostedLoginTestAuthority();
            var providerBuilder = WebApplication.CreateBuilder();
            providerBuilder.Logging.ClearProviders();
            providerBuilder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            await using var provider = providerBuilder.Build();
            provider.Run(async context =>
            {
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                    authority.Origin + context.Request.Path + context.Request.QueryString);
                if (context.Request.ContentLength is > 0) request.Content = new StreamContent(context.Request.Body);
                using var response = await authority.Forward(request, context.RequestAborted);
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            });
            await provider.StartAsync();
            authority.Origin = provider.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var harness = new HostedLoginHarness(spans: spans, authority: authority, realHttp: true);
            var session = await harness.SignedIn();
            using var business = await harness.Send(HttpMethod.Get, "/admin/tags", session);
            using var logout = await harness.Send(HttpMethod.Post, "/admin/auth/oidc/logout", session);
            using var badCallback = await harness.Client.GetAsync("/admin/auth/oidc/callback?code=synthetic-code-trace-canary&state=synthetic-state-trace-canary&nonce=synthetic-nonce-trace-canary");
            harness.Factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
            Assert.NotEmpty(spans);
            Assert.Contains(spans, span => span.Contains("/admin/tags"));
            foreach (var actual in new[] { output.ToString(), string.Join("\n", spans) })
            foreach (var canary in new[] { "synthetic-code-canary", "synthetic-code-trace-canary", "synthetic-state-trace-canary", "synthetic-nonce-trace-canary", HostedLoginTestAuthority.Secret, harness.Authority.LastAccessToken, harness.Authority.LastIdToken, harness.Authority.LogoutState })
                Assert.False(actual.Contains(canary, StringComparison.Ordinal), "A synthetic protocol canary reached an observability sink.");
            Assert.DoesNotContain(spans, span => span.Contains("/admin/auth/oidc"));
            Assert.DoesNotContain(spans, span => span.StartsWith("Client ") && span.Contains(authority.Origin));
        }
        finally { Console.SetOut(original); }
    }
}

internal sealed record BrowserSession(string Cookie, string? Csrf);

internal sealed class HostedLoginHarness : IDisposable
{
    internal readonly HostedLoginTestAuthority Authority;
    internal readonly HostedLoginTestClock Clock = new();
    internal readonly WebApplicationFactory<Program> Factory;
    private HttpClient? _client;
    internal HttpClient Client => _client ??= CreateClient();
    internal InMemoryTicketStore Store => (InMemoryTicketStore)((AdminSessionAdmission)Factory.Services.GetRequiredService<ITicketStore>()).InnerStore;
    internal HostedLoginHarness(string? overrideKey = null, string? overrideValue = null, ConcurrentQueue<string>? spans = null, HostedLoginTestAuthority? authority = null, bool realHttp = false, string? browserWebRoot = null)
    {
        Authority = authority ?? new HostedLoginTestAuthority();
        Factory = new QuaesturaApiFactory().WithWebHostBuilder(builder =>
        {
            foreach (var item in new Dictionary<string, string>
            {
                ["AdminOidc:Enabled"] = "true", ["AdminOidc:Authority"] = Authority.Origin,
                ["AdminOidc:ClientId"] = HostedLoginTestAuthority.ClientId, ["AdminOidc:ClientSecret"] = HostedLoginTestAuthority.Secret,
                ["AdminOidc:RedirectUri"] = "https://consumer.test/admin/auth/oidc/callback",
                ["AdminOidc:PostLogoutRedirectUri"] = "https://consumer.test/admin/auth/oidc/logout/return", ["AdminOidc:PostLogoutReturnPath"] = "/login"
            }) builder.UseSetting(item.Key, item.Value);
            if (browserWebRoot is not null)
            {
                builder.UseWebRoot(browserWebRoot);
                builder.UseSetting("AdminOidc:RedirectUri", "https://127.0.0.1:5009/admin/auth/oidc/callback");
                builder.UseSetting("AdminOidc:PostLogoutRedirectUri", "https://127.0.0.1:5009/admin/auth/oidc/logout/return");
                builder.UseSetting("AdminOidc:PostLogoutReturnPath", "/login?reason=signed_out");
            }
            if (overrideKey is not null) builder.UseSetting("AdminOidc:" + overrideKey, overrideValue!);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => realHttp ? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false } : Authority);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.MapInboundClaims = false;
                    options.Configuration = new OpenIdConnectConfiguration { Issuer = Authority.Origin };
                    options.Configuration.SigningKeys.Add(Authority.Key);
                    options.TokenValidationParameters.ValidIssuers = [Authority.Origin];
                    options.TokenValidationParameters.IssuerSigningKeys = [Authority.Key];
                    options.TokenValidationParameters.RoleClaimType = "role";
                });
                if (spans is not null) services.ConfigureOpenTelemetryTracerProvider((_, builder) => builder.AddProcessor(new SimpleActivityExportProcessor(new SpanCollector(spans))));
            });
        });
    }
    internal HttpClient CreateClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://consumer.test"), AllowAutoRedirect = false, HandleCookies = false });
    internal async Task<string> Start(string mode)
    {
        Authority.Mode = mode;
        using var start = await Client.GetAsync("/admin/auth/oidc/start?returnUrl=%2Fquestions");
        Assert.Equal(HttpStatusCode.Found, start.StatusCode); SecurityResponseHeaders.AssertBaseline(start);
        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query); Authority.Nonce = query["nonce"]!;
        return "/admin/auth/oidc/callback?code=synthetic-code-canary&state=" + Uri.EscapeDataString(query["state"]!) + "&iss=" + Uri.EscapeDataString(Authority.Origin);
    }
    internal async Task<HttpResponseMessage> Login(string mode) => await Client.GetAsync(await Start(mode));
    internal async Task<BrowserSession> SignedIn()
    {
        using var login = await Login("admin"); Assert.Equal(HttpStatusCode.Found, login.StatusCode);
        Assert.Equal("/questions", login.Headers.Location!.OriginalString);
        var sessionSet = login.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", sessionSet.ToLowerInvariant()); Assert.Contains("secure", sessionSet.ToLowerInvariant()); Assert.Contains("samesite=lax", sessionSet.ToLowerInvariant());
        var cookie = sessionSet.Split(';')[0];
        using var csrf = await Send(HttpMethod.Get, "/admin/auth/oidc/csrf", new BrowserSession(cookie, null));
        using var body = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        return new BrowserSession(cookie + "; " + csrf.Headers.GetValues("Set-Cookie").Single().Split(';')[0], body.RootElement.GetProperty("token").GetString());
    }
    internal async Task<HttpResponseMessage> Send(HttpMethod method, string path, BrowserSession session, object? body = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path); request.Headers.Add("Cookie", session.Cookie);
        if (session.Csrf is not null) request.Headers.Add(SignaCoreHostedLoginDefaults.AntiforgeryHeaderName, session.Csrf);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request, cancellationToken);
    }
    internal async Task<HttpResponseMessage> Bearer(HttpMethod method, string path, string role, object? body = null, string subject = HostedLoginTestAuthority.Subject)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Authority.Token(role, subject, "QuantumZhou.microservices"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }
    public void Dispose() { _client?.Dispose(); Factory.Dispose(); }
    private sealed class SpanCollector(ConcurrentQueue<string> spans) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) spans.Enqueue(activity.Kind + " " + activity.DisplayName + " " + string.Join(" ", activity.TagObjects.Select(tag => tag.Key + "=" + tag.Value)));
            return ExportResult.Success;
        }
    }
}
