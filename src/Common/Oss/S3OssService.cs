using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Minio;
using Minio.DataModel.Args;

namespace Quaestura.Common.Oss;

public enum OssBucket
{
    Uploads,      // uploads/
    Mistakes,     // mistakes/
    Questions,    // questions/
    Documents,    // documents/
}

public interface IOssService
{
    Task<string> UploadAsync(Stream stream, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null);
    Task<string> UploadAsync(byte[] data, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null);
    Task<List<string>> UploadManyAsync(IEnumerable<(Stream stream, string fileName, string contentType)> files, OssBucket bucket = OssBucket.Uploads, string? folder = null);
    Task<Stream> DownloadAsync(string objectPath);
    Task<List<Stream>> DownloadManyAsync(IEnumerable<string> objectPaths);
    Task CopyObjectAsync(string sourcePath, string destPath);
    Task<bool> ObjectExistsAsync(string objectPath);
    Task<bool> DeleteAsync(string objectPath);
    Task<int> DeleteManyAsync(IEnumerable<string> objectPaths);
    Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600);
    Task<List<string>> GetPresignedUrlsAsync(IEnumerable<string> objectPaths, int expirySeconds = 3600);
    Task<bool> CheckConnectivityAsync();
    Task<List<OssObjectInfo>> ListObjectsAsync(string? prefix = null);
    Task<List<OssObjectInfo>> ListObjectsWithBucketAsync(OssBucket bucket, string? prefix = null);
}

public class OssObjectInfo
{
    public string ObjectPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public bool IsZombie { get; set; }
}

public class S3OssService : IOssService
{
    private readonly IMinioClient _internalClient;
    private readonly IMinioClient _presignClient;
    private readonly string _bucketName;
    private readonly PathPrefixValidator? _prefixValidator;
    private readonly Uri? _publicBaseUri;

    public S3OssService(OssOptions options, string[]? allowedPrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _bucketName = options.BucketName;
        _prefixValidator = allowedPrefixes != null ? new PathPrefixValidator(allowedPrefixes) : null;
        _internalClient = BuildClient(
            options.InternalEndpoint,
            options.AccessKey,
            options.SecretKey,
            options.InternalSecure);

        _publicBaseUri = ParsePublicBaseUri(options.PublicBaseUrl);
        _presignClient = _publicBaseUri == null
            ? _internalClient
            : BuildClient(
                _publicBaseUri.Authority,
                options.AccessKey,
                options.SecretKey,
                string.Equals(_publicBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static IMinioClient BuildClient(string endpoint, string accessKey, string secretKey, bool secure)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("The S3 endpoint is required.", nameof(endpoint));
        }

        return new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .WithSSL(secure)
            .Build();
    }

    public async Task<string> UploadAsync(Stream stream, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null)
    {
        var objectPath = GenerateObjectPath(fileName, bucket, folder);
        _prefixValidator?.ValidateWritePath(objectPath);

        await EnsureBucketExistsAsync();

        var putObjectArgs = new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectPath)
            .WithStreamData(stream)
            .WithObjectSize(stream.Length)
            .WithContentType(contentType);

        await _internalClient.PutObjectAsync(putObjectArgs);

        return objectPath;
    }

    public async Task<string> UploadAsync(byte[] data, string fileName, string contentType, OssBucket bucket = OssBucket.Uploads, string? folder = null)
    {
        using var stream = new MemoryStream(data);
        return await UploadAsync(stream, fileName, contentType, bucket, folder);
    }

    public async Task<List<string>> UploadManyAsync(IEnumerable<(Stream stream, string fileName, string contentType)> files, OssBucket bucket = OssBucket.Uploads, string? folder = null)
    {
        await EnsureBucketExistsAsync();

        var paths = new List<string>();
        var index = 0;

        foreach (var (stream, fileName, contentType) in files)
        {
            var objectPath = GenerateObjectPath($"{index}_{fileName}", bucket, folder);
            _prefixValidator?.ValidateWritePath(objectPath);

            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectPath)
                .WithStreamData(stream)
                .WithObjectSize(stream.Length)
                .WithContentType(contentType);

            await _internalClient.PutObjectAsync(putObjectArgs);
            paths.Add(objectPath);
            index++;
        }

