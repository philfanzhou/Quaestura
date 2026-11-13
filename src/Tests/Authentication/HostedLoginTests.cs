using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
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
using Quaestura.Common.Authentication;
using Xunit;

namespace Quaestura.Tests.Authentication;

[Collection(ConsoleLoggingCollection.Name)]
public sealed class HostedLoginTests
{
    [Fact]
    public async Task MissingConfiguration_NewRoutesAreUnavailable_WithoutProtocolState()
    {
        using var factory = new QuaesturaApiFactory();
        using var client = factory.CreateClient();
        Assert.Null(factory.Services.GetService<AdminOidcTicketStore>());
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
    [InlineData("Authority", "https://authority.test/synthetic-invalid-config-canary")]
    [InlineData("RedirectUri", "https://consumer.test/wrong")]
    [InlineData("PostLogoutRedirectUri", "https://consumer.test/wrong")]
    [InlineData("TicketCapacity", "synthetic-invalid-config-canary")]
    [InlineData("TicketCapacity", "0")]
    [InlineData("Scope", "profile synthetic-invalid-config-canary")]
    public void Complete_InvalidConfigurationFailsStartup_WithoutValue(string key, string value)
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
    public async Task AccessTokenAdmission_RejectsBeforeSessionStorage(string mode)
    {
        using var harness = new HostedLoginHarness();
        using var response = await harness.Login(mode);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        HostedLoginHarness.AssertNoSessionCookie(response);
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
        // The cookie value is a data-protected reference; the ticket itself is server-side only.
        var ticket = Assert.Single(harness.Store.Snapshot());
        Assert.Equal(HostedLoginTestAuthority.Subject, ticket.Principal.FindFirst("sub")!.Value);
        Assert.True(ticket.Principal.IsInRole("admin"));
        Assert.True(ticket.Properties.ExpiresUtc < ticket.Properties.IssuedUtc!.Value.AddMinutes(20)); // expires_in was 3600, token exp is 900.
        // Both tokens stay server-side in the ticket for the session's whole life (the stored
        // ID token is what the standard RP-initiated logout later presents as id_token_hint).
        Assert.Equal(harness.Authority.LastAccessToken, ticket.Properties.GetTokenValue("access_token"));
        Assert.Equal(harness.Authority.LastIdToken, ticket.Properties.GetTokenValue("id_token"));
        using var status = await harness.Send(HttpMethod.Get, "/admin/auth/oidc/session", session);
        using var statusBody = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.True(statusBody.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(0, statusBody.RootElement.GetProperty("authorization").GetInt32());
        Assert.Equal("Synthetic administrator", statusBody.RootElement.GetProperty("displayName").GetString());
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
        var started = await harness.Start(mode == "bad-nonce" || mode == "exchange-failed" ? mode : "admin");
        var callback = started.CallbackPath;
        if (mode == "bad-state") callback = callback.Replace("state=", "state=synthetic-wrong-state");
        if (mode == "bad-issuer") callback = callback.Replace(Uri.EscapeDataString(HostedLoginTestAuthority.Issuer), Uri.EscapeDataString("https://other.test"));
        if (mode == "denied") callback = callback.Replace("code=" + Uri.EscapeDataString(started.Code), "error=access_denied");
        if (mode == "duplicate-state") callback += "&state=synthetic-duplicate-state";
        using var response = await harness.Get(callback, started.CarrierCookies);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, harness.Store.Count);
        HostedLoginHarness.AssertNoSessionCookie(response);
        Assert.DoesNotContain("synthetic-code-canary", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OfficialCallback_RequiresTheOriginalBrowserCarrierCookies()
    {
        foreach (var defect in new[] { "missing", "wrong" })
        {
            using var harness = new HostedLoginHarness();
            var started = await harness.Start("admin");
            // The correlation cookie is the browser binding of the handshake: without it, or
            // with a forged value, no correlation can be established and nothing is exchanged.
            var correlation = started.CarrierCookies.Split("; ")
                .Single(cookie => cookie.StartsWith(".AspNetCore.Correlation."));
            var cookies = defect == "missing"
                ? string.Join("; ", started.CarrierCookies.Split("; ")
                    .Where(cookie => cookie.StartsWith(".AspNetCore.OpenIdConnect.Nonce.")))
                : string.Join("; ", started.CarrierCookies.Split("; ")
                    .Where(cookie => cookie.StartsWith(".AspNetCore.OpenIdConnect.Nonce.")))
                  + "; " + correlation.Split('=')[0] + "=" + new string('x', 43);
            using var response = await harness.Get(started.CallbackPath, cookies);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, harness.Authority.Exchanges);
            Assert.Equal(0, harness.Store.Count);
            HostedLoginHarness.AssertNoSessionCookie(response);
        }
        using var success = new HostedLoginHarness();
        var signIn = await success.Start("admin");
        using var signedIn = await success.Get(signIn.CallbackPath, signIn.CarrierCookies);
        Assert.Equal(HttpStatusCode.Found, signedIn.StatusCode);
        Assert.Equal(1, success.Store.Count);
        using var replay = await success.Get(signIn.CallbackPath, signIn.CarrierCookies);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal(1, success.Store.Count);
    }

    [Fact]
    public async Task OfficialCallback_IsSingleUse_AndCancellationDoesNotSignIn()
    {
        using var harness = new HostedLoginHarness();
        var started = await harness.Start("admin");
        using var first = await harness.Get(started.CallbackPath, started.CarrierCookies);
        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        using var replay = await harness.Get(started.CallbackPath, started.CarrierCookies);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        // The framework consumes the carrier cookies through the response; a manual replay still
        // reaches the token endpoint, where the authority refuses the one-time code.
        Assert.Equal(2, harness.Authority.Exchanges);
        Assert.Equal(1, harness.Store.Count);
        var canceled = await harness.Start("cancel-exchange");
        using var cancellation = new CancellationTokenSource();
        harness.Authority.ExchangeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = harness.Get(canceled.CallbackPath, canceled.CarrierCookies, cancellation.Token);
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
    public async Task Start_SendsExactAuthorizeContractWithPkceAndRegisteredRedirect()
    {
        using var harness = new HostedLoginHarness();
        var started = await harness.Start("admin");
        var query = started.AuthorizeQuery;
        Assert.Equal(new[] { "client_id", "code_challenge", "code_challenge_method", "nonce", "redirect_uri", "response_type", "scope", "state" }, query.Keys.Order().ToArray());
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("openid profile", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("https://consumer.test/admin/auth/oidc/callback", query["redirect_uri"]);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", query["code_challenge"]);
        // The framework's token request authenticates with client_secret_post; the secret stays
        // server-side on both sides of the exchange.
        using var signedIn = await harness.Get(started.CallbackPath, started.CarrierCookies);
        Assert.Equal(HttpStatusCode.Found, signedIn.StatusCode);
        var form = Assert.Single(harness.Authority.TokenForms);
        Assert.Equal(new[] { "client_id", "client_secret", "code", "code_verifier", "grant_type", "redirect_uri" }, form.Keys.Order().ToArray());
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(HostedLoginTestAuthority.Secret, form["client_secret"]);
    }

    [Fact]
    public async Task UnknownExpiredAndRestartedSessions_RequireReauthentication_WithoutApiRedirect()
    {
        using var harness = new HostedLoginHarness();
        var session = await harness.SignedIn();
        harness.Clock.Advance(TimeSpan.FromMinutes(20));
        foreach (var candidate in new[] { session, new BrowserSession(AdminOidcConstants.SessionCookie + "=unknown", null) })
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
        }
        var responses = await Task.WhenAll(harness.Send(HttpMethod.Post, logoutPath, session), harness.Send(HttpMethod.Post, logoutPath, session));
        try
        {
            Assert.Equal(0, harness.Store.Count);
            var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
            var preparedIndex = Array.FindIndex(bodies, body => body.Contains("logoutUrl"));
            Assert.True(preparedIndex >= 0);
            Assert.Single(bodies, body => body.Contains("local_only"));
            foreach (var response in responses) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Null(response.Headers.Location); SecurityResponseHeaders.AssertBaseline(response); }
            using var preparedBody = JsonDocument.Parse(bodies[preparedIndex]);
            Assert.Equal(2, preparedBody.RootElement.EnumerateObject().Count());
            // The standard RP-initiated logout: the authority's end-session endpoint carrying the
            // server-held ID token as id_token_hint, the registered post-logout URI, and a state.
            var logoutUrl = new Uri(preparedBody.RootElement.GetProperty("logoutUrl").GetString()!, UriKind.Absolute);
            Assert.Equal(harness.Authority.Origin + HostedLoginTestAuthority.EndSessionPath, logoutUrl.GetLeftPart(UriPartial.Path));
            var query = QueryHelpers.ParseQuery(logoutUrl.Query);
            Assert.Equal(harness.Authority.LastIdToken, query["id_token_hint"].ToString());
            Assert.Equal("https://consumer.test/admin/auth/oidc/logout/return", query["post_logout_redirect_uri"].ToString());
            Assert.False(string.IsNullOrEmpty(query["state"].ToString()));
            var cookies = responses[preparedIndex].Headers.GetValues("Set-Cookie").ToArray();
            Assert.Contains(cookies, cookie => cookie.StartsWith(AdminOidcConstants.SessionCookie + "=;") && cookie.Contains("1970"));
            var returnUrl = "/admin/auth/oidc/logout/return?state=" + Uri.EscapeDataString(query["state"].ToString());
            using var returned = await harness.Send(HttpMethod.Get, returnUrl, session);
            Assert.Equal(HttpStatusCode.Found, returned.StatusCode);
            Assert.Equal("/login", returned.Headers.Location!.OriginalString);
            using var replay = await harness.Send(HttpMethod.Get, returnUrl, session);
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
            Assert.DoesNotContain(query["state"].ToString(), await replay.Content.ReadAsStringAsync());
        }
        finally { foreach (var response in responses) response.Dispose(); }
        using var oldSession = await harness.Send(HttpMethod.Get, "/admin/tags", session);
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutEndSessionEndpointKeepsLocalRevocation_WithoutRetry()
    {
        using var harness = new HostedLoginHarness { Authority = { EndSessionUnavailable = true } };
        var session = await harness.SignedIn();
        using var logout = await harness.Send(HttpMethod.Post, "/admin/auth/oidc/logout", session);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Contains("local_only", await logout.Content.ReadAsStringAsync());
        Assert.Equal(0, harness.Store.Count);
        using var again = await harness.Send(HttpMethod.Post, "/admin/auth/oidc/logout", session);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("local_only", await again.Content.ReadAsStringAsync());
        using var oldSession = await harness.Send(HttpMethod.Get, "/admin/tags", session);
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutSessionCookieAnswersLocalOnly_AndTouchesNothing()
    {
        using var harness = new HostedLoginHarness();
        using var anonymous = await harness.Client.GetAsync("/admin/auth/oidc/csrf");
        var credentials = new BrowserSession(
            anonymous.Headers.GetValues("Set-Cookie").Single().Split(';')[0],
            JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString());
        using var logout = await harness.Send(HttpMethod.Post, "/admin/auth/oidc/logout", credentials);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Contains("local_only", await logout.Content.ReadAsStringAsync());
        Assert.Equal(0, harness.Store.Count);
    }

    [Fact]
    public async Task TicketStore_EvictsLeastRecentlyUsedAtCapacity_AndSweepServiceRegistered()
    {
        using var harness = new HostedLoginHarness("TicketCapacity", "1");
        var first = await harness.SignedIn();
        Assert.Equal(1, harness.Store.Count);
        using var secondLogin = await harness.Login("admin");
        Assert.Equal(HttpStatusCode.Found, secondLogin.StatusCode);
        Assert.Equal(1, harness.Store.Count);
        // The evicted session degrades to the fixed re-authentication answer instead of refusing
        // the new administrator (the declared difference from the replaced store).
        using var firstSession = await harness.Send(HttpMethod.Get, "/admin/tags", first);
        Assert.Equal(HttpStatusCode.Unauthorized, firstSession.StatusCode);
        Assert.Contains(harness.Factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            service => service is AdminOidcTicketStoreCleanupService);
    }

    [Fact]
    public async Task Logout_CancellationBeforeDispatchLeavesSessionUntouched()
    {
        using var harness = new HostedLoginHarness();
        var session = await harness.SignedIn();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Send(
            HttpMethod.Post, "/admin/auth/oidc/logout", session, cancellationToken: cancellation.Token));
        Assert.Equal(1, harness.Store.Count);
    }

    [Fact]
    public async Task Admission_CancellationBeforeValidation_DoesNotTouchStore()
    {
        using var harness = new HostedLoginHarness();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var admission = harness.Factory.Services.GetRequiredService<AdminSessionAdmission>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admission.AdmitAsync(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(), "synthetic-access-token", cancellation.Token));
        Assert.Equal(0, harness.Store.Count);
    }

    [Fact]
    public async Task Presentation_DoesNotRewriteStartedResponse_AndPropagatesCancellation()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new AlreadyStartedResponseFeature { StatusCode = 202 });
        using var body = new MemoryStream(); context.Response.Body = body;
        await AdminOidcResponses.WriteSignInFailureAsync(context, AdminOidcResponses.SignInFailedLanding, default);
        await AdminOidcResponses.WriteProblemAsync(context, 400, "signin_failed", "QUAESTURA_OIDC_SIGNIN_FAILED", "title", default);
        Assert.Equal(202, context.Response.StatusCode); Assert.Equal(0, body.Length);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AdminOidcResponses.WriteSignInFailureAsync(
            context, AdminOidcResponses.SignInFailedLanding, cancellation.Token));
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
        // The suite runs in one process, so ambient Activity state from earlier tests can make
        // the OpenTelemetry parent-based sampling decline to record this test's requests (its
        // spans then never reach the processor). One eager listener that forces recording keeps
        // the span export deterministic; the assertions below still exercise the real processor
        // output, and the OpenTelemetry-level request filters still drop what they must.
        var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _)
                => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
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
            foreach (var canary in new[] { "synthetic-code-canary", "synthetic-code-trace-canary", "synthetic-state-trace-canary", "synthetic-nonce-trace-canary", HostedLoginTestAuthority.Secret, harness.Authority.LastAccessToken, harness.Authority.LastIdToken })
                Assert.False(actual.Contains(canary, StringComparison.Ordinal), "A synthetic protocol canary reached an observability sink.");
            Assert.DoesNotContain(spans, span => span.Contains("/admin/auth/oidc"));
            Assert.DoesNotContain(spans, span => span.StartsWith("Client ") && span.Contains(authority.Origin));
        }
        finally { listener.Dispose(); Console.SetOut(original); }
    }
}

