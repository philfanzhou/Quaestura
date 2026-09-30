using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Quaestura.Tests.Authentication;

public sealed class SpaHostingTests : IClassFixture<QuaesturaApiFactory>, IDisposable
{
    private const string IndexHtml =
        "<!doctype html><html><head><title>__APP_TITLE__</title></head><body><div id=\"app\"></div></body></html>";

    private readonly string _webRoot;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SpaHostingTests(QuaesturaApiFactory factory)
    {
        _webRoot = Path.Combine(Path.GetTempPath(), $"quaestura-spa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), IndexHtml);
        File.WriteAllText(Path.Combine(_webRoot, "app.js"), "console.log('app');");

        _factory = factory.WithWebHostBuilder(builder => builder.UseWebRoot(_webRoot));
        _client = _factory.CreateClient();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/login")]
    [InlineData("/questions/123")]
    [InlineData("/app.js")]
    public async Task SpaPaths_AreServedWithoutToken(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/login")]
    [InlineData("/questions/123")]
    public async Task IndexHtmlResponses_InjectAppTitle(string path)
    {
        var response = await _client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain("<title>__APP_TITLE__</title>");
        body.Should().Contain("window.__APP_TITLE__");
    }

    [Fact]
    public async Task StaticFiles_AreServedUnmodified()
    {
        var response = await _client.GetAsync("/app.js");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Be("console.log('app');");
    }

    [Theory]
    [InlineData("/admin/tags")]
    [InlineData("/admin/unknown-route")]
    public async Task AdminPaths_StillRequireToken(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Directory.Delete(_webRoot, recursive: true);
    }
}
