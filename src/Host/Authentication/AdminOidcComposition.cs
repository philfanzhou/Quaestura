using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using ServiceMantle.Web.Http;
using Quaestura.Service.Endpoints;

namespace Quaestura.Host.Authentication;

/// <summary>
/// Registers the admin hosted-login slice on the framework's own OpenID Connect handler
/// (<c>Microsoft.AspNetCore.Authentication.OpenIdConnect</c>, referenced as a package in .NET 10
/// like JwtBearer; the <c>SignaCore.Client.AspNetCore</c> client package is removed entirely).
/// The shared-framework handler owns the authorization-code flow, the correlation/nonce browser
/// binding, and the standard RP-initiated logout; this host owns the server-side session
/// tickets, the CSRF boundary, the pre-sign-in admission gate on the re-verified access token,
/// the fixed response shapes, and the Bearer/session scheme split — keeping the browser-facing
/// contract of the replaced package integration unchanged.
/// </summary>
public static class AdminOidcComposition
{
    public const string Prefix = AdminOidcConstants.Prefix;
    public const string SectionName = AdminOidcSettings.SectionName;

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

        // A complete but illegal configuration fails startup with a diagnostic that names the
        // key, never the value; a missing required key starts the degraded 503 mode instead.
        var settings = AdminOidcSettings.Read(configuration);
        services.AddSingleton(settings);
        services.AddSingleton(new AdminOidcConfigurationStatus(settings.MissingKeys.ToArray()));
        if (!settings.Available) return services;

        // Hosting diagnostics log raw query strings before the application middleware runs,
        // including callbacks rejected by the correlation checks. The framework's OpenID Connect
        // handler also logs the full protocol message URLs (authorization code, state, and
        // tokens included) at Debug level; the slice's security posture keeps every protocol
        // value out of host logs, so both categories are silenced.
        services.AddLogging(logging =>
        {
            logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
            logging.AddFilter("Microsoft.AspNetCore.Authentication.OpenIdConnect", LogLevel.None);
        });

        services.TryAddSingleton(TimeProvider.System);

