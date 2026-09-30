using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Quaestura.Tests.Authentication;
using ServiceMantle.Web.Logging;
using Xunit;

namespace Quaestura.Tests.Observability;

/// <summary>
/// Proves the base OpenTelemetry instrumentation registered by the ServiceMantle composition:
/// both providers resolve from the real host, their resources carry exactly the three standard
/// identity fields of the host's ServiceLogContext, the framework activity sources are
/// subscribed, and no exporter is registered (no telemetry leaves the process).
/// </summary>
public sealed class ServiceMantleInstrumentationTests : IClassFixture<QuaesturaApiFactory>
{
    private readonly QuaesturaApiFactory _factory;

    public ServiceMantleInstrumentationTests(QuaesturaApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Providers_ResolveWithIdentityOnlyResource_AndZeroExporters()
    {
        // Resolving the providers returns the instances the running host built; the library's
        // OpenTelemetryRegistrationValidator hosted service already failed fast on conflicting
        // registrations, so reaching this point proves the registration is valid.
        var tracerProvider = _factory.Services.GetRequiredService<TracerProvider>();
        var meterProvider = _factory.Services.GetRequiredService<MeterProvider>();
        var logContext = _factory.Services.GetRequiredService<ServiceLogContext>();

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        var expected = new Dictionary<string, object>
        {
            ["service.name"] = logContext.ServiceName,
            ["service.version"] = logContext.ServiceVersion,
            ["service.instance.id"] = logContext.InstanceId,
        };

        tracerProvider.GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value)
            .Should().BeEquivalentTo(expected);
        meterProvider.GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value)
            .Should().BeEquivalentTo(expected);

        // The default instrumentation selection subscribes to the ASP.NET Core (incoming) and
        // HttpClient (outgoing) activity sources; the MeterProvider itself only exists because
        // runtime metrics are enabled in the same registration.
        using var incoming = new ActivitySource("Microsoft.AspNetCore");
        using var outgoing = new ActivitySource("System.Net.Http");
        incoming.HasListeners().Should().BeTrue();
        outgoing.HasListeners().Should().BeTrue();

        // Zero exporters: no exporter package assembly is even loaded into the process, so no
        // telemetry has an external destination. The composition performs no installation-phase
        // bookkeeping either; this asserts exactly what is registered, nothing pretended.
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name!)
            .Where(name => name.StartsWith("OpenTelemetry.Exporter", StringComparison.OrdinalIgnoreCase))
            .Should()
            .BeEmpty();
    }
}
