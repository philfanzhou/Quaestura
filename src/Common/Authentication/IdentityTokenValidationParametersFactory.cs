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

        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ValidateIssuer = true,
            ValidIssuers = options.GetValidIssuers(),
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
