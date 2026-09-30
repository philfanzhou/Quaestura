using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Quaestura.Tests.Authentication;

// The shared ServiceMantle logging pipeline writes to the real Console sink synchronously, and
// Console.SetOut redirection is process-wide: every test class that captures console output must
// run alone. (The pipeline no longer replaces the process-wide Serilog Log.Logger, but console
// exclusivity is still required.) ServiceMantleLoggingTests shares this collection for the same
// reason.
[CollectionDefinition(ConsoleLoggingCollection.Name, DisableParallelization = true)]
public sealed class ConsoleLoggingCollection
{
    public const string Name = "console-logging";
}

[Collection(ConsoleLoggingCollection.Name)]
public sealed class AdminAuthEndpointsTests : IClassFixture<QuaesturaApiFactory>, IDisposable
{
    // Same authority QuaesturaApiFactory configures for JWT validation.
    private const string Authority = "https://identity.test.ruoyu.study";
    private const string AppId = "quaestura-test-app";
    private const string AppSecret = "app-secret-4f9c1d7e";
    private const string Password = "password-8b2e6a3c";
    private const string AccessToken = "access-token-5d7f2b9a";
    private const string RefreshToken = "refresh-token-3c8e1f6d";
    private const string AdminUserId = "Admin-User-1";

    private readonly QuaesturaApiFactory _baseFactory;
    private readonly FakeSignaCore _signaCore = new();
    private readonly List<IDisposable> _disposables = new();

    // Log assertions observe the real shared pipeline through its Console sink: the test
    // instance redirects Console.Out for its whole lifetime and asserts on the rendered,
    // filtered, and sanitized lines. The collection above guarantees no other test writes to
    // the console while this window is open.
    private readonly TextWriter _originalConsoleOut;
    private readonly StringWriter _consoleOutput = new();

    public AdminAuthEndpointsTests(QuaesturaApiFactory factory)
    {
        _baseFactory = factory;
        _originalConsoleOut = Console.Out;
        Console.SetOut(_consoleOutput);
    }

    /// <summary>Console lines emitted by the AdminAuthEndpoints category.</summary>
    private IReadOnlyList<string> AuthLines =>
        _consoleOutput.ToString()
            .Split('\n')
            .Where(line => line.Contains("AdminAuthEndpoints"))
            .ToList();

    /// <summary>Warning lines from the AdminAuthEndpoints category. The shared pipeline's
    /// output template renders the level as <c>[TIMESTAMP WRN]</c>.</summary>
    private IReadOnlyList<string> AuthWarnings =>
        AuthLines.Where(line => line.Contains(" WRN]")).ToList();

    /// <summary>Error lines from the AdminAuthEndpoints category.</summary>
    private IReadOnlyList<string> AuthErrors =>
        AuthLines.Where(line => line.Contains(" ERR]")).ToList();

    // ---------- login ----------

    [Theory]
    [InlineData("{\"username\":\"\",\"password\":\"x\"}")]
    [InlineData("{\"username\":\"admin\",\"password\":\"   \"}")]
    [InlineData("{\"password\":\"x\"}")]
    [InlineData("{}")]
    public async Task Login_MissingCredentials_Returns400WithoutCallingSignaCore(string json)
    {
        var client = CreateClient();

        var response = await client.PostAsync("/admin/auth/login", Json(json));

        await AssertFailure(response, HttpStatusCode.BadRequest, "Username and password are required.");
        _signaCore.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("IdentityService:AppId")]
    [InlineData("IdentityService:AppSecret")]
    public async Task Login_ClientCredentialsNotConfigured_Returns503WithoutCallingSignaCore(string missingKey)
    {
        var client = CreateClient(new Dictionary<string, string?> { [missingKey] = "" });

        var response = await client.PostAsync("/admin/auth/login", LoginBody("admin", Password));

        await AssertFailure(response, HttpStatusCode.ServiceUnavailable, "Admin login is not configured.");
        _signaCore.Requests.Should().BeEmpty();
        AuthWarnings.Should().Contain(line => line.Contains("not configured"));
    }

