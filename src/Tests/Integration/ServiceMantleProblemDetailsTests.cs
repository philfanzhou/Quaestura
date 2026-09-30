using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Quaestura.Domain.Services;
using Quaestura.Tests.Authentication;
using Xunit;

namespace Quaestura.Tests.Integration;

/// <summary>
/// The ServiceMantle Problem Details boundary wired in Program.cs for the /admin JSON API:
/// every registered domain exception maps to its fixed status with a stable lowercase
/// quaestura.* error code and the instance-level uppercase QUAESTURA_* code projected only
/// through the whitelisted quaesturaErrorCode extension field; developer-declared validation
/// messages travel only through quaesturaValidationErrors; unmapped exceptions fall through to
/// the library's fixed safe 500 (http.internal_server_error, no exception content); caller
/// cancellation propagates without a problem body; and once a response has started the boundary
/// keeps the already-sent bytes. No exception message from a non-validation source ever
/// appears in a response body.
/// </summary>
public sealed class ServiceMantleProblemDetailsTests : IClassFixture<QuaesturaApiFactory>, IDisposable
{
    private const string Issuer = "https://identity.test.ruoyu.study";

    // Synthetic exception-content canary: must never appear in any response body.
    private const string CanaryMessage = "synthetic-problem-message-canary";

    private readonly QuaesturaApiFactory _baseFactory;
    private readonly List<IDisposable> _disposables = [];

    public ServiceMantleProblemDetailsTests(QuaesturaApiFactory factory)
    {
        _baseFactory = factory;
    }

    // ---------- exact-type mappings: one stable code + projected instance code ----------

    [Fact]
    public async Task EntityNotFound_MapsTo404_WithProjectedInstanceCode()
    {
        var tags = new Mock<ITagService>();
        tags.Setup(service => service.GetAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Quaestura.Database.Entity.Tag?)null);
        using var client = CreateClientWithTags(tags.Object);

        using var response = await client.SendAsync(Get($"/admin/tags/{Guid.NewGuid()}", staff: false));