        return paths;
    }

    public async Task<Stream> DownloadAsync(string objectPath)
    {
        var memoryStream = new MemoryStream();

        var getObjectArgs = new GetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectPath)
            .WithCallbackStream(stream =>
            {
                stream.CopyTo(memoryStream);
                memoryStream.Position = 0;
            });

        await _internalClient.GetObjectAsync(getObjectArgs);

        return memoryStream;
    }

    public async Task<List<Stream>> DownloadManyAsync(IEnumerable<string> objectPaths)
    {
        var streams = new List<Stream>();
        foreach (var path in objectPaths)
        {
            streams.Add(await DownloadAsync(path));
        }
        return streams;
    }

    public async Task CopyObjectAsync(string sourcePath, string destPath)
    {
        _prefixValidator?.ValidateWritePath(destPath);
        using var stream = await DownloadAsync(sourcePath);
        await EnsureBucketExistsAsync();

        var putObjectArgs = new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(destPath)
            .WithStreamData(stream)
            .WithObjectSize(stream.Length)
            .WithContentType("image/jpeg");

        await _internalClient.PutObjectAsync(putObjectArgs);
    }

    public async Task<bool> ObjectExistsAsync(string objectPath)
    {
        try
        {
            var statArgs = new StatObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectPath);

            var stat = await _internalClient.StatObjectAsync(statArgs);
            return stat != null && stat.Size > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> DeleteAsync(string objectPath)
    {
        try
        {
            _prefixValidator?.ValidateWritePath(objectPath);

            // If this is not a thumbnail path, delete the associated thumbnails first
            if (!ThumbnailHelper.IsThumbnailPath(objectPath))
            {
                var thumbnailPaths = ThumbnailHelper.GetAllThumbnailPaths(objectPath);
                foreach (var thumbPath in thumbnailPaths)
                {
                    try
                    {
                        var removeThumbArgs = new RemoveObjectArgs()
                            .WithBucket(_bucketName)
                            .WithObject(thumbPath);
                        await _internalClient.RemoveObjectAsync(removeThumbArgs);
                    }
                    catch
                    {
                        // Ignore missing thumbnails
                    }
                }
            }

            var removeObjectArgs = new RemoveObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectPath);

            await _internalClient.RemoveObjectAsync(removeObjectArgs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<int> DeleteManyAsync(IEnumerable<string> objectPaths)
    {
        var count = 0;
        foreach (var path in objectPaths)
        {
            if (await DeleteAsync(path))
            {
                count++;
            }
        }
        return count;
    }

    public async Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600)
    {
        var presignedGetObjectArgs = new PresignedGetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectPath)
            .WithExpiry(expirySeconds);

        var url = await _presignClient.PresignedGetObjectAsync(presignedGetObjectArgs);

        if (_publicBaseUri != null && !string.IsNullOrEmpty(url))
        {
            url = PresignedUrlPathPrefixer.AddPrefix(url, _publicBaseUri);
        }

        return url;
    }

    public async Task<List<string>> GetPresignedUrlsAsync(IEnumerable<string> objectPaths, int expirySeconds = 3600)
    {
        var urls = new List<string>();
        foreach (var p in objectPaths)
        {
            urls.Add(await GetPresignedUrlAsync(p, expirySeconds));
        }
        return urls;
    }

    public async Task<bool> CheckConnectivityAsync()
    {
        try
        {
            var beArgs = new BucketExistsArgs().WithBucket(_bucketName);
            return await _internalClient.BucketExistsAsync(beArgs);
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<OssObjectInfo>> ListObjectsAsync(string? prefix = null)
    {
        var objects = new List<OssObjectInfo>();

        try
        {
            var listArgs = new ListObjectsArgs()
                .WithBucket(_bucketName)
                .WithPrefix(prefix)
                .WithRecursive(true);

            await foreach (var item in _internalClient.ListObjectsEnumAsync(listArgs))
            {
                objects.Add(new OssObjectInfo
                {
                    ObjectPath = item.Key,
                    Size = (long)item.Size,
                    LastModified = string.IsNullOrEmpty(item.LastModified) ? null : DateTimeOffset.TryParse(item.LastModified, out var dt) ? (DateTimeOffset?)dt : null,
                    IsZombie = false
                });
            }
        }
        catch
        {
            // Ignore errors and return an empty list; the listing is streamed, so drop partial results
            objects.Clear();
        }

        return objects;
    }

    public async Task<List<OssObjectInfo>> ListObjectsWithBucketAsync(OssBucket bucket, string? prefix = null)
    {
        var bucketPrefix = bucket switch
        {
            OssBucket.Uploads => "uploads",
            OssBucket.Mistakes => "mistakes",
            OssBucket.Questions => "questions",
            OssBucket.Documents => "documents",
            _ => "others"
        };

        var fullPrefix = string.IsNullOrWhiteSpace(prefix)
            ? bucketPrefix
            : $"{bucketPrefix}/{prefix}";

        return await ListObjectsAsync(fullPrefix);
    }

    private async Task EnsureBucketExistsAsync()
    {
        var beArgs = new BucketExistsArgs().WithBucket(_bucketName);
        if (!await _internalClient.BucketExistsAsync(beArgs))
        {
            var mbArgs = new MakeBucketArgs().WithBucket(_bucketName);
            await _internalClient.MakeBucketAsync(mbArgs);
        }
    }

    private static Uri? ParsePublicBaseUri(string? publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(publicBaseUrl.TrimEnd('/'), UriKind.Absolute, out var publicBaseUri) ||
            (publicBaseUri.Scheme != Uri.UriSchemeHttp && publicBaseUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(publicBaseUri.Query) ||
            !string.IsNullOrEmpty(publicBaseUri.Fragment) ||
            !string.IsNullOrEmpty(publicBaseUri.UserInfo))
        {
            throw new ArgumentException(
                "Oss:PublicBaseUrl must be an absolute HTTP or HTTPS URL without query, fragment, or user information.",
                nameof(publicBaseUrl));
        }

        return publicBaseUri;
    }

    private static string GenerateObjectPath(string fileName, OssBucket bucket, string? folder = null)
    {
        var prefix = bucket switch
        {
            OssBucket.Uploads => "uploads",
            OssBucket.Mistakes => "mistakes",
            OssBucket.Questions => "questions",
            OssBucket.Documents => "documents",
            _ => "others"
        };

        if (!string.IsNullOrWhiteSpace(folder))
        {
            return $"{prefix}/{folder}/{fileName}";
        }

        var now = DateTime.UtcNow;
        return $"{prefix}/{now.Year}/{now.Month:D2}/{now.Day:D2}/{Guid.NewGuid():N}/{fileName}";
    }
}

public class OssOptions
{
    public string InternalEndpoint { get; set; } = "localhost:8333";
    public bool InternalSecure { get; set; }
    public string AccessKey { get; set; } = "seaweedfs_admin";
    public string SecretKey { get; set; } = "seaweedfs_admin";
    public string BucketName { get; set; } = "ruoyu-study";
    public string? PublicBaseUrl { get; set; }
}
