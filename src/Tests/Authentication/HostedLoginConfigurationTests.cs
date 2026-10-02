using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quaestura.Host.Authentication;
using SignaCore.Client.AspNetCore;
using Xunit;

namespace Quaestura.Tests.Authentication;

[Collection(ConsoleLoggingCollection.Name)]
public sealed class HostedLoginConfigurationTests
{
    [Theory]
    [InlineData("Authority", null)] [InlineData("Authority", "")] [InlineData("Authority", "  ")]
    [InlineData("ClientId", null)] [InlineData("ClientId", "")] [InlineData("ClientId", "  ")]
    [InlineData("ClientSecret", null)] [InlineData("ClientSecret", "")] [InlineData("ClientSecret", "  ")]
    [InlineData("RedirectUri", null)] [InlineData("RedirectUri", "")] [InlineData("RedirectUri", "  ")]
    public async Task AnyMissingRequiredField_StartsSafely_WithoutAnyOfficialState(string key, string? value)
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "quaestura-58-spa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html><head><title>__APP_TITLE__</title></head><body>synthetic-spa</body></html>");
        var original = Console.Out;
        using var output = new StringWriter(); Console.SetOut(output);
        try
        {
            // Missing fields take precedence over malformed optional configuration.
            using var harness = new HostedLoginHarness(key, value, browserWebRoot: webRoot,
                overrides: new Dictionary<string, string?> { ["TicketCapacity"] = "synthetic-invalid-config-canary", ["Scope"] = "profile synthetic-invalid-config-canary", ["Enabled"] = "false" });
            using var client = harness.CreateClient();
            var services = harness.Factory.Services;
            Assert.False(services.GetRequiredService<AdminOidcConfigurationStatus>().IsConfigured);
            Assert.Null(services.GetService<ITicketStore>());
            foreach (var name in new[] { "PendingSignInStore", "LogoutReturnStateStore", "SignaCoreHostedLoginEndpointService", "SignaCoreHostedLogoutService" })
                Assert.Null(services.GetService(typeof(ITicketStore).Assembly.GetType("SignaCore.Client.AspNetCore." + name)!));
            var schemes = await services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
            Assert.DoesNotContain(schemes, scheme => scheme.Name == SignaCoreHostedLoginDefaults.AuthenticationScheme || scheme.Name == SignaCoreHostedLoginDefaults.SessionAuthenticationScheme);
            Assert.DoesNotContain(services.GetServices<IHostedService>(), service => service.GetType().Assembly.GetName().Name == "SignaCore.Client.AspNetCore");
            foreach (var path in new[] { "/", "/login", "/health", "/health/live", "/health/ready" })
            {
                using var healthy = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
            }
            using var bearer = await harness.Bearer(HttpMethod.Get, "/admin/tags", "student");
            Assert.Equal(HttpStatusCode.OK, bearer.StatusCode);
            foreach (var path in new[] { "", "/", "/start?returnUrl=%2Fadmin%2Fauth%2Foidc%2Fstart", "/callback?code=synthetic-protocol-canary&state=synthetic-protocol-canary", "/session", "/csrf", "/signin-failed", "/logout/return", "/other", "/UNKNOWN/DEEP/" })
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
            {
                using var request = new HttpRequestMessage(method, "/ADMIN/AUTH/OIDC" + path);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                SecurityResponseHeaders.AssertBaseline(response);
                Assert.False(response.Headers.Contains("Set-Cookie"));
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("Hosted sign-in is not configured.", problem.RootElement.GetProperty("title").GetString());
                Assert.Equal("quaestura.oidc.not_configured", problem.RootElement.GetProperty("errorCode").GetString());
                Assert.Equal("QUAESTURA_OIDC_NOT_CONFIGURED", problem.RootElement.GetProperty("quaesturaErrorCode").GetString());
                Assert.Equal(response.Headers.GetValues("x-correlation-id").Single(), problem.RootElement.GetProperty("correlationId").GetString());
            }
            var diagnostic = Assert.Single(output.ToString().Split('\n'), line => line.Contains("Hosted sign-in is not configured. Missing keys:"));
            Assert.Contains(" ERR]", diagnostic); Assert.Contains("AdminOidc:" + key, diagnostic);
            Assert.DoesNotContain(HostedLoginTestAuthority.Secret, output.ToString());
            Assert.DoesNotContain("synthetic-invalid-config-canary", output.ToString());
            Assert.DoesNotContain("synthetic-protocol-canary", output.ToString());
        }
        finally { Console.SetOut(original); Directory.Delete(webRoot, recursive: true); }
    }

    [Theory]
    [InlineData("false")] [InlineData("true")] [InlineData("synthetic-invalid-enabled")]
    public async Task RetiredToggle_IsIgnored_AndValidConfigurationAlwaysEnablesOfficialLogin(string value)
    {
        using var harness = new HostedLoginHarness("Enabled", value);
        var session = await harness.SignedIn();
        Assert.True(harness.Factory.Services.GetRequiredService<AdminOidcConfigurationStatus>().IsConfigured);
        using var business = await harness.Send(HttpMethod.Get, "/admin/tags", session);
        Assert.Equal(HttpStatusCode.OK, business.StatusCode);
    }
}
