namespace Quaestura.Host.Authentication;

/// <summary>
/// Fixed public identifiers of the admin hosted-login slice. The protocol runs on the framework's
/// own OpenID Connect handler (<see cref="Microsoft.AspNetCore.Authentication.OpenIdConnect"/>,
/// referenced as a package in .NET 10 like JwtBearer — no client package); these names stay
/// stable so the browser-facing contract (cookie and header names, route paths) is unchanged from
/// the replaced <c>SignaCore.Client.AspNetCore</c> integration.
/// </summary>
public static class AdminOidcConstants
{
    /// <summary>The hosted-login route prefix the slice's endpoints are mounted under.</summary>
    public const string Prefix = "/admin/auth/oidc";

    /// <summary>The callback path: byte-for-byte the configured <c>AdminOidc:RedirectUri</c>'s
    /// path, where the framework handler intercepts the authorization response.</summary>
    public const string CallbackPath = "/admin/auth/oidc/callback";

    /// <summary>The sign-out callback path: the configured
    /// <c>AdminOidc:PostLogoutRedirectUri</c>'s path, where the framework handler intercepts the
    /// RP-initiated logout return.</summary>
    public const string LogoutReturnPath = "/admin/auth/oidc/logout/return";

    /// <summary>The opaque session cookie. HttpOnly, SameSite=Lax, Essential, Secure under the
    /// HTTPS profile; its value is a data-protected reference to a server-side ticket.</summary>
    public const string SessionCookie = "signacore-hosted-login-session";

    /// <summary>The antiforgery request-header name the SPA echoes on every session write.</summary>
    public const string CsrfHeader = "X-SignaCore-CSRF";

    /// <summary>
    /// The cookie scheme holding the server-side session. Its opaque, data-protected cookie
    /// references a ticket in the in-process <see cref="AdminOidcTicketStore"/>. Named after the
    /// public cookie it owns.
    /// </summary>
    public const string SessionScheme = "quaesturaAdminSession";

    /// <summary>The OpenID Connect scheme driving the authorization-code flow and the standard
    /// RP-initiated logout against the configured authority.</summary>
    public const string OidcScheme = "quaesturaAdminOidc";

    /// <summary>
    /// The forwarding scheme the admin authorization policy authenticates against: it routes each
    /// request to the host's JwtBearer handler exactly when the replaced slice's selector did —
    /// a present <c>Authorization</c> header (even an empty one) or the retired password route —
    /// and to the cookie session everywhere else.
    /// </summary>
    public const string AuthenticationScheme = "quaesturaAdmin";

    /// <summary>The named <c>HttpClient</c> wired as the OpenID Connect backchannel: bounded to 30
    /// seconds, redirect-free, cookie-free, unlogged, excluded from HttpClient instrumentation,
    /// carrying the correlation propagation and the timeout normalization.</summary>
    public const string HttpClientName = "QuaesturaAdminOidc";

    /// <summary>
    /// The failure marker of the pre-sign-in admission gate: a subject whose re-verified access
    /// token does not carry the <c>admin</c> role (or whose token fails re-verification) fails
    /// token validation before any session is written.
    /// </summary>
    internal const string AdminDeniedFailure = "The signed-in subject is not an administrator.";

    /// <summary>
    /// The failure marker of an authorization response that is not a usable, single-authority
    /// response: duplicated protocol parameters, a missing RFC 9207 issuer parameter, an issuer
    /// that does not match the discovery document, or a state that does not unprotect.
    /// </summary>
    internal const string InvalidResponseFailure = "The authorization response is invalid.";

    /// <summary>The per-request marker of an independently verified Bearer credential (see
    /// <see cref="AdminOidcSessionMiddleware"/>).</summary>
    internal const string VerifiedBearerItemKey = "quaestura.oidc.verified_bearer";
}
