using System;
using System.Collections.Generic;
using System.IO;
using Grpc.Core;

namespace Ruoyu.Study.QuestionBank.Service.Validation;

/// <summary>
/// 图片上传安全校验工具：文件大小上限 + Magic Number 校验
/// </summary>
public static class ImageValidationHelper
{
    // 默认单文件大小上限：10MB
    private const int MaxFileSizeBytes = 10 * 1024 * 1024;

    // 支持的图片 Magic Number（按字节偏移量）
    private static readonly Dictionary<byte[], string> ImageMagicNumbers = new()
    {
        // JPEG: FF D8 FF
        { new byte[] { 0xFF, 0xD8, 0xFF }, "JPEG" },
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "PNG" },
        // GIF87a: 47 49 46 38 37 61
        { new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }, "GIF" },
        // GIF89a: 47 49 46 38 39 61
        { new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "GIF" },
        // WebP: 52 49 46 46 ... 57 45 42 50 (RIFF....WEBP)
        { new byte[] { 0x52, 0x49, 0x46, 0x46 }, "WebP" },
        // BMP: 42 4D (BM)
        { new byte[] { 0x42, 0x4D }, "BMP" },
    };

    /// <summary>
    /// 校验字节数组是否满足文件大小和图片格式要求
    /// </summary>
    public static void ValidateImage(byte[] imageData)
    {
        if (imageData == null || imageData.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "图片数据为空"));
        }

        if (imageData.Length > MaxFileSizeBytes)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"单张图片大小不能超过 {MaxFileSizeBytes / 1024 / 1024}MB"));
        }

        if (!IsValidImageMagicNumber(imageData))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "图片格式不被支持，仅允许 JPEG、PNG、GIF、WebP、BMP"));
        }
    }

    /// <summary>
    /// 批量校验图片列表
    /// </summary>
    public static void ValidateImages(IList<Google.Protobuf.ByteString> images)
    {
        if (images == null || images.Count == 0) return;

        foreach (var image in images)
        {
            ValidateImage(image.ToByteArray());
        }
    }

    private static bool IsValidImageMagicNumber(byte[] data)
    {
        foreach (var magic in ImageMagicNumbers)
        {
            if (MatchesMagicNumber(data, magic.Key)) return true;
        }
        return false;
    }

    private static bool MatchesMagicNumber(byte[] data, byte[] magic)
    {
        if (data.Length < magic.Length) return false;
        for (int i = 0; i < magic.Length; i++)
        {
            if (data[i] != magic[i]) return false;
        }
        return true;
    }
}
