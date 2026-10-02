using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SignaCore.Client.AspNetCore;

namespace Quaestura.Host.Authentication;

// Admission decorates the official store, without owning any key, dictionary, lock, or cleanup.
internal sealed class AdminSessionAdmission(
    ITicketStore inner,
    IOptionsMonitor<JwtBearerOptions> bearerOptions,
    IOptionsMonitor<SignaCoreHostedLoginOptions> loginOptions,
    TimeProvider clock) : ITicketStore
{
    internal ITicketStore InnerStore => inner;

    public async Task<string?> StoreAsync(SignaCoreSessionTicket ticket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TokenValidationResult validation;
        var current = loginOptions.CurrentValue;
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
                ValidIssuer = current.Authority!.TrimEnd('/'),
                ValidateAudience = true,
                ValidAudience = current.ClientId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero,
                RoleClaimType = "role"
            };
            validation = await new JsonWebTokenHandler().ValidateTokenAsync(ticket.AccessToken, parameters);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new AdminAdmissionDeniedException(); }
        cancellationToken.ThrowIfCancellationRequested();

        if (!validation.IsValid || validation.ClaimsIdentity is not { } identity
            || validation.SecurityToken is not JsonWebToken token)
            throw new AdminAdmissionDeniedException();

        var access = new ClaimsPrincipal(identity);
        var subject = SingleNonempty(access, "sub");
        var issuer = SingleNonempty(access, "iss");
        if (subject is null || issuer is null
            || subject != SingleNonempty(ticket.Principal, "sub")
            || issuer != SingleNonempty(ticket.Principal, "iss")
            || !access.IsInRole("admin"))
            throw new AdminAdmissionDeniedException();

        var tokenExpiry = new DateTimeOffset(token.ValidTo, TimeSpan.Zero);
        if (tokenExpiry <= clock.GetUtcNow()) throw new AdminAdmissionDeniedException();

        // Keep only the ID-token identity and the access-token roles, all already verified.
        var claims = ticket.Principal.Claims.Where(claim => claim.Type is "iss" or "sub" or "name")
            .Concat(access.FindAll("role"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims, "QuaesturaAdmin", nameType: "name", roleType: "role"));
        return await inner.StoreAsync(ticket with
        {
            Principal = principal,
            ExpiresUtc = tokenExpiry < ticket.ExpiresUtc ? tokenExpiry : ticket.ExpiresUtc
        }, cancellationToken);
    }

    private static string? SingleNonempty(ClaimsPrincipal principal, string type)
    {
        var claims = principal.FindAll(type).ToArray();
        return claims.Length == 1 && !string.IsNullOrWhiteSpace(claims[0].Value) ? claims[0].Value : null;
    }

    public Task<SignaCoreSessionTicket?> RetrieveAsync(string key, CancellationToken cancellationToken) =>
        inner.RetrieveAsync(key, cancellationToken);
    public Task RemoveAsync(string key, CancellationToken cancellationToken) => inner.RemoveAsync(key, cancellationToken);
    public int RemoveExpired(CancellationToken cancellationToken) => inner.RemoveExpired(cancellationToken);
}

internal sealed class AdminAdmissionDeniedException : Exception;
