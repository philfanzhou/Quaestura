using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Quaestura.Common.Oss;

/// <summary>
/// Shared logic for deriving, generating, and deleting thumbnail paths.
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
    /// Generates a thumbnail and uploads it to OSS
    /// </summary>
    public static async Task<string> GenerateThumbnailAsync(
        Stream originalStream,
        string objectPath,
        string size,
        IOssService ossService)
    {
        if (!SizeMap.TryGetValue(size, out var maxPixel))
            maxPixel = 800;

        originalStream.Position = 0;
        using var image = await Image.LoadAsync(originalStream);

        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(maxPixel, maxPixel),
            Mode = ResizeMode.Max
        }));

        using var thumbnailStream = new MemoryStream();
        await image.SaveAsJpegAsync(thumbnailStream);
        thumbnailStream.Position = 0;

        var thumbnailPath = GetThumbnailPath(objectPath, size);

        // Option A: extract bucket + folder from thumbnailPath so UploadAsync does not add the prefix twice
        // thumbnailPath = "uploads/2025/06/14/abc/image_small.jpg"
        // bucket = Uploads, folder = "2025/06/14/abc", fileName = "image_small.jpg"
        var bucket = GetBucketFromPath(thumbnailPath);
        var pathWithoutBucketPrefix = thumbnailPath;
        var slashIndex = thumbnailPath.IndexOf('/');
        if (slashIndex > 0)
            pathWithoutBucketPrefix = thumbnailPath[(slashIndex + 1)..];
        var dir = Path.GetDirectoryName(pathWithoutBucketPrefix)?.Replace("\\", "/");
        var fileName = Path.GetFileName(thumbnailPath);

        await ossService.UploadAsync(thumbnailStream, fileName, "image/jpeg", bucket, dir);

        return thumbnailPath;
    }

    /// <summary>
    /// Checks the cache → generates → returns a presigned URL
    /// </summary>
    public static async Task<(string url, int expirySeconds)> GetOrGenerateThumbnailAsync(
        string objectPath,
        string size,
        IOssService ossService,
        int expirySeconds = 3600,
        ILogger? logger = null)
    {
        var thumbnailPath = GetThumbnailPath(objectPath, size);

        // Check the cache
        if (await ossService.ObjectExistsAsync(thumbnailPath))
        {
            var cachedUrl = await ossService.GetPresignedUrlAsync(thumbnailPath, expirySeconds);
            return (cachedUrl, expirySeconds);
        }

        // Generate the thumbnail
        try
        {
            using var originalStream = await ossService.DownloadAsync(objectPath);
            await GenerateThumbnailAsync(originalStream, objectPath, size, ossService);

            var thumbnailUrl = await ossService.GetPresignedUrlAsync(thumbnailPath, expirySeconds);
            return (thumbnailUrl, expirySeconds);
        }
        catch (Exception ex)
        {
            // Thumbnail generation failed: fall back to the original image
            logger?.LogWarning(ex, "Thumbnail generation failed for {Path}, size={Size}, falling back to original", objectPath, size);
            var fallbackUrl = await ossService.GetPresignedUrlAsync(objectPath, expirySeconds);
            return (fallbackUrl, expirySeconds);
        }
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

    /// <summary>
    /// Infers the OssBucket from the path prefix
    /// </summary>
    private static OssBucket GetBucketFromPath(string path)
    {
        if (path.StartsWith("mistakes/", StringComparison.OrdinalIgnoreCase))
            return OssBucket.Mistakes;
        if (path.StartsWith("questions/", StringComparison.OrdinalIgnoreCase))
            return OssBucket.Questions;
        if (path.StartsWith("documents/", StringComparison.OrdinalIgnoreCase))
            return OssBucket.Documents;
        return OssBucket.Uploads;
    }
}
