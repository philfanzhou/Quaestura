using System;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Ruoyu.Study.QuestionBank.Service.Validation;
using Xunit;

namespace QuestionBank.Test.Validation;

public class ImageValidationHelperTests
{
    // JPEG magic number: FF D8 FF
    private static readonly byte[] JpegMagic = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

    // PNG magic number: 89 50 4E 47 0D 0A 1A 0A
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 };

    // GIF87a magic number: 47 49 46 38 37 61
    private static readonly byte[] Gif87aMagic = { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61, 0x00, 0x00 };

    // GIF89a magic number: 47 49 46 38 39 61
    private static readonly byte[] Gif89aMagic = { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x00, 0x00 };

    // WebP (RIFF) magic number: 52 49 46 46
    private static readonly byte[] WebpMagic = { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 };

    // BMP magic number: 42 4D
    private static readonly byte[] BmpMagic = { 0x42, 0x4D, 0x00, 0x00, 0x00, 0x00 };

    // ==================== ValidateImage ====================

    [Fact]
    public void ValidateImage_WithNullData_ThrowsInvalidArgument()
    {
        var act = () => ImageValidationHelper.ValidateImage(null!);

        var ex = act.Should().Throw<RpcException>().Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("图片数据为空");
    }

    [Fact]
    public void ValidateImage_WithEmptyData_ThrowsInvalidArgument()
    {
        var act = () => ImageValidationHelper.ValidateImage(Array.Empty<byte>());

        var ex = act.Should().Throw<RpcException>().Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("图片数据为空");
    }

    [Fact]
    public void ValidateImage_WithExceedingSize_ThrowsInvalidArgument()
    {
        // 10MB + 1 byte
        var oversizedData = new byte[10 * 1024 * 1024 + 1];
        oversizedData[0] = 0xFF;
        oversizedData[1] = 0xD8;
        oversizedData[2] = 0xFF;

        var act = () => ImageValidationHelper.ValidateImage(oversizedData);

        var ex = act.Should().Throw<RpcException>().Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("不能超过 10MB");
    }

    [Fact]
    public void ValidateImage_WithExactlyMaxSize_Succeeds()
    {
        // Exactly 10MB with JPEG magic
        var maxData = new byte[10 * 1024 * 1024];
        maxData[0] = 0xFF;
        maxData[1] = 0xD8;
        maxData[2] = 0xFF;

        var act = () => ImageValidationHelper.ValidateImage(maxData);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateImage_WithJpegMagic_Succeeds()
    {
        var act = () => ImageValidationHelper.ValidateImage(JpegMagic);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateImage_WithPngMagic_Succeeds()
    {
        var act = () => ImageValidationHelper.ValidateImage(PngMagic);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateImage_WithGif87aMagic_Succeeds()
    {
        var act = () => ImageValidationHelper.ValidateImage(Gif87aMagic);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateImage_WithGif89aMagic_Succeeds()
    {
        var act = () => ImageValidationHelper.ValidateImage(Gif89aMagic);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateImage_WithWebpMagic_Succeeds()
    {
        var act = () => ImageValidationHelper.ValidateImage(WebpMagic);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateImage_WithBmpMagic_Succeeds()
    {
        var act = () => ImageValidationHelper.ValidateImage(BmpMagic);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateImage_WithUnsupportedFormat_ThrowsInvalidArgument()
    {
        var unsupportedData = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09 };

        var act = () => ImageValidationHelper.ValidateImage(unsupportedData);

        var ex = act.Should().Throw<RpcException>().Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("图片格式不被支持");
    }

    [Fact]
    public void ValidateImage_WithTooShortData_ThrowsInvalidArgument()
    {
        // Only 1 byte, shorter than any magic number
        var shortData = new byte[] { 0x42 };

        var act = () => ImageValidationHelper.ValidateImage(shortData);

        var ex = act.Should().Throw<RpcException>().Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("图片格式不被支持");
    }

    // ==================== ValidateImages ====================

    [Fact]
    public void ValidateImages_WithNullList_DoesNotThrow()
    {
        var act = () => ImageValidationHelper.ValidateImages(null!);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateImages_WithEmptyList_DoesNotThrow()
    {
        var act = () => ImageValidationHelper.ValidateImages(new System.Collections.Generic.List<ByteString>());

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateImages_WithAllValidImages_DoesNotThrow()
    {
        var images = new System.Collections.Generic.List<ByteString>
        {
            ByteString.CopyFrom(JpegMagic),
            ByteString.CopyFrom(PngMagic),
            ByteString.CopyFrom(Gif89aMagic)
        };

        var act = () => ImageValidationHelper.ValidateImages(images);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateImages_WithOneInvalidImage_ThrowsInvalidArgument()
    {
        var images = new System.Collections.Generic.List<ByteString>
        {
            ByteString.CopyFrom(JpegMagic),
            ByteString.CopyFrom(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09 })
        };

        var act = () => ImageValidationHelper.ValidateImages(images);

        var ex = act.Should().Throw<RpcException>().Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("图片格式不被支持");
    }

    [Fact]
    public void ValidateImages_WithEmptyImageInList_ThrowsInvalidArgument()
    {
        var images = new System.Collections.Generic.List<ByteString>
        {
            ByteString.CopyFrom(JpegMagic),
            ByteString.Empty
        };

        var act = () => ImageValidationHelper.ValidateImages(images);

        var ex = act.Should().Throw<RpcException>().Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("图片数据为空");
    }
}
