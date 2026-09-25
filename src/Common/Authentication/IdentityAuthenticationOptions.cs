namespace Quaestura.Common.Authentication;

/// <summary>
/// Defines the identity trust contract shared by every Ruoyu JWT consumer.
/// Application credentials intentionally remain in each portal's own options.
/// </summary>
public sealed class IdentityAuthenticationOptions
{
    public const string SectionName = "IdentityService";

    public string Authority { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string[] AdditionalValidIssuers { get; set; } = [];
    public string Audience { get; set; } = string.Empty;
    public bool RequireHttpsMetadata { get; set; } = true;
    public int ClockSkewSeconds { get; set; } = 30;

    public IReadOnlyList<string> GetValidIssuers()
    {
        return new[] { Issuer }
            .Concat(AdditionalValidIssuers ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
