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
/// 缩略图路径推导 + 生成 + 删除的共享逻辑。
/// 路径规则：与原图同目录，文件名格式为 {stem}_{size}.jpg
/// 此规则一旦上线不可变更，否则会产生孤儿文件。
/// </summary>
public static class ThumbnailHelper
{
    /// <summary>支持的缩略图尺寸及最大像素</summary>
    public static readonly Dictionary<string, int> SizeMap = new()
    {
        ["thumbnail"] = 200,
        ["small"] = 400,
        ["medium"] = 800,
    };

    /// <summary>
    /// 推导缩略图路径。规则：同目录 + _{size} 后缀 + .jpg 扩展名
    /// 示例：uploads/2025/06/14/abc/image.jpg + small → uploads/2025/06/14/abc/image_small.jpg
    /// </summary>
    public static string GetThumbnailPath(string objectPath, string size)
    {
        var dir = Path.GetDirectoryName(objectPath)?.Replace("\\", "/") ?? "";
        var stem = Path.GetFileNameWithoutExtension(objectPath);
        var fileName = $"{stem}_{size}.jpg";
        return string.IsNullOrEmpty(dir) ? fileName : $"{dir}/{fileName}";
    }

    /// <summary>
    /// 返回原图对应的所有尺寸缩略图路径
    /// </summary>
    public static List<string> GetAllThumbnailPaths(string objectPath)
    {
        return SizeMap.Keys.Select(size => GetThumbnailPath(objectPath, size)).ToList();
    }

    /// <summary>
    /// 判断路径是否是缩略图路径（匹配 _{size}.jpg 模式）
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
    /// 生成缩略图并上传到 OSS
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

        // 方案A：从 thumbnailPath 中提取 bucket + folder，避免 UploadAsync 重复添加前缀
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
    /// 检查缓存→生成→返回预签名 URL
    /// </summary>
    public static async Task<(string url, int expirySeconds)> GetOrGenerateThumbnailAsync(
        string objectPath,
        string size,
        IOssService ossService,
        int expirySeconds = 3600,
        ILogger? logger = null)
    {
        var thumbnailPath = GetThumbnailPath(objectPath, size);

        // 检查缓存
        if (await ossService.ObjectExistsAsync(thumbnailPath))
        {
            var cachedUrl = await ossService.GetPresignedUrlAsync(thumbnailPath, expirySeconds);
            return (cachedUrl, expirySeconds);
        }

        // 生成缩略图
        try
        {
            using var originalStream = await ossService.DownloadAsync(objectPath);
            await GenerateThumbnailAsync(originalStream, objectPath, size, ossService);

            var thumbnailUrl = await ossService.GetPresignedUrlAsync(thumbnailPath, expirySeconds);
            return (thumbnailUrl, expirySeconds);
        }
        catch (Exception ex)
        {
            // 缩略图生成失败：降级返回原图
            logger?.LogWarning(ex, "Thumbnail generation failed for {Path}, size={Size}, falling back to original", objectPath, size);
            var fallbackUrl = await ossService.GetPresignedUrlAsync(objectPath, expirySeconds);
            return (fallbackUrl, expirySeconds);
        }
    }

    /// <summary>
    /// 删除原图 + 所有尺寸缩略图
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
    /// 根据路径前缀推断 OssBucket
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
