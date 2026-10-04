using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Quaestura.Common.Oss;

/// <summary>
/// Shared logic for deriving and deleting historical thumbnail paths.
/// Path rule: same directory as the original image, file name format {stem}_{size}.jpg
/// This rule must not change once in production, otherwise orphaned files are produced.
/// </summary>
public static class ThumbnailHelper
{
    /// <summary>Supported thumbnail sizes and their maximum pixel dimensions</summary>
    public static readonly Dictionary<string, int> SizeMap = new()
    {
        ["thumbnail"] = 200,
        ["small"] = 400,
        ["medium"] = 800,
    };

    /// <summary>
    /// Derives a thumbnail path. Rule: same directory + _{size} suffix + .jpg extension
    /// Example: uploads/2025/06/14/abc/image.jpg + small → uploads/2025/06/14/abc/image_small.jpg
    /// </summary>
    public static string GetThumbnailPath(string objectPath, string size)
    {
        var dir = Path.GetDirectoryName(objectPath)?.Replace("\\", "/") ?? "";
        var stem = Path.GetFileNameWithoutExtension(objectPath);
        var fileName = $"{stem}_{size}.jpg";
        return string.IsNullOrEmpty(dir) ? fileName : $"{dir}/{fileName}";
    }

    /// <summary>
    /// Returns the thumbnail paths of all sizes for an original image
    /// </summary>
    public static List<string> GetAllThumbnailPaths(string objectPath)
    {
        return SizeMap.Keys.Select(size => GetThumbnailPath(objectPath, size)).ToList();
    }

    /// <summary>
    /// Determines whether a path is a thumbnail path (matches the _{size}.jpg pattern)
    /// </summary>
    public static bool IsThumbnailPath(string objectPath)
    {
        if (!objectPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            return false;

        var fileName = Path.GetFileNameWithoutExtension(objectPath);
        foreach (var size in SizeMap.Keys)
        {
            if (fileName.EndsWith($"_{size}", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Deletes the original image + thumbnails of all sizes
    /// </summary>
    public static async Task DeleteWithThumbnailsAsync(string objectPath, IOssService ossService)
    {
        var thumbnailPaths = GetAllThumbnailPaths(objectPath);
        foreach (var thumbPath in thumbnailPaths)
        {
            await ossService.DeleteAsync(thumbPath);
        }
        await ossService.DeleteAsync(objectPath);
    }

}
