using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Quaestura.Host.Authentication;

/// <summary>
/// Request boundary for cookie-session access to the protected admin API — the CSRF and
/// re-authentication boundary the replaced client enforced inside its own session handler.
/// Only requests that actually carry the opaque session cookie enter the boundary, and only
/// under <c>/admin</c> outside the anonymous <c>/admin/auth</c> area.
/// </summary>
/// <remarks>
/// <para>
/// A present <c>Authorization</c> header (even an empty one — the historical header-first rule
/// of the replaced slice's scheme selector) is served by the boundary only after the host's
/// JwtBearer handler independently verifies it; a present but invalid header answers the bare
/// 401 and never falls back to the session, so a Bearer principal can never borrow the cookie
/// session's role claims. A verified Bearer request passes without any CSRF requirement.
/// </para>
/// <para>
/// The CSRF validation itself runs here for every session-authenticated unsafe method (RFC 9110
/// safe methods are GET, HEAD, OPTIONS, and TRACE): the user-neutral antiforgery pair issued by
/// the <c>/admin/auth/oidc/csrf</c> endpoint must validate, otherwise the bare 403 is answered
/// and the endpoint never runs. An unknown or expired session answers the bare 401; the
/// distinction is not observable.
/// </para>
/// </remarks>
public sealed class AdminOidcSessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var settings = context.RequestServices.GetRequiredService<AdminOidcSettings>();
        if (settings.Available
            && context.Request.Path.StartsWithSegments("/admin")
            && !context.Request.Path.StartsWithSegments("/admin/auth")
            && context.Request.Cookies.TryGetValue(settings.SessionCookieName, out var cookie)
            && !string.IsNullOrWhiteSpace(cookie))
        {
            if (context.Request.Headers.ContainsKey(HeaderNames.Authorization))
            {
                // Header presence is not authentication: only the independently verified Bearer
                // credential may bypass the cookie CSRF boundary and carry its own identity.
                var bearer = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
                if (!bearer.Succeeded)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                context.Items[AdminOidcConstants.VerifiedBearerItemKey] = true;
                context.User = bearer.Principal!;
                await next(context);
                return;
            }

            var session = await context.AuthenticateAsync(AdminOidcConstants.SessionScheme);
            if (!session.Succeeded || session.Principal is null)
            {
                // Unknown, expired, and unprotectable sessions are indistinguishable: the bare
                // re-authentication answer, exactly as the replaced session handler challenged.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (!IsSafeMethod(context.Request.Method))
            {
                try
                {
                    await context.RequestServices.GetRequiredService<IAntiforgery>()
                        .ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException)
                {
                    // The request is not silently re-authenticated: one bare 403, and the
                    // endpoint never runs.
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }

            context.User = session.Principal!;
        }

        await next(context);
    }

    /// <summary>
    /// The RFC 9110 safe methods: GET, HEAD, OPTIONS, and TRACE. Everything else — POST, PUT,
    /// PATCH, DELETE, and any extension method — must carry the antiforgery credential.
    /// </summary>
    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method)
        || HttpMethods.IsHead(method)
        || HttpMethods.IsOptions(method)
        || HttpMethods.IsTrace(method);
}
