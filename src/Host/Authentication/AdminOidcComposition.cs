using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using ServiceMantle.Web.Http;
using SignaCore.Client.AspNetCore;
using Quaestura.Service.Endpoints;

namespace Quaestura.Host.Authentication;

public static class AdminOidcComposition
{
    public const string Prefix = "/admin/auth/oidc";
    public const string SectionName = "AdminOidc";

    public static IServiceCollection AddQuaesturaHostedLogin(
        this IServiceCollection services, IConfiguration configuration)
    {
        // The anonymous retired POST must not validate a supplied credential or fetch JWKS,
        // even when hosted login is unconfigured. Preserve the existing event on every other route.
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Events ??= new JwtBearerEvents();
            var previous = options.Events.OnMessageReceived;
            options.Events.OnMessageReceived = context =>
            {
                if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<RetiredPasswordLoginMetadata>() is not null)
                {
                    context.NoResult();
                    return Task.CompletedTask;
                }
                return previous(context);
            };
        });

        var status = AdminOidcConfigurationStatus.Create(configuration);
        services.AddSingleton(status);
        if (!status.IsConfigured) return services;

        services.AddSignaCoreHostedLogin(options =>
        {
            var section = configuration.GetSection(SectionName);
            options.Authority = section["Authority"];
            options.ClientId = section["ClientId"];
            options.ClientSecret = section["ClientSecret"];
            options.RedirectUri = section["RedirectUri"];
            options.PostLogoutRedirectUri = section["PostLogoutRedirectUri"];
            options.PostLogoutReturnPath = section["PostLogoutReturnPath"] ?? options.PostLogoutReturnPath;
            options.Scope = section["Scope"] ?? options.Scope;
            options.SessionCookieName = section["SessionCookieName"] ?? options.SessionCookieName;
            options.AntiforgeryHeaderName = section["AntiforgeryHeaderName"] ?? options.AntiforgeryHeaderName;
            if (section["TicketCapacity"] is { } capacity)
            {
                if (!int.TryParse(capacity, out var value))
                    throw new InvalidOperationException("AdminOidc:TicketCapacity must be an integer.");
                options.TicketCapacity = value;
            }
            options.AuthorizationDecision = new AdminAuthorizationDecision();
            options.ResponseWriter = new AdminOidcResponseWriter();
            // Even an empty/malformed explicit header must never fall back to a cookie.
            options.SchemeSelector = context => context.GetEndpoint()?.Metadata.GetMetadata<RetiredPasswordLoginMetadata>() is not null
                || context.Request.Headers.ContainsKey("Authorization")
                ? JwtBearerDefaults.AuthenticationScheme : null;
        });
        services.AddTransient<AdminOidcBackchannelMarker>();
        services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName)
            .AddHttpMessageHandler<AdminOidcBackchannelMarker>();

        // Keep the official factory and every storage/cleanup/concurrency behavior it owns.
        var store = services.Last(descriptor => descriptor.ServiceType == typeof(ITicketStore));
        var factory = store.ImplementationFactory
            ?? throw new InvalidOperationException("The official ticket-store factory is required.");
        services.Remove(store);
        services.AddSingleton<ITicketStore>(provider => new AdminSessionAdmission(
            (ITicketStore)factory(provider),
            provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>(),
            provider.GetRequiredService<IOptionsMonitor<SignaCoreHostedLoginOptions>>(),
            provider.GetRequiredService<TimeProvider>()));

        services.Configure<AuthenticationOptions>(options =>
        {
            options.DefaultScheme = SignaCoreHostedLoginDefaults.AuthenticationScheme;
            options.DefaultAuthenticateScheme = SignaCoreHostedLoginDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = SignaCoreHostedLoginDefaults.AuthenticationScheme;
        });
        services.Configure<AuthorizationOptions>(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder(
                    SignaCoreHostedLoginDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build();
            options.FallbackPolicy = options.DefaultPolicy;
        });
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AdminApiAuthorizationResultHandler>();
        return services;
    }

    public static void MapQuaesturaHostedLogin(this WebApplication app)
    {
        var endpoints = app.MapGroup(string.Empty).AllowAnonymous()
            .RequireServiceMantleSecurityResponseHeaders();
        if (app.Services.GetRequiredService<AdminOidcConfigurationStatus>().IsConfigured)
        {
            endpoints.MapSignaCoreHostedLogin(Prefix);
        }
        else
        {
            // Cover all new entries without registering any package protocol/session service.
            endpoints.Map(Prefix + "/{**path}", context => AdminOidcResponseWriter.WriteProblemAsync(
                context, 503, "not_configured", "QUAESTURA_OIDC_NOT_CONFIGURED",
                "Hosted sign-in is not configured.", context.RequestAborted));
        }
    }

    internal static void ConfigureTelemetry(IServiceCollection services)
    {
        // Configure the existing instrumentation; no second telemetry pipeline or exporter.
        services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            var previous = options.Filter;
            options.Filter = context => !context.Request.Path.StartsWithSegments(Prefix)
                && (previous?.Invoke(context) ?? true);
        });
        services.Configure<HttpClientTraceInstrumentationOptions>(options =>
        {
            var previous = options.FilterHttpRequestMessage;
            options.FilterHttpRequestMessage = request =>
                !(request.Options.TryGetValue(AdminOidcBackchannelMarker.SensitiveRequest, out var sensitive) && sensitive)
                && (previous?.Invoke(request) ?? true);
        });
    }
}

internal sealed class AdminOidcBackchannelMarker : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<bool> SensitiveRequest = new("Quaestura.OidcBackchannel");
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.Set(SensitiveRequest, true);
        return base.SendAsync(request, cancellationToken);
    }
}

internal sealed class AdminAuthorizationDecision : ISignaCoreAuthorizationDecision
{
    public ValueTask<SignaCoreAuthorizationDecisionResult> DecideAsync(
        System.Security.Claims.ClaimsPrincipal principal, System.Threading.CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(principal.IsInRole("admin")
            ? SignaCoreAuthorizationDecisionResult.Allowed : SignaCoreAuthorizationDecisionResult.Denied);
    }
}

// Capture one registration decision: all mappings/middleware use this immutable result.
internal sealed record AdminOidcConfigurationStatus(string[] MissingKeys)
{
    internal bool IsConfigured => MissingKeys.Length == 0;
    internal static AdminOidcConfigurationStatus Create(IConfiguration configuration) => new(
        new[] { "Authority", "ClientId", "ClientSecret", "RedirectUri" }
            .Where(key => string.IsNullOrWhiteSpace(configuration[AdminOidcComposition.SectionName + ":" + key]))
            .Select(key => AdminOidcComposition.SectionName + ":" + key).ToArray());
}
