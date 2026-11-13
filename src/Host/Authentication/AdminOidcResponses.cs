using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ServiceMantle.Web.Http;

namespace Quaestura.Host.Authentication;

/// <summary>
/// The fixed presentation layer of the admin hosted-login slice. The framework's OpenID Connect
/// handler defines the protocol; this writer keeps the browser-facing contract of the replaced
/// package integration: completed failures on real top-level HTML navigations redirect to the
/// SPA's login route with one bounded <c>reason</c> value, every non-navigation failure answers a
/// fixed Problem Details body, and no failure detail — never a code, state, nonce, or token —
/// ever reaches the response.
/// </summary>
internal static class AdminOidcResponses
{
    internal const string SignInFailedLanding = "signin_failed";
    internal const string DeniedLanding = "denied";
    internal const string CancelledLanding = "cancelled";
    internal const string ProviderUnavailableLanding = "provider_unavailable";
    internal const string RequiresReauthenticationLanding = "requires_reauthentication";
    internal const string LogoutFailedLanding = "logout_failed";

    /// <summary>
    /// The bounded landing vocabulary of one sign-in failure. The decision input is the closed
    /// failure set of the framework handler: the admission gate's denial, the user's
    /// cancellation (the upstream <c>access_denied</c> echo), transport failures of the
    /// authority's Discovery, JWKS, or token endpoints — backchannel timeouts included, which
    /// the timeout handler normalizes into transport failures — and everything else.
    /// </summary>
    internal static string LandingFor(Exception? failure)
    {
        if (failure is null)
        {
            return SignInFailedLanding;
        }

        if (failure.Message == AdminOidcConstants.AdminDeniedFailure)
        {
            return DeniedLanding;
        }

        for (var exception = failure; exception is not null; exception = exception.InnerException)
        {
            if (exception is OpenIdConnectProtocolException protocol
                && string.Equals(protocol.Data["error"] as string, "access_denied", StringComparison.Ordinal))
            {
                // The user's cancellation keeps the SPA's existing cancelled outcome.
                return CancelledLanding;
            }
        }

        return IsAuthorityUnavailable(failure) ? ProviderUnavailableLanding : SignInFailedLanding;
    }

    /// <summary>
    /// Whether the failure chain is a transport failure of the authority's endpoints — the
    /// provider-unavailable outcome. IdentityModel wraps discovery and document retrieval errors
    /// (for example <c>IOException</c>/<c>InvalidOperationException</c> around IDX20803/IDX20807),
    /// so every link of the chain is inspected.
    /// </summary>
    internal static bool IsAuthorityUnavailable(Exception failure)
    {
        for (var exception = failure; exception is not null; exception = exception.InnerException)
        {
            if (exception is HttpRequestException or TimeoutException or IOException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The landing of one wire reason presented to the fixed failure-page route. Every wire name
    /// the replaced client could produce stays parseable so historical links keep rendering;
    /// an unknown value renders the generic failure.
    /// </summary>
    internal static string LandingForWireReason(string? wireReason) => wireReason switch
    {
        "access_denied" or "user_canceled" => CancelledLanding,
        "pre_sign_in_denied" => DeniedLanding,
        "authority_unreachable" => ProviderUnavailableLanding,
        "requires_reauthentication" => RequiresReauthenticationLanding,
        _ => SignInFailedLanding
    };

    /// <summary>
    /// Presents one completed sign-in failure: a real top-level HTML navigation to one of the
    /// slice's routes is redirected to the SPA's login route with the bounded reason; every
    /// other caller answers the fixed Problem Details body — 403 for the admission denial, 503
    /// for an unreachable authority, 400 for every other protocol failure.
    /// </summary>
    internal static Task WriteSignInFailureAsync(HttpContext context, string landing, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryWriteNavigationFailure(context, landing, cancellationToken)) return Task.CompletedTask;
        return landing switch
        {
            DeniedLanding => WriteProblemAsync(context, 403, "admin_denied", "QUAESTURA_ADMIN_DENIED",
                "Administrator access denied.", cancellationToken),
            ProviderUnavailableLanding => WriteProblemAsync(context, 503, "signin_failed", "QUAESTURA_OIDC_SIGNIN_FAILED",
                "Hosted sign-in could not complete.", cancellationToken),
            _ => WriteProblemAsync(context, 400, "signin_failed", "QUAESTURA_OIDC_SIGNIN_FAILED",
                "Hosted sign-in could not complete.", cancellationToken)
        };
    }

    // Only completed outcomes on real top-level HTML navigations are projected.
    internal static bool IsHtmlNavigation(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)
            || context.Request.Headers["Sec-Fetch-Mode"] != "navigate"
            || context.Request.Headers["Sec-Fetch-Dest"] != "document") return false;
        var path = context.Request.Path.Value?.TrimEnd('/');
        if (path is null || !(path.Equals(AdminOidcConstants.Prefix + "/start", StringComparison.OrdinalIgnoreCase)
            || path.Equals(AdminOidcConstants.CallbackPath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(AdminOidcConstants.Prefix + "/signin-failed", StringComparison.OrdinalIgnoreCase)
            || path.Equals(AdminOidcConstants.LogoutReturnPath, StringComparison.OrdinalIgnoreCase))) return false;
        try { return context.Request.GetTypedHeaders().Accept?.Any(value =>
            string.Equals(value.MediaType.Value, "text/html", StringComparison.OrdinalIgnoreCase) && (value.Quality ?? 1) > 0) == true; }
        catch (FormatException) { return false; }
    }

    internal static bool TryWriteNavigationFailure(HttpContext context, string reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Response.HasStarted || !IsHtmlNavigation(context)) return false;
        context.Response.ContentLength = null;
        context.Response.ContentType = null;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect("/login?reason=" + reason);
        return true;
    }