    [Fact]
    public async Task Login_Success_ForwardsPasswordGrantAndReturnsOnlyAccessToken()
    {
        _signaCore.Respond(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            success = true,
            message = "Login successful",
            accessToken = AccessToken,
            refreshToken = RefreshToken,
            expiresIn = 3600,
            expiresAt = 1790000000L,
            userInfo = new { userId = AdminUserId, username = "admin", roles = new[] { "admin" } },
        }));
        var client = CreateClient();

        // Username is forwarded verbatim (no trimming or case folding).
        var response = await client.PostAsync("/admin/auth/login", LoginBody(" Admin ", Password));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SecurityResponseHeaders.AssertBaseline(response);
        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);
        body.RootElement.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo("success", "message", "accessToken", "expiresIn", "expiresAt");
        body.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("message").GetString().Should().Be("Login successful");
        body.RootElement.GetProperty("accessToken").GetString().Should().Be(AccessToken);
        body.RootElement.GetProperty("expiresIn").GetInt64().Should().Be(3600);
        body.RootElement.GetProperty("expiresAt").GetInt64().Should().Be(1790000000L);
        raw.Should().NotContain(RefreshToken).And.NotContain(AppSecret).And.NotContain("userInfo");

        var sent = _signaCore.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Uri.Should().Be(new Uri($"{Authority}/api/auth/token"));
        sent.Headers["X-Admin-AppId"].Should().Be(AppId);
        sent.Headers["X-Admin-AppSecret"].Should().Be(AppSecret);
        using var sentBody = JsonDocument.Parse(sent.Body);
        sentBody.RootElement.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo("grantType", "username", "password");
        sentBody.RootElement.GetProperty("grantType").GetString().Should().Be("password");
        sentBody.RootElement.GetProperty("username").GetString().Should().Be(" Admin ");
        sentBody.RootElement.GetProperty("password").GetString().Should().Be(Password);
    }

    [Theory]
    [InlineData("Invalid username or password", "Invalid username or password")]
    [InlineData("", "Login failed.")]
    public async Task Login_SignaCoreRejects_Returns400WithSignaCoreMessage(string signaCoreMessage, string expected)
    {
        _signaCore.Respond(HttpStatusCode.OK, JsonSerializer.Serialize(new { success = false, message = signaCoreMessage }));
        var client = CreateClient();

        var response = await client.PostAsync("/admin/auth/login", LoginBody("unique-login-name", Password));

        await AssertFailure(response, HttpStatusCode.BadRequest, expected);
        var warning = AuthWarnings.Should().ContainSingle().Subject;
        warning.Should().Contain(expected).And.NotContain("unique-login-name");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{\"success\":true,\"accessToken\":\"x\"}")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.OK, "<html>not json</html>")]
    [InlineData(HttpStatusCode.OK, "null")]
    [InlineData(HttpStatusCode.OK, "{\"success\":true,\"accessToken\":\"\"}")]
    public async Task Login_SignaCoreResponseUnusable_Returns502(HttpStatusCode status, string body)
    {
        _signaCore.Respond(status, body);
        var client = CreateClient();

        var response = await client.PostAsync("/admin/auth/login", LoginBody("admin", Password));

        await AssertFailure(response, HttpStatusCode.BadGateway, "Identity service unavailable.");
        AuthWarnings.Should().ContainSingle().Which.Should().Contain(((int)status).ToString());
    }

    [Fact]
    public async Task Login_SignaCoreUnreachable_Returns502AndLogsError()
    {
        _signaCore.Throw(() => new HttpRequestException("Connection refused"));
        var client = CreateClient();

        var response = await client.PostAsync("/admin/auth/login", LoginBody("admin", Password));

        await AssertFailure(response, HttpStatusCode.BadGateway, "Identity service unavailable.");
        AuthErrors.Should().ContainSingle().Which.Should().Contain(nameof(HttpRequestException));
    }

    [Fact]
    public async Task Login_SignaCoreTimeout_Returns502AndLogsError()
    {
        _signaCore.Throw(() => new TaskCanceledException("timed out", new TimeoutException()));
        var client = CreateClient();

        var response = await client.PostAsync("/admin/auth/login", LoginBody("admin", Password));

        await AssertFailure(response, HttpStatusCode.BadGateway, "Identity service unavailable.");
        AuthErrors.Should().ContainSingle().Which.Should().Contain(nameof(TaskCanceledException));
    }

    [Fact]
    public async Task Login_ClientDisconnects_CancelsSignaCoreCallWithoutLoggingError()
    {
        _signaCore.Hang();
        var client = CreateClient();
        using var cts = new CancellationTokenSource();

        var call = client.PostAsync("/admin/auth/login", LoginBody("admin", Password), cts.Token);
        await _signaCore.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        await _signaCore.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        _consoleOutput.ToString()
            .Split('\n')
            .Where(line => line.Contains(" ERR]"))
            .Should()
            .NotContain(line => line.Contains("AdminAuthEndpoints")
                || line.Contains("ExceptionHandlingMiddleware"));
    }

    [Fact]
    public async Task Login_NeverLogsSecrets()
    {
        _signaCore.Respond(HttpStatusCode.OK, JsonSerializer.Serialize(new { success = true, accessToken = AccessToken }));
        var client = CreateClient();
        await client.PostAsync("/admin/auth/login", LoginBody("admin", Password));
        _signaCore.Respond(HttpStatusCode.OK, JsonSerializer.Serialize(new { success = false, message = "Bad password" }));
        await client.PostAsync("/admin/auth/login", LoginBody("admin", Password));
        _signaCore.Throw(() => new HttpRequestException("Connection refused"));
        await client.PostAsync("/admin/auth/login", LoginBody("admin", Password));

        AuthLines.Should().NotBeEmpty();
        // The captured console text is the real pipeline output after category filtering and
        // mandatory sanitization; nothing secret-bearing may survive into it.
        _consoleOutput.ToString().Should()
            .NotContain(Password).And.NotContain(AppSecret).And.NotContain(AccessToken);
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

    // ---------- anonymous access ----------

    [Fact]
    public async Task AuthEndpoints_AreAnonymous_WhileOtherAdminEndpointsStillRequireToken()
    {
        var client = CreateClient();

        using var login = await client.PostAsync("/admin/auth/login", Json("{}"));
        login.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        SecurityResponseHeaders.AssertBaseline(login);
        using var callback = await client.PostAsync("/admin/auth/callback", Json("{}"));
        callback.StatusCode.Should().Be(HttpStatusCode.OK);
        SecurityResponseHeaders.AssertBaseline(callback);
        using var tags = await client.GetAsync("/admin/tags");
        tags.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // The API-only policy applies to the two marked auth endpoints, not to the rest of
        // /admin: the unmarked 401 never gains the destructive CSP.
        SecurityResponseHeaders.AssertNotApplied(tags);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        Console.SetOut(_originalConsoleOut);
        _consoleOutput.Dispose();
    }

    private HttpClient CreateClient(IDictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["IdentityService:AppId"] = AppId,
            ["IdentityService:AppSecret"] = AppSecret,
            ["AdminPortal:AdminUserIds:0"] = AdminUserId,
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("IdentityService")
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeSignaCoreHandler(_signaCore));
            });
        });
        var client = factory.CreateClient();
        _disposables.Add(client);
        _disposables.Add(factory);
        return client;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static StringContent LoginBody(string username, string password) =>
        Json(JsonSerializer.Serialize(new { username, password }));

    private static async Task AssertFailure(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        response.StatusCode.Should().Be(status);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("message").GetString().Should().Be(message);
        // Success and failure responses of the marked auth endpoints share the same fixed
        // single-value security header baseline.
        SecurityResponseHeaders.AssertBaseline(response);
    }

    private static async Task AssertRoles(HttpResponseMessage response, params string[] roles)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString())
            .Should().Equal(roles);
        SecurityResponseHeaders.AssertBaseline(response);
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? Uri, Dictionary<string, string> Headers, string Body);

    private sealed class FakeSignaCore
    {
        private Func<CancellationToken, Task<HttpResponseMessage>> _behavior =
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Respond(HttpStatusCode status, string body) =>
            _behavior = _ => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        public void Throw(Func<Exception> exception) => _behavior = _ => throw exception();

        public void Hang() => _behavior = async cancellationToken =>
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
            throw new InvalidOperationException("unreachable");
        };

        public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri, headers, body));
            return await _behavior(cancellationToken);
        }
    }

    private sealed class FakeSignaCoreHandler(FakeSignaCore signaCore) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            signaCore.HandleAsync(request, cancellationToken);
    }
}
