using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Http;

namespace Ruoyu.Study.QuestionBank.Service.Validation;

/// <summary>
/// Image upload validation: file size limit + magic number check.
/// Throws <see cref="InvalidOperationException"/> with English message; the global
/// <c>ExceptionHandlingMiddleware</c> maps it to HTTP 400 + errorCode
/// <c>QUESTIONBANK_VALIDATION_INVALID_IMAGE</c>.
/// </summary>
public static class ImageValidationHelper
{
    private const int MaxFileSizeBytes = 10 * 1024 * 1024;

    private static readonly Dictionary<byte[], string> ImageMagicNumbers = new()
    {
        { new byte[] { 0xFF, 0xD8, 0xFF }, "JPEG" },
        { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "PNG" },
        { new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }, "GIF" },
        { new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "GIF" },
        { new byte[] { 0x52, 0x49, 0x46, 0x46 }, "WebP" },
        { new byte[] { 0x42, 0x4D }, "BMP" },
    };

    /// <summary>
    /// Validate a single image's binary content.
    /// </summary>
    public static void ValidateImage(byte[] imageData)
    {
        if (imageData == null || imageData.Length == 0)
        {
            throw new InvalidOperationException("Image data is empty");
        }

        if (imageData.Length > MaxFileSizeBytes)
        {
            throw new InvalidOperationException(
                $"Image size exceeds {MaxFileSizeBytes / 1024 / 1024}MB");
        }

        if (!IsValidImageMagicNumber(imageData))
        {
            throw new InvalidOperationException(
                "Image format not supported, only JPEG, PNG, GIF, WebP, BMP are allowed");
        }
    }

    /// <summary>
    /// Validate a single uploaded file. Reads bytes from the IFormFile stream.
    /// </summary>
    public static byte[] ReadAndValidate(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            throw new InvalidOperationException("Image data is empty");
        }

        if (file.Length > MaxFileSizeBytes)
        {
            throw new InvalidOperationException(
                $"Image size exceeds {MaxFileSizeBytes / 1024 / 1024}MB");
        }

        using var ms = new MemoryStream();
        file.CopyTo(ms);
        var bytes = ms.ToArray();

        if (!IsValidImageMagicNumber(bytes))
        {
            throw new InvalidOperationException(
                "Image format not supported, only JPEG, PNG, GIF, WebP, BMP are allowed");
        }

        return bytes;
    }

    /// <summary>
    /// Validate a batch of image byte arrays.
    /// </summary>
    public static void ValidateImages(IList<byte[]> images)
    {
        if (images == null || images.Count == 0) return;

        foreach (var image in images)
        {
            ValidateImage(image);
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
