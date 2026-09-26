namespace Quaestura.Service.Options;

/// <summary>
/// Client settings Quaestura uses to call SignaCore on behalf of the admin portal.
/// Shares the <c>IdentityService</c> section with the JWT trust settings; AppId/AppSecret
/// must be injected through environment variables or Consul, never committed.
/// </summary>
public sealed class IdentityServiceClientOptions
{
    public const string SectionName = "IdentityService";
    public const string HttpClientName = "IdentityService";

    public string Authority { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
}
