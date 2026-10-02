using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Quaestura.Host.Authentication;
using SignaCore.Client.AspNetCore;
using Quaestura.Database;
using Xunit;

namespace Quaestura.Tests.Authentication;

public sealed class HostedLoginPresentationTests
{
    private static readonly IServiceProvider Services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
    [Theory]
    [InlineData("text/html", "navigate", "document", true)]
    [InlineData("text/html;q=0,application/json", "navigate", "document", false)]
    [InlineData("application/json", "navigate", "document", false)]
    [InlineData("text/html", "cors", "document", false)]
    [InlineData("text/html", "navigate", "iframe", false)]
    [InlineData("text/html", "", "", false)]
    public async Task Writer_OnlyProjectsRealHtmlNavigation(string accept, string mode, string destination, bool redirect)
    {
        foreach (var reason in Enum.GetValues<SignaCoreSignInReason>())
        {
            var context = Context("/admin/auth/oidc/callback", accept, mode, destination);
            await new AdminOidcResponseWriter().WriteSignInFailureAsync(context, reason, default);
            Assert.Equal(redirect ? 302 : reason == SignaCoreSignInReason.AuthorityUnreachable ? 503 : 400, context.Response.StatusCode);
            if (redirect)
            {
                var expected = reason switch
                {
                    SignaCoreSignInReason.AccessDenied => "cancelled",
                    SignaCoreSignInReason.AuthorityUnreachable => "provider_unavailable",
                    SignaCoreSignInReason.RequiresReauthentication => "requires_reauthentication",
                    _ => "signin_failed"
                };
                Assert.Equal("/login?reason=" + expected, context.Response.Headers.Location);
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
        using var request = Navigation(await harness.Start(mode));
        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/login?reason=" + reason, response.Headers.Location!.OriginalString);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, harness.Store.Count);
        SecurityResponseHeaders.AssertBaseline(response);
    }

    [Theory]
    [InlineData("/admin/auth/oidc/start?returnUrl=%2Fadmin%2Fauth%2Foidc%2Fstart", "signin_failed")]
    [InlineData("/admin/auth/oidc/start?returnUrl=%2F&returnUrl=%2Ftags", "signin_failed")]
    [InlineData("/admin/auth/oidc/callback?error=access_denied&state=synthetic-query-canary", "cancelled")]
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

    [Fact]
    public async Task Return_OfficialSingleUseAndCookieDeletionPrecedeHtmlProjection()
    {
        using var harness = new HostedLoginHarness("PostLogoutReturnPath", "/login?reason=signed_out");
        var session = await harness.SignedIn();
        using var logout = await harness.Send(HttpMethod.Post, "/admin/auth/oidc/logout", session);
        var correlation = logout.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith(SignaCoreHostedLoginDefaults.SessionCookieName + SignaCoreHostedLoginDefaults.LogoutReturnCookieSuffix + "="));
        var path = "/admin/auth/oidc/logout/return?state=" + Uri.EscapeDataString(harness.Authority.LogoutState);
        using var request = Navigation(path);
        request.Headers.Add("Cookie", correlation.Split(';')[0]);
        using var returned = await harness.Client.SendAsync(request);
        Assert.Equal("/login?reason=signed_out", returned.Headers.Location!.OriginalString);
        Assert.Contains(returned.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains("1970"));
        using var replayRequest = Navigation(path);
        replayRequest.Headers.Add("Cookie", correlation.Split(';')[0]);
        using var replay = await harness.Client.SendAsync(replayRequest);
        Assert.Equal("/login?reason=logout_failed", replay.Headers.Location!.OriginalString);
        Assert.Contains(replay.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains("1970"));
        using var apiReplay = await harness.Send(HttpMethod.Get, path, new BrowserSession(correlation.Split(';')[0], null));
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AdminOidcResponseWriter().WriteSignInFailureAsync(context, SignaCoreSignInReason.AccessDenied, cancellation.Token));
        Assert.Equal(200, context.Response.StatusCode);
        context.Features.Set<IHttpResponseFeature>(new StartedFeature { StatusCode = 202 });
        await new AdminOidcResponseWriter().WriteSignInFailureAsync(context, SignaCoreSignInReason.AccessDenied, default);
        Assert.Equal(202, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Headers.Location.Count);
        var business = Context("/admin/tags", "text/html", "navigate", "document");
        Assert.False(AdminOidcResponseWriter.IsHtmlNavigation(business));
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
