using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Quaestura.Host.Authentication;
using Quaestura.Database;
using Xunit;

namespace Quaestura.Tests.Authentication;

public sealed class HostedLoginPresentationTests
{
    private static readonly IServiceProvider Services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
    private static readonly string[] Landings =
    [
        AdminOidcResponses.CancelledLanding,
        AdminOidcResponses.DeniedLanding,
        AdminOidcResponses.ProviderUnavailableLanding,
        AdminOidcResponses.RequiresReauthenticationLanding,
        AdminOidcResponses.SignInFailedLanding
    ];

    [Theory]
    [InlineData("text/html", "navigate", "document", true)]
    [InlineData("text/html;q=0,application/json", "navigate", "document", false)]
    [InlineData("application/json", "navigate", "document", false)]
    [InlineData("text/html", "cors", "document", false)]
    [InlineData("text/html", "navigate", "iframe", false)]
    [InlineData("text/html", "", "", false)]
    public async Task Writer_OnlyProjectsRealHtmlNavigation(string accept, string mode, string destination, bool redirect)
    {
        foreach (var landing in Landings)
        {
            var context = Context("/admin/auth/oidc/callback", accept, mode, destination);
            await AdminOidcResponses.WriteSignInFailureAsync(context, landing, default);
            Assert.Equal(redirect ? 302 : landing == AdminOidcResponses.DeniedLanding ? 403
                : landing == AdminOidcResponses.ProviderUnavailableLanding ? 503 : 400, context.Response.StatusCode);
            if (redirect)
            {
                Assert.Equal("/login?reason=" + landing, context.Response.Headers.Location);
            }
            else Assert.Equal(0, context.Response.Headers.Location.Count);
            Assert.DoesNotContain("synthetic-query-canary", context.Response.Headers.Location.ToString());
        }
    }

    [Theory]
    [InlineData("nonadmin", "denied")]
    [InlineData("bad-nonce", "signin_failed")]
    [InlineData("exchange-failed", "signin_failed")]
    public async Task Callback_HtmlFailureKeepsAdmissionAndSecurityHeaders(string mode, string reason)
    {
        using var harness = new HostedLoginHarness();
        var started = await harness.Start(mode);
        using var request = Navigation(started.CallbackPath);
        request.Headers.Add("Cookie", started.CarrierCookies);
        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/login?reason=" + reason, response.Headers.Location!.OriginalString);
        HostedLoginHarness.AssertNoSessionCookie(response);
        Assert.Equal(0, harness.Store.Count);
        SecurityResponseHeaders.AssertBaseline(response);
    }

    [Theory]
    [InlineData("/admin/auth/oidc/start?returnUrl=%2Fadmin%2Fauth%2Foidc%2Fstart", "signin_failed")]
    [InlineData("/admin/auth/oidc/start?returnUrl=%2F&returnUrl=%2Ftags", "signin_failed")]
    // The generic handler validates the correlation (the state) before it classifies an error
    // response, so a cancelled response with an unusable state lands on the generic failure
    // instead of the cancelled landing; a real cancellation carries a valid state.
    [InlineData("/admin/auth/oidc/callback?error=access_denied&state=synthetic-query-canary", "signin_failed")]
    [InlineData("/admin/auth/oidc/logout/return?state=synthetic-query-canary", "logout_failed")]
    public async Task Navigation_FailureHasOnlyFixedReason(string path, string reason)
    {
        using var harness = new HostedLoginHarness();
        using var request = Navigation(path);
        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/login?reason=" + reason, response.Headers.Location!.OriginalString);
        Assert.DoesNotContain("synthetic-query-canary", await response.Content.ReadAsStringAsync());
        SecurityResponseHeaders.AssertBaseline(response);
        Assert.Equal(0, harness.Store.Count);
    }

    [Theory]
    [InlineData("access_denied", "cancelled")]
    [InlineData("user_canceled", "cancelled")]
    [InlineData("pre_sign_in_denied", "denied")]
    [InlineData("authority_unreachable", "provider_unavailable")]
    [InlineData("requires_reauthentication", "requires_reauthentication")]
    [InlineData("synthetic-unknown-wire-reason", "signin_failed")]
    public async Task FailurePage_RendersHistoricalWireReasons(string wireReason, string landing)
    {
        using var harness = new HostedLoginHarness();
        using var request = Navigation("/admin/auth/oidc/signin-failed?reason=" + Uri.EscapeDataString(wireReason));
        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/login?reason=" + landing, response.Headers.Location!.OriginalString);
        SecurityResponseHeaders.AssertBaseline(response);
    }

