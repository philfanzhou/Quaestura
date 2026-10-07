using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Quaestura.Host;
using Quaestura.Tests.Authentication;
using ServiceMantle;
using ServiceMantle.Web.Logging;
using Xunit;

namespace Quaestura.Tests.Observability;

/// <summary>
/// Boots the real Program.cs entry point (WebApplicationFactory) and proves the ServiceMantle
/// host identity wiring: ServiceId/InstanceId/ServiceLogContext resolve consistently, the
/// instance identity is per-host-build, the service version follows the entry-assembly fallback,
/// and the composition never writes a bootstrap file.
/// </summary>
public sealed partial class ServiceMantleIdentityTests : IClassFixture<QuaesturaApiFactory>
{
    private readonly QuaesturaApiFactory _factory;

    public ServiceMantleIdentityTests(QuaesturaApiFactory factory)
    {
        _factory = factory;
    }

    [GeneratedRegex("^quaestura-[0-9a-f]{32}$")]
    private static partial Regex InstanceIdPattern();

    [Fact]
    public void Host_Starts_AndResolvesServiceMantleIdentity()
    {
        // Accessing factory.Services runs the full host startup. The library's startup validators
        // (OpenTelemetry registration, sensitive-header registry) fail the startup when the
        // composition is invalid, so reaching the assertions proves both the host start and the
        // registration validity.
        var serviceId = _factory.Services.GetRequiredService<ServiceId>();
        var instanceId = _factory.Services.GetRequiredService<InstanceId>();
        var logContext = _factory.Services.GetRequiredService<ServiceLogContext>();

        serviceId.Value.Should().Be(ServiceMantleComposition.ServiceIdValue).And.Be("quaestura");
        instanceId.Value.Should().MatchRegex(InstanceIdPattern());
        _factory.Services.GetRequiredService<InstanceId>().Should().Be(instanceId);

        logContext.ServiceName.Should().Be(serviceId.Value);
        logContext.InstanceId.Should().Be(instanceId.Value);
        logContext.ServiceVersion.Should().Be(ObservabilityTestHelpers.ExpectedEntryAssemblyServiceVersion());
    }

    [Fact]
    public void TwoHostBuilds_ProduceDifferentInstanceIds_ButTheSameServiceId()
    {
        using var first = _factory.WithWebHostBuilder(_ => { });
        using var second = _factory.WithWebHostBuilder(_ => { });

        var firstId = first.Services.GetRequiredService<InstanceId>();
        var secondId = second.Services.GetRequiredService<InstanceId>();

        firstId.Value.Should().NotBe(secondId.Value);
        first.Services.GetRequiredService<ServiceId>().Value
            .Should().Be(second.Services.GetRequiredService<ServiceId>().Value)
            .And.Be("quaestura");
    }

    [Fact]
    public async Task Host_WithTemporaryContentRoot_WritesNoBootstrapFile()
    {
        var contentRoot = Path.Combine(
            Path.GetTempPath(),
            $"quaestura-bootstrap-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);
        try
        {
            // The composition passes no bootstrapFilePath, so the library default would be
            // {AppContext.BaseDirectory}/config/quaestura.bootstrap.json; the store is a lazy
            // singleton nothing resolves, so neither location may ever gain a file. The temporary
            // content root has no appsettings, so the required runtime settings are injected
            // through UseSetting, which the minimal-hosting replay applies before Program reads
            // them (ConfigureAppConfiguration sources are only applied at build time). The values
            // mirror the committed mock testing settings and QuaesturaApiFactory's issuer.
            using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting(WebHostDefaults.ContentRootKey, contentRoot);
                builder.UseSetting("IdentityService:Authority", "https://identity.test.ruoyu.study");
                builder.UseSetting("IdentityService:Issuer", "https://identity.test.ruoyu.study");
                builder.UseSetting("IdentityService:Audience", "QuantumZhou.microservices");
                builder.UseSetting("IdentityService:RequireHttpsMetadata", "true");
                builder.UseSetting("IdentityService:ClockSkewSeconds", "30");
                builder.UseSetting("Oss:InternalEndpoint", "127.0.0.1:1");
                builder.UseSetting("Oss:InternalSecure", "false");
                builder.UseSetting("Oss:AccessKey", "mock_access_key");
                builder.UseSetting("Oss:SecretKey", "mock_secret_key");
                builder.UseSetting("Oss:BucketName", "quaestura-test");
                builder.UseSetting("Oss:PublicBaseUrl", "https://oss.example.com/oss");
                builder.UseSetting("Consul:EnableCache", "false");
            });
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/health");
            response.EnsureSuccessStatusCode();

            Directory
                .GetFiles(contentRoot, "*.bootstrap.json", SearchOption.AllDirectories)
                .Should().BeEmpty();
            File.Exists(Path.Combine(AppContext.BaseDirectory, "config", "quaestura.bootstrap.json"))
                .Should().BeFalse();
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }
}
