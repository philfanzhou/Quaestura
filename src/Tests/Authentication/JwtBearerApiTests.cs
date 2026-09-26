using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Quaestura.Common.Authentication;
using Quaestura.Database;
using Xunit;

namespace Quaestura.Tests.Authentication;

public sealed class JwtBearerApiTests : IClassFixture<QuaesturaApiFactory>
{
    private const string Issuer = "https://identity.test.ruoyu.study";
    private const string Audience = "QuantumZhou.microservices";
    private readonly HttpClient _client;

    public JwtBearerApiTests(QuaesturaApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ProtectedApi_AcceptsValidBearerToken()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/tags");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            JwtBearerDefaults.AuthenticationScheme,
            QuaesturaApiFactory.CreateToken(Issuer));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ProtectedApi_RejectsTokenFromUnknownIssuer()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/tags");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            JwtBearerDefaults.AuthenticationScheme,
            QuaesturaApiFactory.CreateToken("https://untrusted-issuer.invalid"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_RemainsAnonymous()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

public sealed class QuaesturaApiFactory : WebApplicationFactory<Program>
{
    private const string Issuer = "https://identity.test.ruoyu.study";
    private const string Audience = "QuantumZhou.microservices";
    private static readonly SymmetricSecurityKey SigningKey = new(
        Encoding.UTF8.GetBytes("question-bank-api-contract-signing-key-32-bytes"));
    private readonly string _databaseName = $"QuaesturaApi_{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IdentityService:Authority"] = Issuer,
                ["IdentityService:Issuer"] = Issuer,
                ["IdentityService:Audience"] = Audience,
                ["IdentityService:RequireHttpsMetadata"] = "true",
                ["IdentityService:ClockSkewSeconds"] = "30"
            });
        });
        builder.ConfigureServices(services =>
        {
            // EF Core 9+ AddDbContext also registers IDbContextOptionsConfiguration<TContext>;
            // remove it too, or the Npgsql and InMemory providers are both configured.
            services.RemoveAll<DbContextOptions<QuaesturaDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<QuaesturaDbContext>>();
            services.AddDbContext<QuaesturaDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    options.Authority = null!;
                    options.MetadataAddress = null!;
                    options.TokenValidationParameters = IdentityTokenValidationParametersFactory.Create(
                        new IdentityAuthenticationOptions
                        {
                            Authority = Issuer,
                            Issuer = Issuer,
                            Audience = Audience,
                            RequireHttpsMetadata = true,
                            ClockSkewSeconds = 30
                        },
                        [SigningKey]);
                });
        });
    }

    public static string CreateToken(string issuer)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer,
            Audience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, "question-bank-test-user"),
                new Claim("role", "student")
            ],
            now.AddSeconds(-1),
            now.AddMinutes(5),
            new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
