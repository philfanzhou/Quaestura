using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Quaestura.Tests.Authentication;

// Console redirection is process-wide, so the real Console/Loki assertions run alone.
[CollectionDefinition(ConsoleLoggingCollection.Name, DisableParallelization = true)]
public sealed class ConsoleLoggingCollection
{
    public const string Name = "console-logging";
}

[Collection(ConsoleLoggingCollection.Name)]
public sealed class AdminAuthEndpointsTests : IClassFixture<QuaesturaApiFactory>, IDisposable
{
    private const string AdminUserId = "Admin-User-1";
    private readonly QuaesturaApiFactory _baseFactory;
    private readonly List<IDisposable> _disposables = [];
    private readonly TextWriter _originalConsoleOut;
    private readonly StringWriter _consoleOutput = new();
    private int _upstreamCalls;

    public AdminAuthEndpointsTests(QuaesturaApiFactory factory)
    {
        _baseFactory = factory;
        _originalConsoleOut = Console.Out;
        Console.SetOut(_consoleOutput);
    }

    private IReadOnlyList<string> AuthLines => _consoleOutput.ToString().Split('\n')
        .Where(line => line.Contains("AdminAuthEndpoints")).ToList();

    public static IEnumerable<object[]> RetiredRequests()
    {
        foreach (var path in new[] { "/admin/auth/login", "/ADMIN/AUTH/LOGIN/" })
        foreach (var contentType in new[] { "application/json", "application/x-www-form-urlencoded", "text/plain", "" })
        foreach (var credential in new[] { "anonymous", "invalid-bearer", "valid-bearer", "old-jwt-cookie" })
            yield return new object[] { path, contentType, credential };
    }

