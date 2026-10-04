using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Quaestura.Common.Authentication;
using Xunit;

namespace Quaestura.Tests.Authentication;

public sealed class ExplicitIssuerTrustTests
{
    private const string Issuer = "https://explicit.invalid";
    private const string LegacyIssuer = "https://legacy.invalid";
    private const string DiscoveryIssuer = "https://discovery-canary.invalid";
    private const string UnknownIssuer = "https://unknown-canary.invalid";
    private const string Audience = "issuer-boundary-tests";
    private const string Diagnostic = "Token issuer must match IdentityService:Issuer or IdentityService:AdditionalValidIssuers.";

    [Fact]
    public async Task Factory_CapturesNormalizedImmutableTrust_AndKeepsInstancesIndependent()
    {
        var options = new IdentityAuthenticationOptions
        {
            Issuer = " " + Issuer + " ", Audience = Audience,
            AdditionalValidIssuers = ["", " ", null!, Issuer, " " + LegacyIssuer + " ", LegacyIssuer]
        };
        var parameters = IdentityTokenValidationParametersFactory.Create(options);
        Assert.Equal(new[] { Issuer, LegacyIssuer }, parameters.ValidIssuers);
        // Even direct mutation through the exposed list must not mutate captured trust.
        var list = Assert.IsAssignableFrom<IList<string>>(parameters.ValidIssuers);
        Assert.Throws<NotSupportedException>(() => list[0] = DiscoveryIssuer);
        options.Issuer = DiscoveryIssuer;
        options.AdditionalValidIssuers[4] = UnknownIssuer;
        options.AdditionalValidIssuers = [UnknownIssuer];
        var other = IdentityTokenValidationParametersFactory.Create(options);
        var clone = parameters.Clone();
        clone.ValidIssuer = DiscoveryIssuer;
        clone.ValidIssuers = [DiscoveryIssuer, UnknownIssuer];
        Assert.Equal(Issuer, clone.IssuerValidator(Issuer, null!, clone));
        AssertRejected(clone, DiscoveryIssuer);
        AssertRejected(clone, UnknownIssuer);
        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() =>
        {
            Assert.Equal(Issuer, parameters.IssuerValidator(Issuer, null!, parameters));
            AssertRejected(parameters, DiscoveryIssuer);
            Assert.Equal(DiscoveryIssuer, other.IssuerValidator(DiscoveryIssuer, null!, other));
            AssertRejected(other, Issuer);
        })));
        Assert.Throws<ArgumentNullException>(() => IdentityTokenValidationParametersFactory.Create(null!));
        var empty = IdentityTokenValidationParametersFactory.Create(new IdentityAuthenticationOptions { AdditionalValidIssuers = null! });
        Assert.Empty(empty.ValidIssuers);
        AssertRejected(empty, Issuer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(UnknownIssuer)]
    [InlineData("https://EXPLICIT.invalid")]
    [InlineData(Issuer + "/")]
    [InlineData(" " + Issuer)]
    public void Validator_RejectsNonExactInput_WithSafeDiagnostic(string? issuer)
    {
        var parameters = IdentityTokenValidationParametersFactory.Create(new IdentityAuthenticationOptions { Issuer = Issuer });
        AssertRejected(parameters, issuer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealBearerHandler_UsesOnlyExplicitTrust_WithDiscoveryAndKeyRefresh(bool nonBaseManager)
    {
        using var rsa = RSA.Create(2048);
        using var rotatedRsa = RSA.Create(2048);
        using var wrongRsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "initial" };
        var rotated = new RsaSecurityKey(rotatedRsa) { KeyId = "rotated" };
        var wrong = new RsaSecurityKey(wrongRsa) { KeyId = key.KeyId };
        using var discovery = new DiscoveryHandler(key);
        using var backchannel = new HttpClient(discovery);
        var logs = new CaptureLogs();
        var failures = new ConcurrentQueue<Exception>();
        var executed = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IdentityService:Authority"] = DiscoveryIssuer,
            ["IdentityService:Issuer"] = " " + Issuer + " ",
            ["IdentityService:AdditionalValidIssuers:0"] = " " + LegacyIssuer + " ",
            ["IdentityService:AdditionalValidIssuers:1"] = Issuer,
            ["IdentityService:AdditionalValidIssuers:2"] = " ",
            ["IdentityService:Audience"] = Audience,
            ["IdentityService:ClockSkewSeconds"] = "0"
        });
        builder.Services.AddRuoyuJwtBearer(builder.Configuration, builder.Environment,
            consumer => { consumer.MapInboundClaims = false; consumer.NameClaimType = "sub"; consumer.RoleClaimType = "role"; });
        var wrappedManager = nonBaseManager ? new DiscoveryConfigurationManager(backchannel) : null;
        builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Backchannel = backchannel;
            if (wrappedManager is not null) options.ConfigurationManager = wrappedManager;
            options.Events.OnAuthenticationFailed = context =>
            {
                failures.Enqueue(context.Exception);
                return Task.CompletedTask;
            };
        });
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/protected", (ClaimsPrincipal user) =>
        {
            Interlocked.Increment(ref executed);
            return user.Identity!.Name == "fixture-user" && user.IsInRole("admin")
                ? Microsoft.AspNetCore.Http.Results.Ok() : Microsoft.AspNetCore.Http.Results.StatusCode(500);
        }).RequireAuthorization();
        await app.StartAsync();
        var bearer = app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.Equal(DiscoveryIssuer, bearer.Authority);
        Assert.Equal(nonBaseManager, bearer.ConfigurationManager is not BaseConfigurationManager);
        using var client = app.GetTestClient();
        foreach (var issuer in new[] { Issuer, LegacyIssuer })
            Assert.Equal(HttpStatusCode.OK, await Send(issuer, key));
        var accepted = executed;
        foreach (var issuer in new[] { DiscoveryIssuer, UnknownIssuer, Issuer + "/", "https://EXPLICIT.invalid", "" })
            Assert.Equal(HttpStatusCode.Unauthorized, await Send(issuer, key, checkDiagnostic: true));
        Assert.Equal(accepted, executed);
        Assert.All(failures, failure =>
        {
            var exception = Assert.IsType<SecurityTokenInvalidIssuerException>(failure);
            Assert.Equal(Diagnostic, exception.Message);
            Assert.Null(exception.InvalidIssuer);
        });
        Assert.True(discovery.MetadataRequests > 0 && discovery.KeyRequests > 0);
        // Existing independent checks still fail when issuer trust succeeds.
        Assert.Equal(HttpStatusCode.Unauthorized, await Send(Issuer, wrong));
        Assert.Equal(HttpStatusCode.Unauthorized, await Send(Issuer, key, audience: "wrong-audience"));
        Assert.Equal(HttpStatusCode.Unauthorized, await Send(Issuer, key, expired: true));
        Assert.Equal(accepted, executed);

        discovery.Key = rotated;
        if (bearer.ConfigurationManager is ConfigurationManager<OpenIdConnectConfiguration> manager)
        {
            manager.RefreshInterval = TimeSpan.FromSeconds(1);
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
        }
        bearer.ConfigurationManager!.RequestRefresh();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        OpenIdConnectConfiguration refreshed;
        do
        {
            refreshed = await bearer.ConfigurationManager.GetConfigurationAsync(timeout.Token);
            if (refreshed.SigningKeys.Any(k => k.KeyId == rotated.KeyId)) break;
            await Task.Delay(20, timeout.Token);
        } while (true);
        Assert.Equal(HttpStatusCode.OK, await Send(Issuer, rotated));
        Assert.Equal(HttpStatusCode.OK, await Send(LegacyIssuer, rotated));
        Assert.Equal(HttpStatusCode.Unauthorized, await Send(DiscoveryIssuer, rotated, checkDiagnostic: true));
        Assert.Equal(accepted + 2, executed);
        Assert.True(discovery.KeyRequests >= 2);

        async Task<HttpStatusCode> Send(string issuer, SecurityKey signingKey, string audience = Audience,
            bool expired = false, bool checkDiagnostic = false)
        {
            var now = DateTime.UtcNow;
            var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience,
                [new Claim("sub", "fixture-user"), new Claim("role", "admin")],
                now.AddMinutes(-3), expired ? now.AddMinutes(-1) : now.AddMinutes(5),
                new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)));
            using var request = new HttpRequestMessage(HttpMethod.Get, "/protected");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            if (checkDiagnostic)
            {
                var output = await response.Content.ReadAsStringAsync() + response.Headers + logs.Text;
                Assert.True(!output.Contains(token, StringComparison.Ordinal), "Issuer rejection must not disclose the token.");
                Assert.True(!output.Contains(DiscoveryIssuer, StringComparison.Ordinal)
                    && !output.Contains(UnknownIssuer, StringComparison.Ordinal), "Issuer rejection must not disclose issuer canaries.");
            }
            return response.StatusCode;
        }
    }

    private static void AssertRejected(TokenValidationParameters parameters, string? issuer)
    {
        var exception = Assert.Throws<SecurityTokenInvalidIssuerException>(() => parameters.IssuerValidator(issuer!, null!, parameters));
        Assert.Equal(Diagnostic, exception.Message);
        Assert.Null(exception.InvalidIssuer);
    }

    private sealed class DiscoveryHandler(SecurityKey key) : HttpMessageHandler
    {
        public SecurityKey Key { get; set; } = key;
        public int MetadataRequests;
        public int KeyRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string content;
            if (request.RequestUri!.AbsolutePath == "/jwks")
            {
                Interlocked.Increment(ref KeyRequests);
                var publicKey = JsonWebKeyConverter.ConvertFromRSASecurityKey((RsaSecurityKey)Key);
                publicKey.D = publicKey.DP = publicKey.DQ = publicKey.P = publicKey.Q = publicKey.QI = null;
                content = JsonSerializer.Serialize(new { keys = new[] { publicKey } });
            }
            else
            {
                Interlocked.Increment(ref MetadataRequests);
                content = JsonSerializer.Serialize(new { issuer = DiscoveryIssuer, jwks_uri = DiscoveryIssuer + "/jwks" });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }

    // Deliberately non-base, but still retrieves real discovery and JWKS over the backchannel.
    private sealed class DiscoveryConfigurationManager(HttpClient backchannel) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private OpenIdConnectConfiguration? _configuration;
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _configuration is not null ? Task.FromResult(_configuration) : Retrieve(cancellationToken);
        }
        private async Task<OpenIdConnectConfiguration> Retrieve(CancellationToken cancellationToken)
        {
            _configuration = await OpenIdConnectConfigurationRetriever.GetAsync(
                DiscoveryIssuer + "/.well-known/openid-configuration", backchannel, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return _configuration;
        }
        public void RequestRefresh() => _configuration = null;
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();
        public string Text => string.Join("\n", _messages);
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(_messages);
        public void Dispose() { }
        private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception) + exception);
        }
    }
}
