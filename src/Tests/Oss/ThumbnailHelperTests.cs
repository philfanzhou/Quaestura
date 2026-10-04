using Moq;
using Quaestura.Common.Oss;
using Xunit;

namespace Quaestura.Tests.Oss;

public class ThumbnailHelperTests
{
    [Theory]
    [InlineData("thumbnail", 200)]
    [InlineData("small", 400)]
    [InlineData("medium", 800)]
    public void GetThumbnailPath_PreservesHistoricalDirectorySuffixAndSize(string size, int pixels)
    {
        Assert.Equal(pixels, ThumbnailHelper.SizeMap[size]);
        Assert.Equal($"questions/2026/10/image_{size}.jpg",
            ThumbnailHelper.GetThumbnailPath("questions/2026/10/image.png", size));
        Assert.Equal($"image_{size}.jpg", ThumbnailHelper.GetThumbnailPath("image.png", size));
    }

    [Fact]
    public void GetAllThumbnailPaths_ReturnsExactlyTheThreeHistoricalPaths()
    {
        Assert.Equal(new[]
        {
            "questions/image_thumbnail.jpg",
            "questions/image_small.jpg",
            "questions/image_medium.jpg",
        }, ThumbnailHelper.GetAllThumbnailPaths("questions/image.png"));
    }

    [Theory]
    [InlineData("questions/image_thumbnail.jpg", true)]
    [InlineData("questions/image_small.jpg", true)]
    [InlineData("questions/image_medium.jpg", true)]
    [InlineData("questions/IMAGE_THUMBNAIL.JPG", true)]
    [InlineData("questions/IMAGE_SMALL.JPG", true)]
    [InlineData("questions/IMAGE_MEDIUM.JPG", true)]
    [InlineData("questions/image.jpg", false)]
    [InlineData("questions/image_large.jpg", false)]
    [InlineData("questions/image_small.png", false)]
    [InlineData("questions/image_small.jpg.png", false)]
    public void IsThumbnailPath_RecognizesOnlyHistoricalJpegSuffixes(string path, bool expected)
    {
        Assert.Equal(expected, ThumbnailHelper.IsThumbnailPath(path));
    }

    [Fact]
    public async Task DeleteWithThumbnailsAsync_DeletesHistoricalPathsBeforeOriginal_EvenWhenDeleteReturnsFalse()
    {
        var requestedPaths = new List<string>();
        var oss = new Mock<IOssService>(MockBehavior.Strict);
        oss.Setup(service => service.DeleteAsync(It.IsAny<string>()))
            .Callback<string>(requestedPaths.Add)
            .ReturnsAsync(false);

        await ThumbnailHelper.DeleteWithThumbnailsAsync("questions/image.png", oss.Object);

        Assert.Equal(new[]
        {
            "questions/image_thumbnail.jpg",
            "questions/image_small.jpg",
            "questions/image_medium.jpg",
            "questions/image.png",
        }, requestedPaths);
        oss.Verify(service => service.DeleteAsync(It.IsAny<string>()), Times.Exactly(4));
    }

    [Fact]
    public async Task DeleteWithThumbnailsAsync_PropagatesDeletionExceptionAndStops()
    {
        var expected = new InvalidOperationException("Synthetic deletion failure.");
        var oss = new Mock<IOssService>(MockBehavior.Strict);
        oss.Setup(service => service.DeleteAsync("questions/image_thumbnail.jpg"))
            .ThrowsAsync(expected);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ThumbnailHelper.DeleteWithThumbnailsAsync("questions/image.png", oss.Object));

        Assert.Same(expected, actual);
        oss.Verify(service => service.DeleteAsync("questions/image_thumbnail.jpg"), Times.Once);
        oss.VerifyNoOtherCalls();
    }
}