    [Fact]
    public async Task Return_OfficialSingleUsePrecedesHtmlProjection()
    {
        using var harness = new HostedLoginHarness("PostLogoutReturnPath", "/login?reason=signed_out");
        var session = await harness.SignedIn();
        using var logout = await harness.Send(HttpMethod.Post, "/admin/auth/oidc/logout", session);
        var logoutUrl = new Uri(JsonDocument.Parse(await logout.Content.ReadAsStringAsync())
            .RootElement.GetProperty("logoutUrl").GetString()!, UriKind.Absolute);
        var state = QueryHelpers.ParseQuery(logoutUrl.Query)["state"].ToString();
        var path = "/admin/auth/oidc/logout/return?state=" + Uri.EscapeDataString(state);
        using var request = Navigation(path);
        using var returned = await harness.Client.SendAsync(request);
        Assert.Equal("/login?reason=signed_out", returned.Headers.Location!.OriginalString);
        using var replayRequest = Navigation(path);
        using var replay = await harness.Client.SendAsync(replayRequest);
        Assert.Equal("/login?reason=logout_failed", replay.Headers.Location!.OriginalString);
        using var apiReplay = await harness.Send(HttpMethod.Get, path, session);
        Assert.Equal(HttpStatusCode.BadRequest, apiReplay.StatusCode);
        Assert.Null(apiReplay.Headers.Location);
    }

    [Fact]
    public async Task AllSharedClientUnsafeEndpoints_RejectMissingAndWrongCsrfBeforeBusinessEffects()
    {
        using var harness = new HostedLoginHarness();
        var session = await harness.SignedIn();
        var id = Guid.NewGuid().ToString();
        foreach (var csrf in new string?[] { null, "synthetic-wrong-csrf" })
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "/admin/tags", new { name = "must-not-create" }),
            (HttpMethod.Delete, "/admin/tags/" + id, null),
            (HttpMethod.Post, "/admin/knowledges", new { name = "must-not-create", subject = 1, grade = 7 }),
            (HttpMethod.Delete, "/admin/knowledges/" + id, null),
            (HttpMethod.Delete, "/admin/questions/" + id, null)
        })
        {
            using var response = await harness.Send(method, path, session with { Csrf = csrf }, body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using var scope = harness.Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<QuaesturaDbContext>();
        Assert.Empty(database.Tags); Assert.Empty(database.Knowledges); Assert.Empty(database.Questions);
        Assert.Equal(1, harness.Store.Count);
    }

    [Fact]
    public async Task CancellationAndStartedResponse_AreNotRewritten()
    {
        var context = Context("/admin/auth/oidc/callback", "text/html", "navigate", "document");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AdminOidcResponses.WriteSignInFailureAsync(
            context, AdminOidcResponses.CancelledLanding, cancellation.Token));
        Assert.Equal(200, context.Response.StatusCode);
        context.Features.Set<IHttpResponseFeature>(new StartedFeature { StatusCode = 202 });
        await AdminOidcResponses.WriteSignInFailureAsync(context, AdminOidcResponses.CancelledLanding, default);
        Assert.Equal(202, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Headers.Location.Count);
        var business = Context("/admin/tags", "text/html", "navigate", "document");
        Assert.False(AdminOidcResponses.IsHtmlNavigation(business));
    }
    private sealed class StartedFeature : HttpResponseFeature { public override bool HasStarted => true; }
    private static DefaultHttpContext Context(string path, string accept, string mode, string destination)
    {
        var context = new DefaultHttpContext { RequestServices = Services };
        context.Request.Method = "GET"; context.Request.Path = path;
        context.Request.QueryString = new QueryString("?code=synthetic-query-canary&state=synthetic-query-canary");
        context.Request.Headers.Accept = accept; context.Request.Headers["Sec-Fetch-Mode"] = mode; context.Request.Headers["Sec-Fetch-Dest"] = destination;
        context.Response.Body = new MemoryStream();
        return context;
    }
    private static HttpRequestMessage Navigation(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Accept", "text/html"); request.Headers.Add("Sec-Fetch-Mode", "navigate"); request.Headers.Add("Sec-Fetch-Dest", "document");
        return request;
    }
}
