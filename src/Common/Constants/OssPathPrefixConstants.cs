namespace Quaestura.Common.Constants;

/// <summary>
/// OSS object path prefixes used as system-internal namespaces for object storage.
/// </summary>
/// <remarks>
/// <para>
/// These prefixes are conventions shared across services (e.g. image fingerprint
/// lazy validation cross-prefix lookup) and are NOT environment-specific config.
/// Making them configurable has caused production incidents: when the section was
/// missing or only listed "uploads/", lazy validation deleted valid fingerprints
/// for confirmed-mistake images (whose objects live under "mistakes/"), allowing
/// duplicate uploads.
/// </para>
/// <para>
/// Adding a new prefix here is a code change that must go through review and be
/// reflected in the documentation (docs/constants.md).
/// </para>
/// </remarks>
public static class OssPathPrefixConstants
{
    /// <summary>
    /// Uploads in flight — student-uploaded images before teacher review
    /// migrates them to <see cref="Mistakes"/>.
    /// </summary>
    public const string Uploads = "uploads/";

    /// <summary>
    /// Reviewed mistake images — migrated from <see cref="Uploads"/> after a
    /// teacher confirms a mistake item.
    /// </summary>
    public const string Mistakes = "mistakes/";

    /// <summary>
    /// Homework images. Reserved for future homework-service image upload;
    /// the homework service currently has no OSS integration.
    /// </summary>
    public const string Homework = "homework/";

    /// <summary>
    /// All known OSS path prefixes, ordered by typical object lifecycle
    /// (uploads/ → mistakes/ or homework/). Used by image fingerprint lazy
    /// validation to cross-check object existence across prefixes when the
    /// recorded oss_path no longer exists.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Uploads, Mistakes, Homework
    };
}