    internal static Task WriteProblemAsync(HttpContext context, int status, string code, string applicationCode,
        string title, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Response.HasStarted) return Task.CompletedTask;
        context.Response.Headers.CacheControl = "no-store";
        context.TryGetServiceMantleCorrelationId(out var correlationId);
        return Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?>
        {
            ["errorCode"] = "quaestura.oidc." + code,
            [QuaesturaProblemDetailsExtensions.ErrorCodeFieldName] = applicationCode,
            ["correlationId"] = correlationId
        }).ExecuteAsync(context);
    }

    /// <summary>
    /// The security response-header baseline the endpoint-marked routes receive from the
    /// ServiceMantle middleware. The protocol handler answers the callback and the sign-out
    /// callback before routing — those responses carry the same six headers here.
    /// </summary>
    internal static void ApplyCallbackSafetyHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.ContentSecurityPolicy =
            "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    }

    /// <summary>
    /// The session-status endpoint's two fixed shapes: an anonymous, unknown, or expired session
    /// answers the fixed re-authentication status; a live session reports its display name and
    /// the admin-role decision (the numeric vocabulary the SPA reads). Never a token.
    /// </summary>
    internal static Task WriteSessionStatusAsync(
        HttpContext context, AuthenticateResult session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Response.HasStarted) return Task.CompletedTask;
        context.Response.Headers.CacheControl = "no-store";
        if (!session.Succeeded || session.Principal is null)
        {
            return context.Response.WriteAsJsonAsync(new
            {
                authenticated = false,
                requiresReauthentication = true,
                displayName = (string?)null,
                authorization = (int?)null
            }, cancellationToken);
        }

        return context.Response.WriteAsJsonAsync(new
        {
            authenticated = true,
            requiresReauthentication = false,
            displayName = session.Principal.FindFirst("name")?.Value,
            authorization = session.Principal.IsInRole("admin") ? 0 : 1
        }, cancellationToken);
    }
}

/// <summary>
/// Keeps the replaced slice's session-boundary challenge contract: a missing or expired browser
/// session on a JSON API answers a bare 401 (never a redirect into the hosted-auth flow), while
/// the CSRF 403 the session boundary writes passes through untouched.
/// </summary>
internal sealed class AdminApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
        PolicyAuthorizationResult result)
    {
        await _default.HandleAsync(next, context, policy, result);
        if (result.Challenged && !context.Response.HasStarted && context.Response.StatusCode == 302
            && context.Request.Path.StartsWithSegments("/admin")
            && !context.Request.Path.StartsWithSegments(AdminOidcConstants.Prefix))
        {
            context.Response.StatusCode = 401;
            context.Response.Headers.Remove("Location");
        }
    }
}
