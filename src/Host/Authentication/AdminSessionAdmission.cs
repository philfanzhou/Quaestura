using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Quaestura.Host.Authentication;

/// <summary>
/// The pre-sign-in admission gate of the admin hosted login (the seam the replaced package
/// exposed as the <c>ITicketStore</c> admission decorator): the token-endpoint response's access
/// token is independently re-verified through the resource server's Discovery/JWKS trust before
/// any session is written. A session may only be established for a subject whose verified access
/// token carries the <c>admin</c> role, whose single <c>iss</c>/<c>sub</c> match the verified ID
/// token's, and whose expiry is still in the future; the stored principal keeps only the
/// ID-token identity and the access-token roles, all already verified, and the session's expiry
/// never outlives the access token.
/// </summary>
/// <remarks>
/// Failing admission prevents the sign-in entirely — no ticket and no cookie are written, and an
/// existing session is neither replaced nor extended. The gate runs in the OpenID Connect
/// handler's token-validated event, so a denial fails the whole remote authentication before the
/// sign-in step.
/// </remarks>
internal sealed class AdminSessionAdmission(
    IOptionsMonitor<JwtBearerOptions> bearerOptions,
    AdminOidcSettings settings,
    TimeProvider clock)
{
    /// <summary>
    /// Admits one verified sign-in: re-verifies <paramref name="accessToken"/> against the
    /// authority's published keys with the confidential client's audience, correlates it with
    /// the verified ID token, and returns the stored principal plus the session's expiry bound.
    /// Returns <see langword="null"/> when the sign-in must be denied.
    /// </summary>
    internal async Task<AdmittedSession?> AdmitAsync(
        System.IdentityModel.Tokens.Jwt.JwtSecurityToken idToken,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idToken);
        cancellationToken.ThrowIfCancellationRequested();
        TokenValidationResult validation;
        try
        {
            // Reuse the resource server's Discovery/JWKS manager, but retain its Bearer audience.
            // The confidential client's access token has a separate, explicit ClientId audience.
            var bearer = bearerOptions.Get(JwtBearerDefaults.AuthenticationScheme);
            var configuration = bearer.Configuration
                ?? await bearer.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
            var parameters = new TokenValidationParameters
            {
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = configuration.SigningKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateIssuer = true,
                ValidIssuer = settings.Authority.TrimEnd('/'),
                ValidateAudience = true,
                ValidAudience = settings.ClientId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero,
                RoleClaimType = "role"
            };
            validation = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken, parameters);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
        cancellationToken.ThrowIfCancellationRequested();

        if (!validation.IsValid || validation.ClaimsIdentity is not { } identity
            || validation.SecurityToken is not JsonWebToken token)
        {
            return null;
        }

        var access = new ClaimsPrincipal(identity);
        var subject = SingleNonempty(access, "sub");
        var issuer = SingleNonempty(access, "iss");
        var idSubject = SingleNonempty(idToken.Subject) ? idToken.Subject : null;
        var idIssuer = SingleNonempty(idToken.Issuer) ? idToken.Issuer : null;
        if (subject is null || issuer is null
            || subject != idSubject
            || issuer != idIssuer
            || !access.IsInRole("admin"))
        {
            return null;
        }

        var tokenExpiry = new DateTimeOffset(token.ValidTo, TimeSpan.Zero);
        if (tokenExpiry <= clock.GetUtcNow())
        {
            return null;
        }

        // Keep only the ID-token identity and the access-token roles, all already verified.
        var claims = idToken.Claims.Where(claim => claim.Type is "iss" or "sub" or "name")
            .Concat(access.FindAll("role"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims, "QuaesturaAdmin", nameType: "name", roleType: "role"));
        return new AdmittedSession(principal, tokenExpiry);
    }

    private static bool SingleNonempty(string? value) =>
        !string.IsNullOrWhiteSpace(value);

    private static string? SingleNonempty(ClaimsPrincipal principal, string type)
    {
        var claims = principal.FindAll(type).ToArray();
        return claims.Length == 1 && !string.IsNullOrWhiteSpace(claims[0].Value) ? claims[0].Value : null;
    }
}

/// <summary>The verified outcome of one admitted sign-in: the stored principal and the session's
/// expiry bound taken from the re-verified access token.</summary>
internal sealed record AdmittedSession(ClaimsPrincipal Principal, DateTimeOffset AccessTokenExpiry);
