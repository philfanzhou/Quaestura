using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Quaestura.Host.Authentication;

/// <summary>
/// Bound and validated configuration of the admin hosted-login slice, read from the
/// <c>AdminOidc</c> section. The availability precheck runs before any protocol component is
/// registered: without the required keys the slice is never wired and every hosted-login route
/// answers the fixed 503 result. A complete but illegal value fails startup with a diagnostic
/// that names the key, never the value.
/// </summary>
public sealed record AdminOidcSettings
{
    public const string SectionName = "AdminOidc";

    public string Authority { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string RedirectUri { get; init; } = string.Empty;
    public string PostLogoutRedirectUri { get; init; } = string.Empty;

    /// <summary>The fixed local path the sign-out return lands the browser on. Must be a local
    /// absolute path; the historical default is the application root.</summary>
    public string PostLogoutReturnPath { get; init; } = "/";

    /// <summary>The requested scopes; always includes <c>openid</c>.</summary>
    public string Scope { get; init; } = "openid profile";

    public string SessionCookieName { get; init; } = AdminOidcConstants.SessionCookie;
    public string AntiforgeryHeaderName { get; init; } = AdminOidcConstants.CsrfHeader;

    /// <summary>The maximum number of concurrent server-side session tickets the in-process store
    /// holds. At capacity the least-recently-used session is evicted (it answers the fixed
    /// re-authentication result) instead of refusing a new administrator.</summary>
    public int TicketCapacity { get; init; } = 10_000;

    public IReadOnlyList<string> MissingKeys { get; init; } = [];

    internal bool Available => MissingKeys.Count == 0;

    /// <summary>Whether the configured callback URI is served over plain <c>http</c>: the cookie
    /// profile follows that scheme (a deployment decision made in front of the application), so
    /// under the plain-HTTP profile no carrier cookie carries the Secure attribute a browser
    /// would refuse.</summary>
    internal bool PlainHttpProfile =>
        Uri.TryCreate(RedirectUri, UriKind.Absolute, out var redirect)
        && redirect.Scheme == Uri.UriSchemeHttp;

    internal static AdminOidcSettings Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var missing = new[] { "Authority", "ClientId", "ClientSecret", "RedirectUri" }
            .Where(key => string.IsNullOrWhiteSpace(section[key]))
            .Select(key => SectionName + ":" + key)
            .ToArray();

        // Missing required fields take precedence over malformed optional values: the degraded
        // 503 mode never inspects anything else, exactly like the replaced client's split.
        if (missing.Length > 0)
        {
            return new AdminOidcSettings
            {
                Authority = section["Authority"] ?? string.Empty,
                ClientId = section["ClientId"] ?? string.Empty,
                ClientSecret = section["ClientSecret"] ?? string.Empty,
                RedirectUri = section["RedirectUri"] ?? string.Empty,
                PostLogoutRedirectUri = section["PostLogoutRedirectUri"] ?? string.Empty,
                PostLogoutReturnPath = section["PostLogoutReturnPath"] ?? "/",
                Scope = section["Scope"] ?? "openid profile",
                SessionCookieName = section["SessionCookieName"] ?? AdminOidcConstants.SessionCookie,
                AntiforgeryHeaderName = section["AntiforgeryHeaderName"] ?? AdminOidcConstants.CsrfHeader,
                MissingKeys = missing
            };
        }

        var capacity = 10_000;
        if (section["TicketCapacity"] is { } rawCapacity)
        {
            if (!int.TryParse(rawCapacity, out capacity) || capacity <= 0)
                throw new InvalidOperationException(
                    SectionName + ":TicketCapacity must be a positive integer.");
        }

        var scope = section["Scope"] ?? "openid profile";
        if (string.IsNullOrWhiteSpace(scope)
            || !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                SectionName + ":Scope is invalid; it must be a non-empty scope list that includes openid.");
        }

