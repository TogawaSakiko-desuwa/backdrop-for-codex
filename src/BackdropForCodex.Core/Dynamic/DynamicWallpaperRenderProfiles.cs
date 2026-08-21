namespace BackdropForCodex.Core.Dynamic;

/// <summary>
/// One supported dynamic-wallpaper capture and encoding tier.
/// </summary>
public sealed record DynamicWallpaperRenderProfile
{
    public DynamicWallpaperRenderProfile(
        int width,
        int height,
        double frameRate,
        double? captureFrameRate = null)
    {
        if (width is <= 0 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height is <= 0 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (!double.IsFinite(frameRate) || frameRate is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        var resolvedCaptureFrameRate = captureFrameRate ?? frameRate;
        if (!double.IsFinite(resolvedCaptureFrameRate) ||
            resolvedCaptureFrameRate is < 1 or > 240 ||
            resolvedCaptureFrameRate < frameRate)
        {
            throw new ArgumentOutOfRangeException(nameof(captureFrameRate));
        }

        Width = width;
        Height = height;
        FrameRate = frameRate;
        CaptureFrameRate = resolvedCaptureFrameRate;
    }

    public int Width { get; }

    public int Height { get; }

    public double FrameRate { get; }

    public double CaptureFrameRate { get; }
}

/// <summary>
/// Dynamic-wallpaper quality tiers, ordered from preferred quality to the
/// low-cost compatibility tier used for long-running recovery.
/// </summary>
public static class DynamicWallpaperRenderProfiles
{
    public static DynamicWallpaperRenderProfile Primary { get; } =
        new(width: 1920, height: 1080, frameRate: 30);

    public static DynamicWallpaperRenderProfile Fallback { get; } =
        new(width: 1280, height: 720, frameRate: 15, captureFrameRate: 30);

    public static DynamicWallpaperRenderProfile Compatibility { get; } =
        new(width: 960, height: 540, frameRate: 5, captureFrameRate: 15);

    public static IReadOnlyList<DynamicWallpaperRenderProfile> InPreferenceOrder { get; } =
        Array.AsReadOnly([Primary, Fallback, Compatibility]);
}
