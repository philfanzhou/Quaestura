using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Xunit;
using SignaCore.Client.AspNetCore;

namespace Quaestura.Tests.Authentication;

// Opt-in Playwright process fixture: the product Host runs unchanged on Kestrel/5007.
// Fake-provider controls belong to this test server only, never to the product.
[Collection("Hosted login observability")]
public sealed class BrowserHostFixture
{
    [BrowserFixtureFact]
    public async Task ServeBrowserHost()
    {
        var webRoot = Environment.GetEnvironmentVariable("QUAESTURA_BROWSER_WEBROOT");
        Assert.False(string.IsNullOrEmpty(webRoot));
        Assert.True(File.Exists(Path.Combine(webRoot, "index.html")), "Build the frontend before browser tests.");
        using var authority = new HostedLoginTestAuthority { Origin = "http://127.0.0.1:5008" };
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); names.AddDnsName("localhost");
        certificateRequest.CertificateExtensions.Add(names.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        HostedLoginHarness StartHost()
        {
            var host = new HostedLoginHarness(authority: authority, realHttp: true, browserWebRoot: webRoot);
            host.Factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 5009, endpoint => endpoint.UseHttps(certificate)));
            host.Factory.StartServer();
            return host;
        }
        var harness = StartHost();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(authority.Origin);
        await using var provider = builder.Build();
        provider.MapGet("/fixture/mode", (HttpContext context) =>
        {
            harness.Clock.Reset();
            authority.Mode = context.Request.Query["signin"].ToString() is { Length: > 0 } mode ? mode : "admin";
            authority.LogoutMode = context.Request.Query["logout"].ToString() is { Length: > 0 } logout ? logout : "success";
            if (authority.Mode == "unreachable") { harness.Dispose(); harness = StartHost(); }
            return Results.Ok();
        });
        provider.MapGet("/fixture/status", () => Results.Json(new { sessionCookieName = SignaCoreHostedLoginDefaults.SessionCookieName, tickets = harness.Store.Count, prepares = authority.Prepares, exchanges = authority.Exchanges }));
        provider.MapGet("/fixture/expire", () => { harness.Clock.Advance(TimeSpan.FromMinutes(20)); return Results.Ok(); });
        provider.MapGet("/fixture/restart", () => { harness.Dispose(); harness = StartHost(); return Results.Ok(); });
        provider.MapGet("/fixture/stop", () => { stopped.TrySetResult(); return Results.Ok(); });
        provider.MapGet("/oauth2/authorize", (HttpContext context) =>
        {
            authority.Nonce = context.Request.Query["nonce"]!;
            var fields = new Dictionary<string, string?> { [authority.Mode == "cancelled" ? "error" : "code"] = authority.Mode == "cancelled" ? "access_denied" : "synthetic-code-canary", ["state"] = context.Request.Query["state"], ["iss"] = authority.Origin };
            return Results.Redirect(QueryHelpers.AddQueryString("https://127.0.0.1:5009/admin/auth/oidc/callback", fields));
        });
        provider.MapGet("/oauth2/logout", () => Results.Redirect(QueryHelpers.AddQueryString("https://127.0.0.1:5009/admin/auth/oidc/logout/return", "state", authority.LogoutState)));
        provider.MapFallback(async context =>
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), authority.Origin + context.Request.Path + context.Request.QueryString);
            if (context.Request.ContentLength is > 0) request.Content = new StreamContent(context.Request.Body);
            try
            {
                using var response = await authority.Forward(request, context.RequestAborted);
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            catch (HttpRequestException) { context.Response.StatusCode = 503; }
        });
        await provider.StartAsync();
        try { await stopped.Task.WaitAsync(TimeSpan.FromMinutes(15)); }
        finally { harness.Dispose(); }
    }
}

internal sealed class BrowserFixtureFactAttribute : FactAttribute
{
    public BrowserFixtureFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QUAESTURA_BROWSER_WEBROOT")))
            Skip = "Process fixture is started by frontend npm test; browser assertions run there.";
    }
}
