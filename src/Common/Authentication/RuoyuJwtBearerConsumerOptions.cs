namespace Quaestura.Common.Authentication;

public sealed class RuoyuJwtBearerConsumerOptions
{
    public bool MapInboundClaims { get; set; } = true;
    public string? AccessTokenCookieName { get; set; }
    public string? NameClaimType { get; set; }
    public string? RoleClaimType { get; set; }
}
