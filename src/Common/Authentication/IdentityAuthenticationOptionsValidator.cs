using Microsoft.Extensions.Options;

namespace Quaestura.Common.Authentication;

public sealed class IdentityAuthenticationOptionsValidator
    : IValidateOptions<IdentityAuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, IdentityAuthenticationOptions options)
    {
        var errors = new List<string>();

        if (!TryGetAbsoluteUri(options.Authority, out var authority))
        {
            errors.Add("IdentityService:Authority must be a non-empty absolute URI.");
        }

        if (!TryGetAbsoluteHttpUri(options.Issuer, out var issuer))
        {
            errors.Add("IdentityService:Issuer must be a non-empty absolute HTTP or HTTPS URI.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            errors.Add("IdentityService:Audience is required.");
        }

        if (options.ClockSkewSeconds is < 0 or > 300)
        {
            errors.Add("IdentityService:ClockSkewSeconds must be between 0 and 300.");
        }

        if (authority is not null
            && !string.Equals(authority.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(authority.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("IdentityService:Authority must use HTTP or HTTPS.");
        }

        if ((authority is not null
                && string.Equals(authority.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
             || issuer is not null
                && string.Equals(issuer.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            && options.RequireHttpsMetadata)
        {
            errors.Add(
                "IdentityService:RequireHttpsMetadata must be false when Authority or Issuer explicitly uses HTTP.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static bool TryGetAbsoluteUri(string? value, out Uri? uri)
    {
        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out uri);
    }

    private static bool TryGetAbsoluteHttpUri(string? value, out Uri? uri)
    {
        if (!TryGetAbsoluteUri(value, out uri))
        {
            return false;
        }

        return string.Equals(uri!.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}
