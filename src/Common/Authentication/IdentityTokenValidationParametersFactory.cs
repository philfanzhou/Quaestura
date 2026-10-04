using Microsoft.IdentityModel.Tokens;

namespace Quaestura.Common.Authentication;

public static class IdentityTokenValidationParametersFactory
{
    public static TokenValidationParameters Create(
        IdentityAuthenticationOptions options,
        IEnumerable<SecurityKey>? signingKeys = null,
        string? nameClaimType = null,
        string? roleClaimType = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var validIssuers = Array.AsReadOnly(options.GetValidIssuers().ToArray());

        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ValidateIssuer = true,
            ValidIssuers = validIssuers,
            // Discovery supplies signing keys, never an additional trust anchor.
            IssuerValidator = (issuer, _, _) => validIssuers.Contains(issuer, StringComparer.Ordinal)
                ? issuer
                : throw new SecurityTokenInvalidIssuerException(
                    "Token issuer must match IdentityService:Issuer or IdentityService:AdditionalValidIssuers."),
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds)
        };

        if (!string.IsNullOrWhiteSpace(nameClaimType))
        {
            parameters.NameClaimType = nameClaimType;
        }

        if (!string.IsNullOrWhiteSpace(roleClaimType))
        {
            parameters.RoleClaimType = roleClaimType;
        }

        return parameters;
    }
}