        // The cookie profile follows the configured callback URI's scheme (transport is a
        // deployment decision made in front of the application): every carrier cookie is Secure
        // under the HTTPS profile and written without the Secure attribute under the plain-HTTP
        // profile, where a Secure cookie would be refused by the browser. The antiforgery cookie
        // keeps its Strict SameSite; the session cookie stays Lax so the browser sends it on the
        // top-level sign-in redirect chain.
        services.AddAntiforgery(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.IsEssential = true;
        });
        services.AddOptions<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>()
            .Configure<AdminOidcSettings>((options, current) =>
            {
                options.HeaderName = current.AntiforgeryHeaderName;
                options.Cookie.SecurePolicy = current.PlainHttpProfile
                    ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
            });
        // Wrap the framework's antiforgery with the boundary's historical user-neutrality: the
        // default implementation binds each request token to the minting request's principal,
        // which would retire a pair the SPA fetched before signing in or after expiry. The
        // cookie binding and every cryptographic check stay exactly the framework's own.
        var frameworkAntiforgery = services.FirstOrDefault(
            descriptor => descriptor.ServiceType == typeof(Microsoft.AspNetCore.Antiforgery.IAntiforgery));
        if (frameworkAntiforgery?.ImplementationType is { } implementation)
        {
            services.RemoveAll<Microsoft.AspNetCore.Antiforgery.IAntiforgery>();
            services.AddSingleton<Microsoft.AspNetCore.Antiforgery.IAntiforgery>(serviceProvider =>
                new AdminUserNeutralAntiforgery(
                    (Microsoft.AspNetCore.Antiforgery.IAntiforgery)Microsoft.Extensions.DependencyInjection.ActivatorUtilities
                        .CreateInstance(serviceProvider, implementation)));
        }

        // The backchannel carries the client secret and, through it, the authority's Discovery,
        // JWKS, and token endpoints: it never follows a redirect, never carries a cookie, writes
        // no request/response log line, is excluded from HttpClient instrumentation, and
        // normalizes client-side timeouts into transport failures so a timeout keeps answering
        // the fixed provider-unavailable outcome instead of a 5xx.
        services.AddTransient<AdminOidcBackchannelMarker>();
        services.AddHttpClient(AdminOidcConstants.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            })
            .AddHttpMessageHandler<AdminOidcBackchannelMarker>()
            .AddServiceMantleCorrelationIdPropagation()
            .RemoveAllLoggers();

        // The in-process, capacity-bounded session ticket store, its periodic sweep, the
        // one-time logout-return states, and the pre-sign-in admission gate.
        services.AddSingleton(serviceProvider => new AdminOidcTicketStore(
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<AdminOidcSettings>().TicketCapacity));
        services.AddHostedService<AdminOidcTicketStoreCleanupService>();
        services.AddSingleton<AdminLogoutReturnStates>();
        services.AddSingleton<AdminSessionRevocations>();
        services.AddSingleton<AdminSessionAdmission>();

        services.AddAuthentication()
            .AddCookie(AdminOidcConstants.SessionScheme, options =>
            {
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.IsEssential = true;
                // The SPA navigates to the sign-in start explicitly, so the cookie handler's own
                // login redirect is never exercised and keeps the hosted-login start as its
                // inert default target. The ticket's lifetime comes from the admission-clamped
                // expiry set at ticket receipt; the fallback span below is only a defensive
                // bound. Sessions never slide: an expired session answers the fixed
                // re-authentication result, never a silent refresh.
                options.LoginPath = AdminOidcConstants.Prefix + "/start";
                options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                options.SlidingExpiration = false;
            })
            .AddOpenIdConnect(AdminOidcConstants.OidcScheme, options =>
            {
                options.ResponseType = OpenIdConnectResponseType.Code;
                // The code flow's standard response mode — and the only mode the authority
                // delivers. The framework's own default is form_post.
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                // The wire contract of the replaced slice: no telemetry parameters on the
                // authorize or end-session redirect, and exactly the requested scopes.
                options.DisableTelemetry = true;
                options.SaveTokens = true;
                options.MapInboundClaims = false;
                options.SignInScheme = AdminOidcConstants.SessionScheme;
                options.BackchannelTimeout = TimeSpan.FromSeconds(30);
                // The authorization parameters travel in the redirect itself; the authority's
                // discovery entry for PAR must never be taken.
                options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
                // The nonce and correlation carrier cookies must survive the plain-HTTP profile:
                // the framework defaults (SameSite=None with Secure=Always) are refused by
                // browsers there. Lax is enough — the callback is a top-level navigation,
                // exactly like the session cookie itself. The five-minute lifetime matches the
                // replaced slice's handshake window (the framework default is fifteen minutes).
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(5);
                options.ProtocolValidator.NonceLifetime = TimeSpan.FromMinutes(5);
                // The documented 30-second lifetime tolerance of the replaced integration,
                // matching the Bearer trust snapshot.
                options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = "role";

                // Strict authorization-response shape (kept from the replaced slice; the
                // framework handler itself accepts a weaker form): state/iss/code/error are
                // single-valued, the RFC 9207 issuer parameter is required and must equal the
                // discovery issuer — the mix-up protection of a single-authority client — and
                // the state must unprotect before any authority interaction. The raw query is
                // the decision input: by this event the handler has already replaced the
                // message's state with its userstate portion.
                options.Events.OnMessageReceived = async context =>
                {
                    foreach (var member in new[] { "state", "iss", "code", "error" })
                    {
                        if (context.Request.Query[member].Count > 1)
                        {
                            context.Fail(AdminOidcConstants.InvalidResponseFailure);
                            return;
                        }
                    }

                    var issuer = context.ProtocolMessage.Iss;
                    var state = context.Request.Query["state"].ToString();
                    if (string.IsNullOrEmpty(state)
                        || string.IsNullOrEmpty(issuer)
                        || context.Options.StateDataFormat.Unprotect(state) is null)
                    {
                        context.Fail(AdminOidcConstants.InvalidResponseFailure);
                        return;
                    }

                    if (context.Options.ConfigurationManager is not { } manager
                        || await manager.GetConfigurationAsync(context.HttpContext.RequestAborted) is not { } configuration
                        || !string.Equals(issuer, configuration.Issuer, StringComparison.Ordinal))
                    {
                        context.Fail(AdminOidcConstants.InvalidResponseFailure);
                    }
                };

                // The pre-sign-in admission gate (the package's ITicketStore admission seam): a
                // session may only be established for a subject whose re-verified access token
                // carries the admin role and matches the verified ID token. Failing validation
                // prevents the sign-in entirely — no ticket and no cookie are written, and an
                // existing session is neither replaced nor extended. An unsigned ID token is
                // refused here as well: the framework skips the signature requirement for tokens
                // received over its authenticated backchannel.
                options.Events.OnTokenValidated = async context =>
                {
                    if (string.Equals(context.SecurityToken?.SignatureAlgorithm, "none", StringComparison.Ordinal)
                        || context.SecurityToken is not { } idToken)
                    {
                        context.Fail(AdminOidcConstants.InvalidResponseFailure);
                        return;
                    }

                    var admitted = await context.HttpContext.RequestServices
                        .GetRequiredService<AdminSessionAdmission>()
                        .AdmitAsync(idToken, context.TokenEndpointResponse?.AccessToken,
                            context.HttpContext.RequestAborted);
                    if (admitted is null)
                    {
                        context.Fail(AdminOidcConstants.AdminDeniedFailure);
                        return;
                    }

                    // The server-side session never outlives the re-verified access token, and
                    // the cookie is persistent with the same boundary so a browser restart keeps
                    // neither.
                    context.Principal = admitted.Principal;
                    if (context.Properties is { } properties)
                    {
                        properties.ExpiresUtc = admitted.AccessTokenExpiry;
                        properties.IsPersistent = true;
                    }
                };

                // A token response whose lifetime has already elapsed never creates a session:
                // the failure surfaces through the fixed sign-in failure landing. (The
                // ticket-received event cannot fail the flow — the framework ignores a Fail
                // result there — so the boundary is checked where it still can.)
                options.Events.OnTokenResponseReceived = context =>
                {
                    if (int.TryParse(context.TokenEndpointResponse?.ExpiresIn,
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var seconds)
                        && seconds <= 0)
                    {
                        context.Fail("The token response is already expired.");
                    }

                    return Task.CompletedTask;
                };

                // Every protocol failure lands on one bounded SPA outcome (or the fixed Problem
                // Details body for non-navigation callers); no failure detail ever reaches the
                // browser. The callback is answered before routing, so the response carries the
                // security-header baseline the routed surface gets from its endpoint markers.
                options.Events.OnRemoteFailure = async context =>
                {
                    AdminOidcResponses.ApplyCallbackSafetyHeaders(context.Response);
                    var landing = AdminOidcResponses.LandingFor(context.Failure);
                    context.HandleResponse();
                    await AdminOidcResponses.WriteSignInFailureAsync(
                        context.HttpContext, landing, context.HttpContext.RequestAborted);
                };

                // The completed sign-in redirect carries the same baseline; the admission
                // already clamped the session expiry, and the fallback below only covers a
                // missing admission boundary.
                options.Events.OnTicketReceived = context =>
                {
                    AdminOidcResponses.ApplyCallbackSafetyHeaders(context.Response);
                    if (context.Properties is { } properties && properties.ExpiresUtc is null
                        && DateTimeOffset.TryParse(properties.GetTokenValue("expires_at"),
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal, out var expiry))
                    {
                        properties.ExpiresUtc = expiry.ToUniversalTime();
                        properties.IsPersistent = true;
                    }

                    return Task.CompletedTask;
                };

                // The authorization redirect carries the configured callback URI exactly as
                // registered with the authority — never the request-derived one, which a
                // TLS-terminating proxy would rewrite to the internal scheme and host.
                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    context.ProtocolMessage.RedirectUri = context.HttpContext.RequestServices
                        .GetRequiredService<AdminOidcSettings>().RedirectUri;
                    return Task.CompletedTask;
                };

                // The standard RP-initiated logout: the end-session redirect carries the
                // configured post-logout URI exactly as registered with the authority (never a
                // request-scheme-derived one, which a TLS-terminating proxy would break), and
                // the protected state lands the browser on the configured SPA landing.
                options.Events.OnRedirectToIdentityProviderForSignOut = context =>
                {
                    var current = context.HttpContext.RequestServices.GetRequiredService<AdminOidcSettings>();
                    if (!string.IsNullOrEmpty(current.PostLogoutRedirectUri))
                    {
                        context.ProtocolMessage.PostLogoutRedirectUri = current.PostLogoutRedirectUri;
                    }

                    context.Properties.RedirectUri = current.PostLogoutReturnPath;
                    return Task.CompletedTask;
                };

                // A sign-out callback whose state does not unprotect, is duplicated, or was
                // already consumed is not a completed logout: the fixed failure presentation
                // (the SPA logout-failed landing for real navigations, the fixed 400 page
                // otherwise) instead of an empty 200.
                options.Events.OnSignedOutCallbackRedirect = async context =>
                {
                    var states = context.HttpContext.RequestServices.GetRequiredService<AdminLogoutReturnStates>();
                    var query = context.HttpContext.Request.Query["state"];
                    var completed = query.Count == 1
                        && !string.IsNullOrEmpty(query[0])
                        && !string.IsNullOrEmpty(context.Properties?.RedirectUri)
                        && states.Consume(query[0]!);
                    if (completed)
                    {
                        // The default redirect to the protected state's landing stands.
                        return;
                    }

                    AdminOidcResponses.ApplyCallbackSafetyHeaders(context.Response);
                    if (!AdminOidcResponses.TryWriteNavigationFailure(
                            context.HttpContext, AdminOidcResponses.LogoutFailedLanding,
                            context.HttpContext.RequestAborted))
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        context.Response.ContentType = "text/html; charset=utf-8";
                        context.Response.Headers.CacheControl = "no-store";
                        await context.Response.WriteAsync(
                            """
                            <!doctype html>
                            <html lang="en">
                            <head><title>Sign-out could not be confirmed</title></head>
                            <body>
                            <h1>Sign-out could not be confirmed</h1>
                            <p>The sign-out result could not be matched. Sign in again if you were trying to use the application.</p>
                            <p><a href="/">Back</a></p>
                            </body>
                            </html>
                            """,
                            context.HttpContext.RequestAborted);
                    }

                    context.HandleResponse();
                };
            });

        // The per-deployment protocol values, the session store, and the DI TimeProvider (the
        // test suite's manual clock) that drives both handlers' expiry decisions. The OpenID
        // Connect backchannel is the hardened named client above.
        services.AddOptions<CookieAuthenticationOptions>(AdminOidcConstants.SessionScheme)
            .Configure<AdminOidcSettings>((options, current) =>
            {
                options.Cookie.Name = current.SessionCookieName;
                options.Cookie.SecurePolicy = current.PlainHttpProfile
                    ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
            })
            .Configure<AdminOidcTicketStore, TimeProvider>((options, store, time) =>
            {
                options.SessionStore = store;
                options.TimeProvider = time;
            });
        services.AddOptions<OpenIdConnectOptions>(AdminOidcConstants.OidcScheme)
            .Configure<AdminOidcSettings>(ApplySettings)
            .Configure<TimeProvider, IHttpClientFactory>((options, time, factory) =>
            {
                options.TimeProvider = time;
                options.Backchannel = factory.CreateClient(AdminOidcConstants.HttpClientName);
            });

        // The forwarding scheme the admin policy authenticates against: it routes a request to
        // the host's JwtBearer handler exactly when the replaced slice's selector did — a
        // present Authorization header (even an empty one) or the retired password route — and
        // to the cookie session everywhere else. A present but invalid Bearer header therefore
        // never steals the session's role claims. Challenges keep the fixed 401 contract instead
        // of redirecting anonymous API calls to the sign-in start.
        services.AddAuthentication()
            .AddPolicyScheme(AdminOidcConstants.AuthenticationScheme, "Quaestura admin hosted login", options =>
            {
                options.ForwardDefaultSelector = context =>
                    context.GetEndpoint()?.Metadata.GetMetadata<RetiredPasswordLoginMetadata>() is not null
                    || context.Request.Headers.ContainsKey("Authorization")
                        ? JwtBearerDefaults.AuthenticationScheme
                        : AdminOidcConstants.SessionScheme;
            });
        services.Configure<AuthenticationOptions>(options =>
        {
            options.DefaultScheme = AdminOidcConstants.AuthenticationScheme;
            options.DefaultAuthenticateScheme = AdminOidcConstants.AuthenticationScheme;
            options.DefaultChallengeScheme = AdminOidcConstants.AuthenticationScheme;
        });
        services.Configure<AuthorizationOptions>(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder(
                    AdminOidcConstants.AuthenticationScheme).RequireAuthenticatedUser().Build();
            options.FallbackPolicy = options.DefaultPolicy;
        });
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AdminApiAuthorizationResultHandler>();
        return services;
    }

    private static void ApplySettings(OpenIdConnectOptions options, AdminOidcSettings settings)
    {
        options.Authority = settings.Authority;
        options.ClientId = settings.ClientId;
        options.ClientSecret = settings.ClientSecret;
        options.CallbackPath = AdminOidcConstants.CallbackPath;
        options.SignedOutCallbackPath = AdminOidcConstants.LogoutReturnPath;
        options.SignedOutRedirectUri = settings.PostLogoutReturnPath;
        options.Scope.Clear();
        foreach (var scope in settings.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            options.Scope.Add(scope);
        }

        // Plain-HTTP authorities follow the same rule the replaced client applied (ADR 0008:
        // transport is a deployment decision made in front of the application); HTTPS
        // authorities keep the framework's metadata requirement.
        options.RequireHttpsMetadata = !(Uri.TryCreate(settings.Authority, UriKind.Absolute, out var authority)
            && authority.Scheme == Uri.UriSchemeHttp);
        options.NonceCookie.SecurePolicy = settings.PlainHttpProfile ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
        options.CorrelationCookie.SecurePolicy = settings.PlainHttpProfile ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
    }

    /// <summary>
    /// Mounts the hosted-login endpoints: with a complete configuration the slice's five routes
    /// (the framework handler itself intercepts the callback and the sign-out callback), and
    /// the fixed 503 catch-all covering the whole area otherwise.
    /// </summary>
    public static void MapQuaesturaHostedLogin(this WebApplication app)
    {
        app.MapQuaesturaHostedLoginEndpoints();
    }

    /// <summary>
    /// The session boundary of the protected admin API (the CSRF and re-authentication checks
    /// the replaced client enforced inside its own session handler). Registered after
    /// <c>UseAuthentication</c>; inert in the unconfigured mode.
    /// </summary>
    public static void UseQuaesturaHostedLoginSession(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AdminOidcConfigurationStatus>().IsConfigured) return;
        app.UseMiddleware<AdminOidcSessionMiddleware>();
    }

    internal static void ConfigureTelemetry(IServiceCollection services)
    {
        // Configure the existing instrumentation; no second telemetry pipeline or exporter.
        services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            var previous = options.Filter;
            options.Filter = context => !context.Request.Path.StartsWithSegments(AdminOidcConstants.Prefix)
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

/// <summary>
/// The backchannel marker of the OpenID Connect named client: every request it carries is
/// excluded from HttpClient instrumentation (the authority's URLs and form bodies stay out of
/// traces and metrics), and a client-side timeout — a cancellation that did not come from the
/// caller — is normalized into a transport failure so the protocol outcomes keep answering the
/// fixed provider-unavailable result instead of an unhandled exception.
/// </summary>
internal sealed class AdminOidcBackchannelMarker : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<bool> SensitiveRequest = new("Quaestura.OidcBackchannel");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.Set(SensitiveRequest, true);
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(
                "The authority request timed out.", new TimeoutException(exception.Message, exception));
        }
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