    [Theory]
    [MemberData(nameof(RetiredRequests))]
    public async Task RetiredPasswordPost_IsAnonymousConstant410_WithoutBodyReadOrUpstream(
        string path, string contentType, string credential)
    {
        using var client = CreateClient();
        var factory = (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>)_disposables[^1];
        using var body = new UnreadPasswordBody(contentType.Length == 0 ? "" : "password=synthetic-password-canary&secret=synthetic-secret-canary&token=synthetic-token-canary{malformed");
        var context = await factory.Server.SendAsync(context =>
        {
            context.Request.Scheme = "https";
            context.Request.Method = "POST"; context.Request.Path = path;
            context.Request.Headers["x-correlation-id"] = "retired-password-probe";
            context.Request.Body = body; context.Request.ContentLength = body.Length;
            context.Request.ContentType = contentType;
            if (credential == "old-jwt-cookie") context.Request.Headers.Cookie = "quaesturaAuthToken=" + QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study");
            if (credential.EndsWith("bearer")) context.Request.Headers.Authorization = "Bearer " + (credential == "valid-bearer" ? QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study") : "invalid");
        });
        Assert.Equal(410, context.Response.StatusCode);
        Assert.Equal(0, body.ReadCount); Assert.Equal(0, _upstreamCalls);
        using var response = await client.PostAsync(path, new StringContent("{malformed", Encoding.UTF8, contentType.Length == 0 ? "text/plain" : contentType));
        await AssertRetired(response);
        foreach (var canary in new[] { "synthetic-password-canary", "synthetic-secret-canary", "synthetic-token-canary" })
            Assert.False(_consoleOutput.ToString().Contains(canary), "A synthetic retired-body canary reached Console.");
    }

    // ---------- callback ----------

    [Fact]
    public async Task Callback_WhitelistedUser_GetsAdminRoleFromSignaCoreRequestShape()
    {
        var client = CreateClient();

        // Exactly how SignaCore's CallbackService sends it.
        var response = await client.PostAsJsonAsync("/admin/auth/callback", new { user_id = AdminUserId });

        await AssertRoles(response, "admin");
        // The role grant stays observable, but the callback never logs the user identifier.
        AuthLines.Should().Contain(line =>
            line.Contains(" INF]") && line.Contains("granted the admin role"));
        _consoleOutput.ToString().Should().NotContain(AdminUserId);
    }

    [Fact]
    public async Task Callback_WhitelistIsCaseInsensitive()
    {
        var client = CreateClient();

        var response = await client.PostAsync("/admin/auth/callback", Json("{\"user_id\":\"ADMIN-USER-1\"}"));

        await AssertRoles(response, "admin");
    }

    [Theory]
    [InlineData("{\"user_id\":\"someone-else\"}")]
    [InlineData("{\"user_id\":\"\"}")]
    [InlineData("{\"userId\":\"Admin-User-1\"}")]
    [InlineData("{}")]
    [InlineData("")]
    public async Task Callback_OtherRequests_GetNoRoles(string json)
    {
        var client = CreateClient();

        var response = await client.PostAsync("/admin/auth/callback", Json(json));

        await AssertRoles(response);
    }

    [Fact]
    public async Task AuthEndpoints_AreAnonymous_WhileOtherAdminEndpointsStillRequireToken()
    {
        using var client = CreateClient();
        using var login = await client.PostAsync("/admin/auth/login", Json("{}"));
        await AssertRetired(login);
        using var callback = await client.PostAsync("/admin/auth/callback", Json("{}"));
        await AssertRoles(callback);
        using var tags = await client.GetAsync("/admin/tags");
        tags.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        SecurityResponseHeaders.AssertNotApplied(tags);
    }

    [Fact]
    public async Task OldJwtCookieAlone_DoesNotAuthenticateBusiness()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "quaesturaAuthToken=" + QuaesturaApiFactory.CreateToken("https://identity.test.ruoyu.study"));
        using var response = await client.GetAsync("/admin/tags");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        Console.SetOut(_originalConsoleOut); _consoleOutput.Dispose();
    }

    private HttpClient CreateClient()
    {
        var factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AdminPortal:AdminUserIds:0", AdminUserId);
            builder.UseSetting("IdentityService:AppId", "synthetic-unused-app");
            builder.UseSetting("IdentityService:AppSecret", "synthetic-unused-secret");
            builder.ConfigureServices(services => services.AddHttpClient("IdentityService")
                .ConfigurePrimaryHttpMessageHandler(() => new UnexpectedUpstream(() => _upstreamCalls++)));
        });
        var client = factory.CreateClient();
        _disposables.Add(client); _disposables.Add(factory);
        return client;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
    internal static async Task AssertRetired(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        SecurityResponseHeaders.AssertBaseline(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.11", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("Password sign-in is no longer available.", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("quaestura.auth.password_login_retired", body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("QUAESTURA_PASSWORD_LOGIN_RETIRED", body.RootElement.GetProperty("quaesturaErrorCode").GetString());
        Assert.Equal(response.Headers.GetValues("x-correlation-id").Single(), body.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(new[] { "correlationId", "errorCode", "quaesturaErrorCode", "status", "title", "type" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }
    private static async Task AssertRoles(HttpResponseMessage response, params string[] roles)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal(roles);
        SecurityResponseHeaders.AssertBaseline(response);
    }
    private sealed class UnexpectedUpstream(Action called) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { called(); throw new InvalidOperationException("Retired password path attempted an upstream call."); }
    }
}

internal sealed class UnreadPasswordBody(string body) : MemoryStream(Encoding.UTF8.GetBytes(body))
{
    internal int ReadCount;
    private int Fail() { ReadCount++; throw new InvalidOperationException("Retired password body must not be read."); }
    public override int ReadByte() => Fail();
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadFailure());
    public override int Read(byte[] buffer, int offset, int count) => Fail();
    public override int Read(Span<byte> buffer) => Fail();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new InvalidOperationException(ReadFailure());
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException(ReadFailure());
    private string ReadFailure() { ReadCount++; return "Retired password body must not be read."; }
}
