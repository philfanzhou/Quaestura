using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Minio;
using Minio.DataModel.Args;

namespace Ruoyu.Study.QuestionBank.Database.Oss;

public enum OssBucket
{
    Mistakes,   // 错题图片
    Questions,  // 题目图片
}

public interface IOssService
{
    /// <summary>
    /// 上传单个文件，返回存储路径
    /// </summary>
    Task<string> UploadAsync(Stream stream, string fileName, string contentType, OssBucket bucket = OssBucket.Questions);

    /// <summary>
    /// 上传单个文件，返回存储路径
    /// </summary>
    Task<string> UploadAsync(byte[] data, string fileName, string contentType, OssBucket bucket = OssBucket.Questions);

    /// <summary>
    /// 批量上传文件，返回存储路径列表
    /// </summary>
    Task<List<string>> UploadManyAsync(IEnumerable<(Stream stream, string fileName, string contentType)> files, OssBucket bucket = OssBucket.Questions);

    /// <summary>
    /// 下载文件
    /// </summary>
    Task<Stream> DownloadAsync(string objectPath);

    /// <summary>
    /// 批量下载文件
    /// </summary>
    Task<List<Stream>> DownloadManyAsync(IEnumerable<string> objectPaths);

    /// <summary>
    /// 删除单个文件
    /// </summary>
    Task<bool> DeleteAsync(string objectPath);

    /// <summary>
    /// 批量删除文件
    /// </summary>
    Task<int> DeleteManyAsync(IEnumerable<string> objectPaths);

    /// <summary>
    /// 获取预签名URL
    /// </summary>
    string GetPresignedUrl(string objectPath, int expirySeconds = 3600);

    /// <summary>
    /// 批量获取预签名URL
    /// </summary>
    List<string> GetPresignedUrls(IEnumerable<string> objectPaths, int expirySeconds = 3600);

    /// <summary>
    /// 检查 MinIO 连接是否正常
    /// </summary>
    Task<bool> CheckConnectivityAsync();
}

public class MinioOssService : IOssService
{
    private readonly IMinioClient _minioClient;
    private readonly string _bucketName;

    public MinioOssService(string endpoint, string accessKey, string secretKey, string bucketName)
    {
        _bucketName = bucketName;

        _minioClient = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .Build();
    }

    public async Task<string> UploadAsync(Stream stream, string fileName, string contentType, OssBucket bucket = OssBucket.Questions)
    {
        var objectPath = GenerateObjectPath(fileName, bucket);

        await EnsureBucketExistsAsync();

        var putObjectArgs = new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectPath)
            .WithStreamData(stream)
            .WithObjectSize(stream.Length)
            .WithContentType(contentType);

        await _minioClient.PutObjectAsync(putObjectArgs);

        return objectPath;
    }

    public async Task<string> UploadAsync(byte[] data, string fileName, string contentType, OssBucket bucket = OssBucket.Questions)
    {
        using var stream = new MemoryStream(data);
        return await UploadAsync(stream, fileName, contentType, bucket);
    }

    public async Task<List<string>> UploadManyAsync(IEnumerable<(Stream stream, string fileName, string contentType)> files, OssBucket bucket = OssBucket.Questions)
    {
        await EnsureBucketExistsAsync();

        var paths = new List<string>();
        var index = 0;

        foreach (var (stream, fileName, contentType) in files)
        {
            var objectPath = GenerateObjectPath($"{index}_{fileName}", bucket);

            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectPath)
                .WithStreamData(stream)
                .WithObjectSize(stream.Length)
                .WithContentType(contentType);

            await _minioClient.PutObjectAsync(putObjectArgs);
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

        await _minioClient.GetObjectAsync(getObjectArgs);

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

    public async Task<bool> DeleteAsync(string objectPath)
    {
        try
        {
            var removeObjectArgs = new RemoveObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectPath);

            await _minioClient.RemoveObjectAsync(removeObjectArgs);
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

    public string GetPresignedUrl(string objectPath, int expirySeconds = 3600)
    {
        var presignedGetObjectArgs = new PresignedGetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectPath)
            .WithExpiry(expirySeconds);

        return _minioClient.PresignedGetObjectAsync(presignedGetObjectArgs).Result;
    }

    public List<string> GetPresignedUrls(IEnumerable<string> objectPaths, int expirySeconds = 3600)
    {
        return objectPaths.Select(p => GetPresignedUrl(p, expirySeconds)).ToList();
    }

    public async Task<bool> CheckConnectivityAsync()
    {
        try
        {
            var beArgs = new BucketExistsArgs().WithBucket(_bucketName);
            return await _minioClient.BucketExistsAsync(beArgs);
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureBucketExistsAsync()
    {
        var beArgs = new BucketExistsArgs().WithBucket(_bucketName);
        if (!await _minioClient.BucketExistsAsync(beArgs))
        {
            var mbArgs = new MakeBucketArgs().WithBucket(_bucketName);
            await _minioClient.MakeBucketAsync(mbArgs);
        }
    }

    private static string GenerateObjectPath(string fileName, OssBucket bucket)
    {
        var now = DateTime.UtcNow;
        var prefix = bucket switch
        {
            OssBucket.Mistakes => "mistakes",
            OssBucket.Questions => "questions",
            _ => "others"
        };

        return $"{prefix}/{now.Year}/{now.Month:D2}/{now.Day:D2}/{Guid.NewGuid():N}/{fileName}";
    }
}

public class OssOptions
{
    public string Endpoint { get; set; } = "localhost:9000";
    public string AccessKey { get; set; } = "minioadmin";
    public string SecretKey { get; set; } = "minioadmin";
    public string BucketName { get; set; } = "ruoyu-study";
    public bool IsSecure { get; set; } = false;
}
