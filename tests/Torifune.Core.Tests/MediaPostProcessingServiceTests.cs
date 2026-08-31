using Torifune.Core.Services.PostProcessing;

namespace Torifune.Core.Tests;

public sealed class MediaPostProcessingServiceTests
{
    [Theory]
    [InlineData(1280, 720, true)]
    [InlineData(854, 480, true)]
    [InlineData(720, 1280, true)]
    [InlineData(1920, 1080, false)]
    [InlineData(2560, 1440, false)]
    public void FHD未満だけ変換対象になる(int width, int height, bool expected)
    {
        Assert.Equal(expected, MediaPostProcessingService.ShouldUpscaleToFhd(width, height));
    }
}