        var postLogoutReturnPath = section["PostLogoutReturnPath"] ?? "/";
        if (AsLocalPath(postLogoutReturnPath) is null)
        {
            throw new InvalidOperationException(
                SectionName + ":PostLogoutReturnPath must be a local absolute path that starts with exactly one slash.");
        }

        var settings = new AdminOidcSettings
        {
            Authority = section["Authority"] ?? string.Empty,
            ClientId = section["ClientId"] ?? string.Empty,
            ClientSecret = section["ClientSecret"] ?? string.Empty,
            RedirectUri = section["RedirectUri"] ?? string.Empty,
            PostLogoutRedirectUri = section["PostLogoutRedirectUri"] ?? string.Empty,
            PostLogoutReturnPath = postLogoutReturnPath,
            Scope = scope,
            SessionCookieName = section["SessionCookieName"] ?? AdminOidcConstants.SessionCookie,
            AntiforgeryHeaderName = section["AntiforgeryHeaderName"] ?? AdminOidcConstants.CsrfHeader,
            TicketCapacity = capacity
        };

        if (!IsAcceptableAuthority(settings.Authority))
        {
            throw new InvalidOperationException(
                SectionName + ":Authority must be an absolute http or https URI without a path, query, fragment, or user info.");
        }

        if (!IsAcceptableRedirectUri(settings.RedirectUri)
            || new Uri(settings.RedirectUri).AbsolutePath.TrimEnd('/') != AdminOidcConstants.CallbackPath)
        {
            throw new InvalidOperationException(
                SectionName + ":RedirectUri must be an absolute http or https URI whose path is exactly "
                + AdminOidcConstants.CallbackPath + ".");
        }

        if (!string.IsNullOrWhiteSpace(settings.PostLogoutRedirectUri))
        {
            if (!IsAcceptableRedirectUri(settings.PostLogoutRedirectUri)
                || new Uri(settings.PostLogoutRedirectUri).AbsolutePath.TrimEnd('/') != AdminOidcConstants.LogoutReturnPath)
            {
                throw new InvalidOperationException(
                    SectionName + ":PostLogoutRedirectUri must be an absolute http or https URI whose path is exactly "
                    + AdminOidcConstants.LogoutReturnPath + ".");
            }
        }

        if (string.IsNullOrWhiteSpace(settings.SessionCookieName)
            || (settings.PlainHttpProfile && (settings.SessionCookieName.StartsWith("__Host-", StringComparison.OrdinalIgnoreCase)
                || settings.SessionCookieName.StartsWith("__Secure-", StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException(
                SectionName + ":SessionCookieName is invalid; a name carrying the __Host- or __Secure- prefix cannot be "
                + "combined with an http " + SectionName + ":RedirectUri.");
        }

        if (string.IsNullOrWhiteSpace(settings.AntiforgeryHeaderName))
        {
            throw new InvalidOperationException(SectionName + ":AntiforgeryHeaderName is required.");
        }

        return settings;
    }

    /// <summary>
    /// Validates the <c>returnUrl</c> of the sign-in entry — byte for byte the replaced slice's
    /// two-layer rule: a single value that is a local absolute path (exactly one leading slash,
    /// no protocol-relative or backslash form) which, resolved against this site, never lands
    /// back inside the hosted-auth area the flow itself owns.
    /// </summary>
    internal static bool IsValidReturnUrl(string? value)
    {
        if (AsLocalPath(value) is null)
        {
            return false;
        }

        return !(Uri.TryCreate(new Uri("https://local.invalid"), value!, out var target)
            && target.AbsolutePath.StartsWith(AdminOidcConstants.Prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The historical local-path rule of the replaced client: a local absolute path
    /// starts with exactly one leading slash.</summary>
    internal static string? AsLocalPath(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value[0] != '/'
            || (value.Length > 1 && (value[1] == '/' || value[1] == '\\')))
        {
            return null;
        }

        return value;
    }

    private static bool IsAcceptableAuthority(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && uri.AbsolutePath is "/" or ""
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    private static bool IsAcceptableRedirectUri(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && !string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/"
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    public override string ToString() => "AdminOidcSettings";
}
