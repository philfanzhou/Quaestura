using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql;
using ServiceMantle.Migration;
using ServiceMantle.Persistence.Relational;
using Quaestura.Database;
using Quaestura.Host.Authentication;
using Quaestura.Service.Middleware;
using ServiceMantle;
using ServiceMantle.Database.PostgreSql.Migration;
using ServiceMantle.Health;
using ServiceMantle.Web.Http;

namespace Quaestura.Host;

/// <summary>
/// The whitelisted Problem Details extension field names through which the Quaestura instance
/// level error contract is projected. <c>quaesturaErrorCode</c> carries the original uppercase
/// <c>QUAESTURA_*</c> code of the thrown domain exception; <c>quaesturaValidationErrors</c>
/// carries the developer-declared validation messages joined for display. No other exception
/// content is ever projected into a response.
/// </summary>
public static class QuaesturaProblemDetailsExtensions
{
    public const string ErrorCodeFieldName = "quaesturaErrorCode";
    public const string ValidationErrorsFieldName = "quaesturaValidationErrors";
}

/// <summary>
/// Registers the ServiceMantle host identity, the safe request-header projector, the mandatory
/// security response-header capability, the base OpenTelemetry instrumentation, the shared
/// migration orchestration capability (PostgreSQL advisory lock plus the Quaestura migration
/// executor), and the fixed health probe endpoints capability for Quaestura, together with the
/// shared startup receipt and scoped mapped-schema health source. This composition performs
/// zero disk writes and registers no telemetry
/// exporter: the Bootstrap file store stays a lazy singleton that is never resolved, and
/// instrumentation data stays in-process until an exporter task adds one.
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
                InstanceId.CreateRandom(ServiceId.Parse(ServiceIdValue)))
            // Default options: AspNetCore + HttpClient tracing and runtime metrics, no exporter.
            .AddOpenTelemetryInstrumentation()
            // Deny the SignaCore admin-login secret explicitly so the safe request-header
            // projector (RequestHeaderDiagnosticProjector) always redacts it. This registers no
            // automatic header logging; projecting headers stays an explicit caller decision.
            .AddSensitiveHeaders(options =>
                options.DeniedHeaderNames = ["X-Admin-AppSecret"])
            // Mandatory security response-header capability for the admin-auth endpoints,
            // including the optional hosted-auth group. It exposes no weakening options; the middleware and the per-endpoint
            // metadata decide where the fixed baseline applies (see Program.cs and
            // AdminAuthEndpoints). The full ServiceMantle management pipeline is deliberately
            // NOT used: only the capabilities Quaestura wires itself are registered.
            .AddSecurityResponseHeaders()
            // Safe Problem Details mappings for every domain exception escaping an /admin JSON
            // endpoint (the boundary itself is wired in Program.cs through a /admin-only
            // UseWhen branch). Each exact exception type maps to its fixed status with a stable
            // lowercase quaestura.* error code; the instance-level uppercase QUAESTURA_* code is
            // projected only through the whitelisted quaesturaErrorCode extension field, never
            // through the exception message. Unmapped exceptions fall through to the library's
            // fixed safe 500 (errorCode http.internal_server_error); the legacy
            // QUAESTURA_INTERNAL_ERROR value is retired with the old envelope.
            .AddExceptionMapping<EntityNotFoundException>(
                StatusCodes.Status404NotFound,
                "quaestura.entity_not_found",
                "The requested resource was not found.",
                new Dictionary<string, Func<EntityNotFoundException, object?>>
                {
                    [QuaesturaProblemDetailsExtensions.ErrorCodeFieldName] =
                        exception => exception.ErrorCode,
                })
            .AddExceptionMapping<BusinessPreconditionException>(
                StatusCodes.Status422UnprocessableEntity,
                "quaestura.business_precondition_failed",
                "The business precondition for this operation was not met.",
                new Dictionary<string, Func<BusinessPreconditionException, object?>>
                {
                    [QuaesturaProblemDetailsExtensions.ErrorCodeFieldName] =
                        exception => exception.ErrorCode,
                })
            .AddExceptionMapping<ForbiddenException>(
                StatusCodes.Status403Forbidden,
                "quaestura.forbidden",
                "You are not allowed to perform this operation.",
                new Dictionary<string, Func<ForbiddenException, object?>>
                {
                    [QuaesturaProblemDetailsExtensions.ErrorCodeFieldName] =
                        exception => exception.ErrorCode,
                })
            .AddExceptionMapping<ValidationException>(
                StatusCodes.Status400BadRequest,
                "quaestura.validation_failed",
                "The request failed validation.",
                new Dictionary<string, Func<ValidationException, object?>>
                {
                    [QuaesturaProblemDetailsExtensions.ErrorCodeFieldName] =
                        _ => "QUAESTURA_VALIDATION_FAILED",
                    // Developer-declared validation messages (endpoint guards and FluentValidation
                    // error texts), not raw exception text from an arbitrary source.
                    [QuaesturaProblemDetailsExtensions.ValidationErrorsFieldName] =
                        exception => string.Join("; ", exception.Errors.Select(e => e.ErrorMessage)),
                })
            // The DomainException base type is thrown directly at several call sites with
            // different StatusCode values, so it maps through ordered candidates grouped by the
            // declared status: the 409 conflict group, the declared 500 failure group, and an
            // unconditional 500 fallback for any undeclared status (fail-closed, same shape).
            .AddConditionalExceptionMapping<DomainException>(
            [
                new ExceptionMappingCandidate<DomainException>(
                    StatusCodes.Status409Conflict,
                    "quaestura.conflict",
                    "The operation conflicts with the current state.",
                    condition: exception =>
                        exception.StatusCode == HttpStatusCode.Conflict,
                    extensionFields: new Dictionary<string, Func<DomainException, object?>>
                    {
                        [QuaesturaProblemDetailsExtensions.ErrorCodeFieldName] =
                            exception => exception.ErrorCode,
                    }),
                new ExceptionMappingCandidate<DomainException>(
                    StatusCodes.Status500InternalServerError,
                    "quaestura.domain_error",
                    "The operation failed.",
                    condition: exception =>
                        exception.StatusCode == HttpStatusCode.InternalServerError,
                    extensionFields: new Dictionary<string, Func<DomainException, object?>>
                    {
                        [QuaesturaProblemDetailsExtensions.ErrorCodeFieldName] =
                            exception => exception.ErrorCode,
                    }),
                new ExceptionMappingCandidate<DomainException>(
                    StatusCodes.Status500InternalServerError,
                    "quaestura.unexpected_domain_status",
                    "The operation failed.",
                    extensionFields: new Dictionary<string, Func<DomainException, object?>>
                    {
                        [QuaesturaProblemDetailsExtensions.ErrorCodeFieldName] =
                            exception => exception.ErrorCode,
                    }),
            ])
            // InvalidOperationException is the image-validation signal (fixed developer-declared
            // messages from ImageValidationHelper) and otherwise an unexpected failure: the
            // ordered candidates keep the 400 image-validation contract and normalize every other
            // occurrence to a fixed 500 without projecting the message.
            .AddConditionalExceptionMapping<InvalidOperationException>(
            [
                new ExceptionMappingCandidate<InvalidOperationException>(
                    StatusCodes.Status400BadRequest,
                    "quaestura.validation_invalid_image",
                    "The submitted image is invalid.",
                    condition: exception =>
                        exception.Message.Contains("Image", StringComparison.OrdinalIgnoreCase),
                    extensionFields: new Dictionary<string, Func<InvalidOperationException, object?>>
                    {
                        [QuaesturaProblemDetailsExtensions.ErrorCodeFieldName] =
                            _ => "QUAESTURA_VALIDATION_INVALID_IMAGE",
                        [QuaesturaProblemDetailsExtensions.ValidationErrorsFieldName] =
                            exception => exception.Message,
                    }),
                new ExceptionMappingCandidate<InvalidOperationException>(
                    StatusCodes.Status500InternalServerError,
                    "quaestura.invalid_operation",
                    "An unexpected error occurred."),
            ])
            // The PostgreSQL session advisory lock that serializes multi-instance migration:
            // it covers the orchestrator's initial inspection, the legacy takeover, the EF Core
            // migration execution, and the final inspection (never only the Migrate call).
            .AddMigrationLockProvider<PostgreSqlMigrationLockProvider>()
            // The consuming service's migration executor plus the scoped orchestrator. Each
            // scope resolves its own executor instance; the lock/state machine itself belongs to
            // the shared orchestrator and is never reimplemented locally.
            .AddDatabaseMigration<QuaesturaMigrationExecutor>()
            .AddDatabaseTargetPreparationProvider<PostgreSqlDatabaseTargetPreparationProvider>()
            // Fixed /health/live, /health/ready, and /health routes (mapped in Program.cs) with
            // the default bounded snapshot read. No readiness contributors are registered: S3
            // stays in its warning mode and is deliberately not a readiness condition.
            .AddServiceMantleHealthEndpoints();

        // Direct gate invocation in Program.cs; no hosted runner or Bootstrap store resolution.
        services.AddServiceMantlePostgreSqlDeploymentCapability();
        services.AddServiceMantleStartupDatabaseGateServices();
        services.AddServiceMantleEfCoreHealthSnapshotSource<QuaesturaDbContext>(
            ServiceId.Parse(ServiceIdValue), new PostgreSqlDatabaseProbeFailureClassifier(),
            EfCoreHealthSnapshotProbeMode.MappedSchema, errorCodePrefix: "health");
        services.RemoveAll<IServiceHealthSnapshotSource>();
        services.AddScoped<TestingHealthSnapshotSource>();
        services.AddScoped<IServiceHealthSnapshotSource>(provider =>
            provider.GetRequiredService<QuaesturaDbContext>().Database.IsRelational()
                ? provider.GetRequiredService<EfCoreHealthSnapshotSource<QuaesturaDbContext>>()
                : provider.GetRequiredService<TestingHealthSnapshotSource>());

        AdminOidcComposition.ConfigureTelemetry(services);
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
