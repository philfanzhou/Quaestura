using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Quaestura.Common.Oss;

public class LocalFileOssService : IOssService
{
    private readonly string _rootPath;

    public LocalFileOssService(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
        foreach (OssBucket bucket in Enum.GetValues(typeof(OssBucket)))
        {
            var prefix = BucketToPrefix(bucket);
            Directory.CreateDirectory(Path.Combine(_rootPath, prefix));
        }
    }

    public Task<string> UploadAsync(Stream stream, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null)
    {
        var objectPath = GenerateObjectPath(fileName, bucket, folder);
        var fullPath = GetFullPath(objectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        stream.CopyTo(fs);
        return Task.FromResult(objectPath);
    }

    public async Task<string> UploadAsync(byte[] data, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null)
    {
        using var stream = new MemoryStream(data);
        return await UploadAsync(stream, fileName, contentType, bucket, folder);
    }

    public async Task<List<string>> UploadManyAsync(IEnumerable<(Stream stream, string fileName, string contentType)> files, OssBucket bucket = OssBucket.Uploads, string? folder = null)
    {
        var paths = new List<string>();
        var index = 0;
        foreach (var (stream, fileName, contentType) in files)
        {
            var objectPath = GenerateObjectPath($"{index}_{fileName}", bucket, folder);
            var fullPath = GetFullPath(objectPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
            await stream.CopyToAsync(fs);
            paths.Add(objectPath);
            index++;
        }
        return paths;
    }

    public Task<Stream> DownloadAsync(string objectPath)
    {
        var fullPath = GetFullPath(objectPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Object not found: {objectPath}", fullPath);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return Task.FromResult<Stream>(stream);
    }

    public async Task<List<Stream>> DownloadManyAsync(IEnumerable<string> objectPaths)
    {
        var streams = new List<Stream>();
        foreach (var path in objectPaths)
            streams.Add(await DownloadAsync(path));
        return streams;
    }

    public async Task CopyObjectAsync(string sourcePath, string destPath)
    {
        var sourceFullPath = GetFullPath(sourcePath);
        var destFullPath = GetFullPath(destPath);
        if (!File.Exists(sourceFullPath))
            throw new FileNotFoundException($"Source object not found: {sourcePath}", sourceFullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destFullPath)!);
        using var sourceStream = new FileStream(sourceFullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        using var destStream = new FileStream(destFullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await sourceStream.CopyToAsync(destStream);
    }

    public Task<bool> ObjectExistsAsync(string objectPath)
    {
        var fullPath = GetFullPath(objectPath);
        return Task.FromResult(File.Exists(fullPath));
    }

    public Task<bool> DeleteAsync(string objectPath)
    {
        var fullPath = GetFullPath(objectPath);
        if (!File.Exists(fullPath))
            return Task.FromResult(false);
        File.Delete(fullPath);
        return Task.FromResult(true);
    }

    public Task<int> DeleteManyAsync(IEnumerable<string> objectPaths)
    {
        var count = 0;
        foreach (var path in objectPaths)
        {
            var fullPath = GetFullPath(path);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                count++;
            }
        }
        return Task.FromResult(count);
    }

    public Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600)
    {
        var fullPath = GetFullPath(objectPath);
        return Task.FromResult(fullPath);
    }

    public Task<List<string>> GetPresignedUrlsAsync(IEnumerable<string> objectPaths, int expirySeconds = 3600)
    {
        return Task.FromResult(objectPaths.Select(p => GetFullPath(p)).ToList());
    }

    public Task<bool> CheckConnectivityAsync()
    {
        return Task.FromResult(Directory.Exists(_rootPath));
    }

    public Task<List<OssObjectInfo>> ListObjectsAsync(string? prefix = null)
    {
        var searchDir = _rootPath;

        if (!string.IsNullOrEmpty(prefix))
        {
            var currentDir = _rootPath;
            foreach (var part in prefix.Split('/'))
            {
                var nextDir = Path.Combine(currentDir, part);
                if (Directory.Exists(nextDir))
                    currentDir = nextDir;
                else
                    return Task.FromResult(new List<OssObjectInfo>());
            }
            searchDir = currentDir;
        }

        var objects = new List<OssObjectInfo>();
        if (!Directory.Exists(searchDir))
            return Task.FromResult(objects);

        foreach (var file in Directory.EnumerateFiles(searchDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_rootPath, file).Replace('\\', '/');
            if (!string.IsNullOrEmpty(prefix) && !relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var fi = new FileInfo(file);
            objects.Add(new OssObjectInfo
            {
                ObjectPath = relativePath,
                Size = fi.Length,
                LastModified = fi.LastWriteTimeUtc,
                IsZombie = false
            });
        }

        return Task.FromResult(objects);
    }

    public Task<List<OssObjectInfo>> ListObjectsWithBucketAsync(OssBucket bucket, string? prefix = null)
    {
        var bucketPrefix = BucketToPrefix(bucket);
        var fullPrefix = string.IsNullOrWhiteSpace(prefix)
            ? bucketPrefix
            : $"{bucketPrefix}/{prefix}";
        return ListObjectsAsync(fullPrefix);
    }

    private string GetFullPath(string objectPath)
    {
        var normalized = objectPath.Replace('\\', '/').TrimStart('/');
        return Path.GetFullPath(Path.Combine(_rootPath, normalized));
    }

    private static string BucketToPrefix(OssBucket bucket) => bucket switch
    {
        OssBucket.Uploads => "uploads",
        OssBucket.Mistakes => "mistakes",
        OssBucket.Questions => "questions",
        OssBucket.Documents => "documents",
        _ => "others"
    };

    private static string GenerateObjectPath(string fileName, OssBucket bucket, string? folder = null)
    {
        var prefix = BucketToPrefix(bucket);
        if (!string.IsNullOrWhiteSpace(folder))
            return $"{prefix}/{folder}/{fileName}";
        var now = DateTime.UtcNow;
        return $"{prefix}/{now.Year}/{now.Month:D2}/{now.Day:D2}/{Guid.NewGuid():N}/{fileName}";
    }
}
