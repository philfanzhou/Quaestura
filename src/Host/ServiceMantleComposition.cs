using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle;

namespace Quaestura.Host;

/// <summary>
/// Registers the ServiceMantle host identity, the safe request-header projector, the mandatory
/// security response-header capability, and the base OpenTelemetry instrumentation for
/// Quaestura. This composition performs zero disk writes and registers no telemetry exporter:
/// the Bootstrap file store stays a lazy singleton that is never resolved, and instrumentation
/// data stays in-process until an exporter task adds one.
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
    /// The fixed Grafana Loki stream label value of this service. It is part of the logging
    /// deployment contract: existing Grafana queries and dashboards filter on
    /// <c>service="Quaestura"</c>, so it never changes case and is never derived from
    /// <see cref="ServiceIdValue"/>.
    /// </summary>
    public const string LokiServiceLabelValue = "Quaestura";

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
                options.DeniedHeaderNames = ["X-Admin-AppSecret"])
            // Mandatory security response-header capability for the two admin-auth JSON
            // endpoints. It exposes no weakening options; the middleware and the per-endpoint
            // metadata decide where the fixed baseline applies (see Program.cs and
            // AdminAuthEndpoints). The full ServiceMantle management pipeline is deliberately
            // NOT used: the existing QUAESTURA_* business error envelopes stay in charge.
            .AddSecurityResponseHeaders();

        return services;
    }

    /// <summary>
    /// Registers the shared ServiceMantle logging pipeline: the mandatory-sanitizing Serilog
    /// Console pipeline plus the opt-in Grafana Loki remote sink behind the same sanitizer.
    /// Replaces the former local <c>AddRuoyuLokiSink</c>/<c>UseRuoyuSerilog</c> wiring; there is
    /// exactly one logging entry point and no second unsanitized provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Configuration contract (unchanged inputs): <c>Loki:Uri</c> stays the only remote-sink
    /// input. An empty or whitespace value disables the remote sink entirely (Console only,
    /// zero remote requests); it is never re-enabled from any legacy <c>Serilog:WriteTo</c>
    /// configuration. <c>Loki:AllowInsecureHttp</c> defaults to <c>true</c> as the explicit
    /// continuation of the existing trusted-network HTTP deployment contract and can be set to
    /// <c>false</c> to require HTTPS; network trust is never inferred from the host name.
    /// </para>
    /// <para>
    /// Levels keep the previous contract: Information by default, with Warning overrides for
    /// <c>Microsoft.AspNetCore</c> and <c>Microsoft.EntityFrameworkCore.Database.Command</c>.
    /// Console and Loki observe the same filtered, sanitized events. The fixed Loki stream
    /// label <c>service=Quaestura</c> is the only label; request, user, and instance values
    /// are structured log fields, never stream labels. Invalid endpoint shapes (relative URI,
    /// userinfo, query, fragment, or disallowed HTTP) fail host startup through the package
    /// validator with a stable error code and without echoing the submitted value.
    /// </para>
    /// </remarks>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder.</returns>
    public static IHostApplicationBuilder AddQuaesturaLogging(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddServiceMantleSerilog(options =>
        {
            options.MinimumLevel = LogLevel.Information;
            options.MinimumLevelOverrides = new Dictionary<string, LogLevel>
            {
                ["Microsoft.AspNetCore"] = LogLevel.Warning,
                ["Microsoft.EntityFrameworkCore.Database.Command"] = LogLevel.Warning,
            };
            options.IncludeScopes = true;
        });

        // Loki:Uri is typically injected from Consul KV or environment configuration. An
        // unparsable value is passed through as Enabled with a null Endpoint so the package
        // validator fails startup safely instead of silently dropping the remote sink.
        var address = builder.Configuration["Loki:Uri"];
        Uri? endpoint = null;
        if (!string.IsNullOrWhiteSpace(address))
        {
            Uri.TryCreate(address, UriKind.Absolute, out endpoint);
        }

        var allowInsecureHttp = builder.Configuration.GetValue("Loki:AllowInsecureHttp", true);

        builder.AddServiceMantleGrafanaLoki(options =>
        {
            options.Enabled = !string.IsNullOrWhiteSpace(address);
            options.Endpoint = endpoint;
            // Explicit acceptance of the existing trusted-network HTTP deployment contract.
            options.AllowInsecureHttp = allowInsecureHttp;
            options.Labels = new Dictionary<string, string>
            {
                ["service"] = LokiServiceLabelValue,
            };
        });

        return builder;
    }
}
