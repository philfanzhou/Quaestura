using System;

namespace Quaestura.Service.Options;

/// <summary>
/// Admin portal settings. Users listed in <see cref="AdminUserIds"/> receive the
/// <c>admin</c> role through the SignaCore token callback.
/// </summary>
public sealed class AdminPortalOptions
{
    public const string SectionName = "AdminPortal";

    public string[] AdminUserIds { get; set; } = Array.Empty<string>();
}
