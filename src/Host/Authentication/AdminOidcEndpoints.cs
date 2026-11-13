using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Quaestura.Host.Authentication;

/// <summary>
/// The hosted-login endpoints. Missing configuration returns the fixed 503 Problem Details
/// answer on every route of the area; with a complete configuration this host maps the sign-in
/// start, the fixed failure page, the antiforgery-token issuer, the session status, and the
/// logout, while the framework's OpenID Connect handler itself intercepts the callback and the
/// sign-out callback on their configured paths. Every route is anonymous: the session boundary
/// never applies under <c>/admin/auth</c>, and each endpoint enforces its own fixed outcomes.
/// </summary>
public static class AdminOidcEndpoints
{
    /// <summary>
    /// A fixed stripe of gates keyed by session-key hash: one session always maps to one gate,
    /// so its revoke sequence is strictly serialized, while unrelated sessions spread across
    /// stripes. The bounded stripe count keeps the gate set from growing with every login.
    /// </summary>
    private static readonly SemaphoreSlim[] LogoutGates =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>
    /// Maps the hosted-login endpoints under the fixed prefix. The group's conventions apply to
    /// every mapped route: anonymous access and the ServiceMantle security response headers.
    /// </summary>
    public static IEndpointConventionBuilder MapQuaesturaHostedLoginEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.ServiceProvider.GetRequiredService<AdminOidcSettings>();
        var group = endpoints.MapGroup(AdminOidcConstants.Prefix)
            .AllowAnonymous()
            .RequireServiceMantleSecurityResponseHeaders();
        if (!settings.Available)
        {
            // Cover all new entries without registering any protocol or session component.
            return group.Map("/{**path}", static context => AdminOidcResponses.WriteProblemAsync(
                context, 503, "not_configured", "QUAESTURA_OIDC_NOT_CONFIGURED",
                "Hosted sign-in is not configured.", context.RequestAborted));
        }

