using System;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;

namespace Quaestura.Host;

/// <summary>
/// Registers the ServiceMantle host identity, the safe request-header projector, and the base
/// OpenTelemetry instrumentation for Quaestura. This composition performs zero disk writes and
/// registers no telemetry exporter: the Bootstrap file store stays a lazy singleton that is
/// never resolved, and instrumentation data stays in-process until an exporter task adds one.
/// </summary>
public static class ServiceMantleComposition
{
    /// <summary>
    /// The stable deployment identity of this service. It is lowercase and used verbatim as the
    /// ServiceMantle <c>service.name</c> and log-scope <c>ServiceName</c>. It is deliberately
    /// distinct from the fixed Serilog/Loki stream label "Quaestura".
    /// </summary>
    public const string ServiceIdValue = "quaestura";

    /// <summary>
    /// Registers ServiceMantle with a per-process instance identity. The instance id is
    /// regenerated on every call (every host build) and is not a persistent identity.
    /// The service version is not passed explicitly: it resolves from the entry assembly
    /// informational version, matching the shared library fallback.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddQuaesturaServiceMantle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddServiceMantle(
                ServiceId.Parse(ServiceIdValue),
                InstanceId.Parse($"{ServiceIdValue}-{Guid.NewGuid():N}"))
            // Default options: AspNetCore + HttpClient tracing and runtime metrics, no exporter.
            .AddOpenTelemetryInstrumentation()
            // Deny the SignaCore admin-login secret explicitly so the safe request-header
            // projector (RequestHeaderDiagnosticProjector) always redacts it. This registers no
            // automatic header logging; projecting headers stays an explicit caller decision.
            .AddSensitiveHeaders(options =>
                options.DeniedHeaderNames = ["X-Admin-AppSecret"]);

        return services;
    }
}
