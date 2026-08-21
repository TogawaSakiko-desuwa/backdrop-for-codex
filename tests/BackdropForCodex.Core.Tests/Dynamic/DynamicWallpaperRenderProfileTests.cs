using BackdropForCodex.Core.Dynamic;
using Xunit;

namespace BackdropForCodex.Core.Tests.Dynamic;

public sealed class DynamicWallpaperRenderProfileTests
{
    [Fact]
    public void SupportedProfilesEndWithALowCostCompatibilityTier()
    {
        Assert.Equal(
            new DynamicWallpaperRenderProfile(width: 1920, height: 1080, frameRate: 30),
            DynamicWallpaperRenderProfiles.Primary);
        Assert.Equal(
            new DynamicWallpaperRenderProfile(
                width: 1280,
                height: 720,
                frameRate: 15,
                captureFrameRate: 30),
            DynamicWallpaperRenderProfiles.Fallback);
        Assert.Equal(30, DynamicWallpaperRenderProfiles.Primary.CaptureFrameRate);
        Assert.Equal(30, DynamicWallpaperRenderProfiles.Fallback.CaptureFrameRate);
        Assert.Equal(
            [
                DynamicWallpaperRenderProfiles.Primary,
                DynamicWallpaperRenderProfiles.Fallback,
                new DynamicWallpaperRenderProfile(
                    width: 960,
                    height: 540,
                    frameRate: 5,
                    captureFrameRate: 15),
            ],
            DynamicWallpaperRenderProfiles.InPreferenceOrder);
    }
}