        group.MapGet("/start", Start);
        group.MapGet("/signin-failed", SignInFailedPage);
        group.MapGet("/csrf", Csrf);
        group.MapGet("/session", Session);
        group.MapPost("/logout", Logout);
        return group;
    }

    /// <summary>
    /// Starts the authorization-code flow. The strict input guard below has already validated
    /// the optional single-valued returnUrl (or answered the fixed 400 body) before the
    /// challenge runs, so a rejected input never consults the authority; the challenge stores
    /// the landing path in the protected state, and the completed sign-in redirects back to
    /// exactly that path. An unreachable authority answers the fixed provider-unavailable
    /// result — the only failure the start endpoint can produce.
    /// </summary>
    private static async Task Start(HttpContext context)
    {
        var returnUrl = context.Request.Query["returnUrl"];
        string? target = null;
        if (returnUrl.Count == 0)
        {
            target = "/";
        }
        else if (returnUrl.Count == 1 && AdminOidcSettings.IsValidReturnUrl(returnUrl[0]))
        {
            target = returnUrl[0];
        }

        if (target is null)
        {
            // A rejected input never starts a pending sign-in; the presentation keeps the
            // historical shape: the SPA landing for navigations, the fixed 400 body otherwise.
            if (!AdminOidcResponses.TryWriteNavigationFailure(
                    context, AdminOidcResponses.SignInFailedLanding, context.RequestAborted))
            {
                await AdminOidcResponses.WriteProblemAsync(context, 400, "invalid_return_url",
                    "QUAESTURA_OIDC_INVALID_RETURN_URL", "The return address is not allowed.", context.RequestAborted);
            }

            return;
        }

        try
        {
            await context.ChallengeAsync(AdminOidcConstants.OidcScheme, new AuthenticationProperties
            {
                RedirectUri = target
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !context.RequestAborted.IsCancellationRequested)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            await AdminOidcResponses.WriteSignInFailureAsync(
                context, AdminOidcResponses.ProviderUnavailableLanding, context.RequestAborted);
        }
    }

    /// <summary>
    /// The fixed failure-page route: renders the bounded landing of the historical wire reason
    /// (an unknown value renders the generic failure), exactly as the replaced client's failure
    /// page did.
    /// </summary>
    private static Task SignInFailedPage(HttpContext context) =>
        AdminOidcResponses.WriteSignInFailureAsync(
            context,
            AdminOidcResponses.LandingForWireReason(context.Request.Query["reason"].ToString()),
            context.RequestAborted);

    /// <summary>
    /// Issues the antiforgery credential pair: the cookie is set alongside the token, and
    /// issuance is a public, unauthenticated read — the protection is the cookie binding, not
    /// secrecy of the issuer. The pair is user-neutral (see
    /// <see cref="AdminUserNeutralAntiforgery"/>), so one fetch is reused across the login.
    /// The declared shape is kept: <c>{"token": "..."}</c> with <c>no-store</c>.
    /// </summary>
    private static async Task Csrf(HttpContext context, IAntiforgery antiforgery, CancellationToken cancellationToken)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(
            """{"token":""" + JsonSerializer.Serialize(tokens.RequestToken) + "}",
            cancellationToken);
    }

    /// <summary>
    /// Reports the live session's fixed status shape from the server-side ticket: never a
    /// token, never a reason other than authenticated/reauthenticationRequired.
    /// </summary>
    private static async Task Session(HttpContext context)
    {
        var session = await context.AuthenticateAsync(AdminOidcConstants.SessionScheme);
        await AdminOidcResponses.WriteSessionStatusAsync(context, session, context.RequestAborted);
    }

    /// <summary>
    /// The local-session-first logout with the standard RP-initiated upstream redirect:
    /// <list type="bullet">
    /// <item>A missing or wrong antiforgery credential answers the fixed 403 Problem Details
    /// body and touches nothing.</item>
    /// <item>Without a live session there is nothing to revoke locally and no ID token to hint:
    /// the fixed local-only body.</item>
    /// <item>With one, the end-session redirect is constructed through the stored ID token
    /// (id_token_hint, the registered post-logout URI, and the protected state), then the local
    /// session is revoked and the browser is handed the verified end-session URL in the fixed
    /// prepared body. A logout whose upstream redirect cannot be constructed still keeps the
    /// local revocation and answers the fixed local-only body.</item>
    /// </list>
    /// One session key is revoked at most once, enforced by striped per-key gates that
    /// serialize concurrent logouts of the same browser session.
    /// </summary>
    private static async Task Logout(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            // A missing or wrong token changes nothing: no session is touched and no upstream
            // redirect is attempted. The fixed answer is indistinguishable for both defect shapes.
            await AdminOidcResponses.WriteProblemAsync(context, 403, "csrf_rejected",
                "QUAESTURA_CSRF_REJECTED", "The request failed antiforgery validation.", context.RequestAborted);
            return;
        }

        var settings = context.RequestServices.GetRequiredService<AdminOidcSettings>();
        if (!context.Request.Cookies.TryGetValue(settings.SessionCookieName, out var key)
            || string.IsNullOrEmpty(key))
        {
            // No session cookie means there is nothing to revoke locally and no ID token to hint:
            // the fixed local-only result.
            await WriteOutcomeAsync(context, "local_only");
            return;
        }

        // One stripe per session key serializes the revoke sequence, so concurrent logouts of the
        // same session revoke exactly once and construct exactly one end-session redirect.
        var gate = LogoutGates[Math.Abs(key.GetHashCode()) % LogoutGates.Length];
        await gate.WaitAsync(context.RequestAborted);
        try
        {
            // Authentication results are cached per request, so the gate alone does not decide
            // liveness: the revocation marker answers whether this key is already gone.
            if (!context.RequestServices.GetRequiredService<AdminSessionRevocations>().TryMark(key))
            {
                // Another request already revoked this session: local-only, no second end-session
                // redirect.
                await WriteOutcomeAsync(context, "local_only");
                return;
            }

            var session = await context.AuthenticateAsync(AdminOidcConstants.SessionScheme);
            if (!session.Succeeded)
            {
                // The session was expired or unknown: nothing is revoked upstream or locally.
                await WriteOutcomeAsync(context, "local_only");
                return;
            }

            try
            {
                // Construct the end-session redirect first: the OpenID Connect handler reads the
                // stored ID token from the still-live session and sets the redirect to the
                // authority's end-session endpoint (id_token_hint included).
                await context.SignOutAsync(AdminOidcConstants.OidcScheme);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                || !context.RequestAborted.IsCancellationRequested)
            {
                context.RequestAborted.ThrowIfCancellationRequested();
                // The upstream redirect could not be constructed (for example the authority is
                // unreachable or publishes no end-session endpoint). The local session is still
                // revoked, never restored, and never retried within this request.
                await context.SignOutAsync(AdminOidcConstants.SessionScheme);
                if (!context.Response.HasStarted)
                {
                    await WriteOutcomeAsync(context, "local_only");
                }

                return;
            }

            var logoutUrl = context.Response.Headers.Location.ToString();
            // The local session ends after the redirect is set: the server-side ticket is removed
            // and the cookie deleted.
            await context.SignOutAsync(AdminOidcConstants.SessionScheme);
            if (context.Response.HasStarted)
            {
                return;
            }

            if (!string.IsNullOrEmpty(logoutUrl))
            {
                context.Response.StatusCode = 200;
                context.Response.Headers.Remove("Location");
                context.Response.ContentLength = null;
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(
                    new { outcome = "prepared", logoutUrl }, context.RequestAborted);
            }
            else
            {
                await WriteOutcomeAsync(context, "local_only");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task WriteOutcomeAsync(HttpContext context, string outcome)
    {
        context.Response.StatusCode = 200;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { outcome }, context.RequestAborted);
    }
}

/// <summary>
/// The one-time logout-return states of the standard RP-initiated logout: a completed return's
/// protected state is consumed exactly once, so a replayed return answers the fixed failure
/// instead of re-rendering the completed landing — the single-use contract the replaced client
/// enforced through its own correlation store. States are held only as hashes, bounded in
/// count, and reclaimed lazily after the five-minute logout window.
/// </summary>
internal sealed class AdminLogoutReturnStates(TimeProvider clock)
{
    private const int Capacity = 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly LinkedList<(string Hash, DateTimeOffset Consumed)> _order = new(); // oldest first
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

    /// <summary>
    /// Consumes one state: returns <see langword="true"/> exactly once for a state this store
    /// has not seen before (marking it consumed), and <see langword="false"/> for a replay.
    /// </summary>
    internal bool Consume(string state)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            while (_order.First is { } oldest && now - oldest.Value.Consumed > Lifetime)
            {
                _consumed.Remove(oldest.Value.Hash);
                _order.RemoveFirst();
            }

            if (!_consumed.Add(hash))
            {
                return false;
            }

            _order.AddLast((hash, now));
            if (_order.Count > Capacity && _order.First is { } evicted)
            {
                _consumed.Remove(evicted.Value.Hash);
                _order.RemoveFirst();
            }

            return true;
        }
    }
}