        await AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            "quaestura.entity_not_found",
            "urn:servicemantle:error:quaestura.entity_not_found",
            "The requested resource was not found.",
            "QUAESTURA_TAG_NOT_FOUND");
    }

    [Fact]
    public async Task BusinessPrecondition_MapsTo422_WithProjectedInstanceCode()
    {
        var tagId = Guid.NewGuid();
        var tags = new Mock<ITagService>();
        tags.Setup(service => service.GetAsync(tagId))
            .ReturnsAsync(new Quaestura.Database.Entity.Tag
            {
                Id = tagId,
                Name = "referenced-tag",
                CreatedBy = "question-bank-test-user",
            });
        tags.Setup(service => service.DeleteAsync(tagId))
            .ReturnsAsync((success: false, isReferenced: true));
        using var client = CreateClientWithTags(tags.Object);

        using var response = await client.SendAsync(Delete($"/admin/tags/{tagId}", staff: true));

        await AssertProblemAsync(
            response,
            HttpStatusCode.UnprocessableEntity,
            "quaestura.business_precondition_failed",
            "urn:servicemantle:error:quaestura.business_precondition_failed",
            "The business precondition for this operation was not met.",
            "QUAESTURA_TAG_REFERENCED");
    }

    [Fact]
    public async Task Forbidden_MapsTo403_WithProjectedInstanceCode()
    {
        using var client = CreateClient();

        using var response = await client.SendAsync(
            Post("/admin/tags", Json("{\"name\":\"probe\"}"), staff: false));

        await AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            "quaestura.forbidden",
            "urn:servicemantle:error:quaestura.forbidden",
            "You are not allowed to perform this operation.",
            "QUAESTURA_FORBIDDEN");
    }

    [Fact]
    public async Task ForbiddenNotOwner_MapsTo403_WithProjectedInstanceCode()
    {
        var tagId = Guid.NewGuid();
        var tags = new Mock<ITagService>();
        tags.Setup(service => service.GetAsync(tagId))
            .ReturnsAsync(new Quaestura.Database.Entity.Tag
            {
                Id = tagId,
                Name = "foreign-tag",
                CreatedBy = "another-user",
            });
        using var client = CreateClientWithTags(tags.Object);

        using var response = await client.SendAsync(Delete($"/admin/tags/{tagId}", staff: true));

        await AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            "quaestura.forbidden",
            "urn:servicemantle:error:quaestura.forbidden",
            "You are not allowed to perform this operation.",
            "QUAESTURA_FORBIDDEN_NOT_OWNER");
    }

    // ---------- conditional mapping: DomainException grouped by declared status ----------

    [Fact]
    public async Task DomainConflict_MapsTo409_WithProjectedInstanceCode()
    {
        var tags = new Mock<ITagService>();
        tags.Setup(service => service.AddAsync(It.IsAny<Quaestura.Database.Entity.Tag>(), It.IsAny<string>()))
            .ReturnsAsync((success: false, isDuplicate: true));
        using var client = CreateClientWithTags(tags.Object);

        using var response = await client.SendAsync(
            Post("/admin/tags", Json("{\"name\":\"duplicate\"}"), staff: true));

        await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "quaestura.conflict",
            "urn:servicemantle:error:quaestura.conflict",
            "The operation conflicts with the current state.",
            "QUAESTURA_TAG_DUPLICATE");
    }

    [Fact]
    public async Task DomainDeclared500_MapsTo500_WithProjectedInstanceCode()
    {
        var tags = new Mock<ITagService>();
        tags.Setup(service => service.AddAsync(It.IsAny<Quaestura.Database.Entity.Tag>(), It.IsAny<string>()))
            .ReturnsAsync((success: false, isDuplicate: false));
        using var client = CreateClientWithTags(tags.Object);

        using var response = await client.SendAsync(
            Post("/admin/tags", Json("{\"name\":\"failing\"}"), staff: true));

        await AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "quaestura.domain_error",
            "urn:servicemantle:error:quaestura.domain_error",
            "The operation failed.",
            "QUAESTURA_TAG_CREATE_FAILED");
    }

    // ---------- validation: developer-declared messages via the whitelisted field ----------

    [Fact]
    public async Task FluentValidationFailure_MapsTo400_WithJoinedMessages()
    {
        using var client = CreateClient();

        using var response = await client.SendAsync(
            Post("/admin/tags", Json("{\"name\":\"\"}"), staff: true));

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "quaestura.validation_failed",
            "urn:servicemantle:error:quaestura.validation_failed",
            "The request failed validation.",
            "QUAESTURA_VALIDATION_FAILED",
            validationErrors: "Name is required");
    }

    [Fact]
    public async Task EndpointExplicitFailure_KeepsBusinessJsonShape()
    {
        // The login endpoint returns its validation failure explicitly (an endpoint-owned
        // business JSON envelope), so the Problem Details boundary is never involved: the
        // legacy {success, message} shape survives for explicitly returned failures.
        using var client = CreateClient();

        using var response = await client.PostAsync("/admin/auth/login", Json("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("message").GetString()
            .Should().Be("Username and password are required.");
        body.Should().NotContain("urn:servicemantle:error");
    }

    [Fact]
    public async Task InvalidImage_MapsTo400_WithImageValidationCode()
    {
        using var client = CreateClient();

        using var form = new MultipartFormDataContent
        {
            { new StringContent("{\"level\":0,\"type\":0}"), "question" },
            { new StringContent("1"), "subject" },
            { new StringContent("1"), "grade" },
        };
        form.Add(new ByteArrayContent([]), "pictures", "empty.png");
        using var request = Post("/admin/questions", form, staff: true);

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "quaestura.validation_invalid_image",
            "urn:servicemantle:error:quaestura.validation_invalid_image",
            "The submitted image is invalid.",
            "QUAESTURA_VALIDATION_INVALID_IMAGE",
            validationErrors: "Image data is empty");
    }

    // ---------- unmapped failures: the fixed safe 500, never exception content ----------

    [Fact]
    public async Task UnmappedException_MapsToFixedSafe500()
    {
        var tags = new Mock<ITagService>();
        tags.Setup(service => service.ListAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new NotSupportedException(CanaryMessage));
        using var client = CreateClientWithTags(tags.Object);

        using var response = await client.SendAsync(Get("/admin/tags", staff: false));

        var body = await AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "http.internal_server_error",
            "urn:servicemantle:error:http.internal_server_error",
            "An unexpected error occurred.");
        body.Should().NotContain(CanaryMessage);
        body.Should().NotContain("quaesturaErrorCode");
    }

    [Fact]
    public async Task NonImageInvalidOperationException_MapsTo500_WithoutMessage()
    {
        var tags = new Mock<ITagService>();
        tags.Setup(service => service.ListAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException(CanaryMessage));
        using var client = CreateClientWithTags(tags.Object);

        using var response = await client.SendAsync(Get("/admin/tags", staff: false));

        var body = await AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "quaestura.invalid_operation",
            "urn:servicemantle:error:quaestura.invalid_operation",
            "An unexpected error occurred.");
        body.Should().NotContain(CanaryMessage);
    }

    // ---------- cancellation: propagated, never converted into a problem ----------

    [Fact]
    public async Task CallerCancellation_Propagates_WithoutProblemBody()
    {
        var hanging = new HangingTagService();
        using var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpContextAccessor();
                services.Replace(ServiceDescriptor.Scoped<ITagService>(serviceProvider =>
                {
                    hanging.Bind(serviceProvider.GetRequiredService<IHttpContextAccessor>());
                    return hanging;
                }));
            }));
        Track(factory);
        using var client = factory.CreateClient();
        using var cts = new CancellationTokenSource();

        var send = client.SendAsync(Get("/admin/tags", staff: false), cts.Token);
        await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        // The boundary rethrows caller cancellation instead of writing a problem response.
        await FluentActions.Awaiting(() => send).Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------- response already started: the library non-guarantee ----------

    [Fact]
    public async Task ResponseAlreadyStarted_KeepsStatusAndSentBytes()
    {
        var started = new StartedResponseTagService();
        using var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpContextAccessor();
                services.Replace(ServiceDescriptor.Scoped<ITagService>(serviceProvider =>
                {
                    started.Bind(serviceProvider.GetRequiredService<IHttpContextAccessor>());
                    return started;
                }));
            }));
        Track(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Get("/admin/tags", staff: false));

        // The flushed bytes and the default 200 stay; the failure is swallowed with a safe log
        // and no problem body is appended after the started response.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("partial-body");
        body.Should().NotContain("problem");
        body.Should().NotContain(CanaryMessage);
    }

    // ---------- boundary scope: non-admin surfaces never enter the branch ----------

    [Fact]
    public async Task Health_NeverEntersTheProblemBranch()
    {
        using var client = CreateClient();

        using var health = await client.GetAsync("/health");
        health.StatusCode.Should().Be(HttpStatusCode.OK);
        health.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        (await health.Content.ReadAsStringAsync()).Should().NotContain("problem");
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    // ---------- helpers ----------

    private HttpClient CreateClient()
    {
        var client = _baseFactory.CreateClient();
        _disposables.Add(client);
        return client;
    }

    private HttpClient CreateClientWithTags(ITagService tags)
    {
        var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Scoped<ITagService>(_ => tags))));
        Track(factory);
        var client = factory.CreateClient();
        _disposables.Add(client);
        return client;
    }

    private void Track(IDisposable disposable) => _disposables.Add(disposable);

    private static HttpRequestMessage Get(string url, bool staff) =>
        Authorize(new HttpRequestMessage(HttpMethod.Get, url), staff);

    private static HttpRequestMessage Post(string url, HttpContent content, bool staff)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        return Authorize(request, staff);
    }

    private static HttpRequestMessage Delete(string url, bool staff) =>
        Authorize(new HttpRequestMessage(HttpMethod.Delete, url), staff);

    private static HttpRequestMessage Authorize(HttpRequestMessage request, bool staff)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", QuaesturaApiFactory.CreateToken(Issuer, staff ? "teacher" : "student"));
        return request;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// Asserts the complete problem contract for a mapped exception: the HTTP status, the
    /// application/problem+json content type, the five fixed members (type/title/status/
    /// correlationId/errorCode), the optional whitelisted extension projections, and the exact
    /// field set (no additional members such as detail or traceId). Returns the raw body for
    /// caller-specific absence assertions.
    /// </summary>
    private static async Task<string> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string errorCode,
        string typeUri,
        string title,
        string? quaesturaErrorCode = null,
        string? validationErrors = null)
    {
        response.StatusCode.Should().Be(expectedStatus);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var expectedFields = new List<string>
        {
            "type", "title", "status", "correlationId", "errorCode",
        };
        if (quaesturaErrorCode is not null)
        {
            expectedFields.Add("quaesturaErrorCode");
        }

        if (validationErrors is not null)
        {
            expectedFields.Add("quaesturaValidationErrors");
        }

        root.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(expectedFields);

        root.GetProperty("type").GetString().Should().Be(typeUri);
        root.GetProperty("title").GetString().Should().Be(title);
        root.GetProperty("status").GetInt32().Should().Be((int)expectedStatus);
        root.GetProperty("errorCode").GetString().Should().Be(errorCode);
        if (quaesturaErrorCode is not null)
        {
            root.GetProperty("quaesturaErrorCode").GetString().Should().Be(quaesturaErrorCode);
        }

        if (validationErrors is not null)
        {
            root.GetProperty("quaesturaValidationErrors").GetString().Should().Be(validationErrors);
        }

        // The body correlation id matches the response header (same request scope).
        var correlationId = root.GetProperty("correlationId").GetString();
        correlationId.Should().NotBeNullOrWhiteSpace();
        response.Headers.TryGetValues("x-correlation-id", out var headerValues);
        headerValues.Single().Should().Be(correlationId);

        return body;
    }

    /// <summary>
    /// A scoped double whose list behavior enters a wait bound to the request's abortion
    /// token, so the test can cancel the caller while the handler is still running.
    /// </summary>
    private sealed class HangingTagService : ITagService
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private IHttpContextAccessor? _accessor;

        public void Bind(IHttpContextAccessor accessor) => _accessor = accessor;

        public Task<Quaestura.Database.Entity.Tag?> GetAsync(Guid id) =>
            Task.FromResult<Quaestura.Database.Entity.Tag?>(null);

        public async Task<(List<Quaestura.Database.Entity.Tag> items, int totalPages, int totalCount)> ListAsync(
            int page, int size, string? name, string? sortBy)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, _accessor!.HttpContext!.RequestAborted);
            throw new InvalidOperationException("unreachable");
        }

        public Task<(bool success, bool isDuplicate)> AddAsync(
            Quaestura.Database.Entity.Tag tag, string userId) =>
            Task.FromResult((false, false));

        public Task<(bool success, bool isDuplicate)> UpdateAsync(
            Quaestura.Database.Entity.Tag tag, string userId) =>
            Task.FromResult((false, false));

        public Task<(bool success, bool isReferenced)> DeleteAsync(Guid id) =>
            Task.FromResult((false, false));
    }

    /// <summary>
    /// A scoped double that starts the response manually (writes and flushes bytes), then
    /// throws: the Problem Details boundary must keep the sent bytes instead of converting the
    /// failure into a problem response.
    /// </summary>
    private sealed class StartedResponseTagService : ITagService
    {
        private IHttpContextAccessor? _accessor;

        public void Bind(IHttpContextAccessor accessor) => _accessor = accessor;

        public Task<Quaestura.Database.Entity.Tag?> GetAsync(Guid id) =>
            Task.FromResult<Quaestura.Database.Entity.Tag?>(null);

        public async Task<(List<Quaestura.Database.Entity.Tag> items, int totalPages, int totalCount)> ListAsync(
            int page, int size, string? name, string? sortBy)
        {
            var response = _accessor!.HttpContext!.Response;
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes("partial-body"));
            await response.Body.FlushAsync();
            throw new InvalidOperationException(CanaryMessage);
        }

        public Task<(bool success, bool isDuplicate)> AddAsync(
            Quaestura.Database.Entity.Tag tag, string userId) =>
            Task.FromResult((false, false));

        public Task<(bool success, bool isDuplicate)> UpdateAsync(
            Quaestura.Database.Entity.Tag tag, string userId) =>
            Task.FromResult((false, false));

        public Task<(bool success, bool isReferenced)> DeleteAsync(Guid id) =>
            Task.FromResult((false, false));
    }
}