internal sealed record BrowserSession(string Cookie, string? Csrf);

internal sealed record StartedSignIn(string CallbackPath, string CarrierCookies, Dictionary<string, string> AuthorizeQuery, string Code);

internal sealed class HostedLoginHarness : IDisposable
{
    internal readonly HostedLoginTestAuthority Authority;
    internal readonly HostedLoginTestClock Clock = new();
    internal readonly WebApplicationFactory<Program> Factory;
    private HttpClient? _client;
    internal HttpClient Client => _client ??= CreateClient();
    internal AdminOidcTicketStore Store => Factory.Services.GetRequiredService<AdminOidcTicketStore>();
    internal HostedLoginHarness(string? overrideKey = null, string? overrideValue = null, ConcurrentQueue<string>? spans = null, HostedLoginTestAuthority? authority = null, bool realHttp = false, string? browserWebRoot = null, IDictionary<string, string?>? overrides = null)
    {
        Authority = authority ?? new HostedLoginTestAuthority();
        Factory = new QuaesturaApiFactory().WithWebHostBuilder(builder =>
        {
            foreach (var item in new Dictionary<string, string>
            {
                ["AdminOidc:Authority"] = Authority.Origin,
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
            foreach (var item in overrides ?? new Dictionary<string, string?>()) builder.UseSetting("AdminOidc:" + item.Key, item.Value!);
            if (overrideKey is not null) builder.UseSetting("AdminOidc:" + overrideKey, overrideValue!);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                services.AddHttpClient(AdminOidcConstants.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => realHttp ? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false } : Authority);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.MapInboundClaims = false;
                    options.Configuration = new OpenIdConnectConfiguration { Issuer = Authority.Origin };
                    options.Configuration.SigningKeys.Add(Authority.Key);
                    // Capture explicit fixture trust through the production factory as the Host does.
                    options.TokenValidationParameters = IdentityTokenValidationParametersFactory.Create(
                        new IdentityAuthenticationOptions
                        {
                            Authority = Authority.Origin,
                            Issuer = Authority.Origin,
                            Audience = options.TokenValidationParameters.ValidAudience!,
                            ClockSkewSeconds = (int)options.TokenValidationParameters.ClockSkew.TotalSeconds
                        }, [Authority.Key], roleClaimType: "role");
                });
                if (spans is not null) services.ConfigureOpenTelemetryTracerProvider((_, builder) => builder.AddProcessor(new SimpleActivityExportProcessor(new SpanCollector(spans))));
            });
        });
    }
    internal HttpClient CreateClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://consumer.test"), AllowAutoRedirect = false, HandleCookies = false });
    internal async Task<StartedSignIn> Start(string mode)
    {
        Authority.Mode = mode;
        using var start = await Client.GetAsync("/admin/auth/oidc/start?returnUrl=%2Fquestions");
        Assert.Equal(HttpStatusCode.Found, start.StatusCode); SecurityResponseHeaders.AssertBaseline(start);
        Assert.Equal(Authority.Origin + "/oauth2/authorize", start.Headers.Location!.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query).ToDictionary(
            pair => pair.Key, pair => pair.Value.ToString());
        Authority.Nonce = query["nonce"]!;
        Authority.LastCodeChallenge = query["code_challenge"]!;
        // The framework's carrier cookies: the correlation and nonce pair, both HttpOnly and
        // single-purpose like the replaced client's per-state binding cookie was.
        var cookies = start.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0]).ToArray();
        Assert.Contains(cookies, cookie => cookie.StartsWith(".AspNetCore.Correlation."));
        Assert.Contains(cookies, cookie => cookie.StartsWith(".AspNetCore.OpenIdConnect.Nonce."));
        // A fresh single-use authorization code, minted exactly like a real authority's.
        var code = Authority.IssueCode();
        return new StartedSignIn(
            "/admin/auth/oidc/callback?code=" + Uri.EscapeDataString(code) + "&state="
                + Uri.EscapeDataString(query["state"]!) + "&iss=" + Uri.EscapeDataString(Authority.Origin),
            string.Join("; ", cookies),
            query,
            code);
    }
    internal async Task<HttpResponseMessage> Login(string mode)
    {
        var started = await Start(mode);
        return await Get(started.CallbackPath, started.CarrierCookies);
    }
    internal async Task<HttpResponseMessage> Get(string path, string? cookie = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return await Client.SendAsync(request, cancellationToken);
    }
    internal static void AssertNoSessionCookie(HttpResponseMessage response)
    {
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values : Array.Empty<string>();
        Assert.DoesNotContain(cookies, cookie =>
            cookie.StartsWith(AdminOidcConstants.SessionCookie + "=", StringComparison.Ordinal));
    }
    internal async Task<BrowserSession> SignedIn()
    {
        using var login = await Login("admin"); Assert.Equal(HttpStatusCode.Found, login.StatusCode);
        Assert.Equal("/questions", login.Headers.Location!.OriginalString);
        var sessionSet = login.Headers.GetValues("Set-Cookie").Single(cookie =>
            cookie.StartsWith(AdminOidcConstants.SessionCookie + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", sessionSet.ToLowerInvariant()); Assert.Contains("secure", sessionSet.ToLowerInvariant()); Assert.Contains("samesite=lax", sessionSet.ToLowerInvariant());
        var cookie = sessionSet.Split(';')[0];
        using var csrf = await Send(HttpMethod.Get, "/admin/auth/oidc/csrf", new BrowserSession(cookie, null));
        using var body = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        return new BrowserSession(cookie + "; " + csrf.Headers.GetValues("Set-Cookie").Single().Split(';')[0], body.RootElement.GetProperty("token").GetString());
    }
    internal async Task<HttpResponseMessage> Send(HttpMethod method, string path, BrowserSession session, object? body = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path); request.Headers.Add("Cookie", session.Cookie);
        if (session.Csrf is not null) request.Headers.Add(AdminOidcConstants.CsrfHeader, session.Csrf);
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
