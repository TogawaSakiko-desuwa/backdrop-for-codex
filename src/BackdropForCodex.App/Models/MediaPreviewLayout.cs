using BackdropForCodex.Core.Settings;

namespace BackdropForCodex.App.Models;

/// <summary>
/// Calculates the rendered media bounds used by the WPF preview. Cover positioning mirrors
/// CSS object-position: the selected percentage of the media is aligned with the same
/// percentage of the viewport.
/// </summary>
public static class MediaPreviewLayout
{
    public static MediaPreviewPlacement Calculate(
        double viewportWidth,
        double viewportHeight,
        double mediaWidth,
        double mediaHeight,
        WallpaperFit fit,
        double focusX,
        double focusY)
    {
        if (!IsPositiveFinite(viewportWidth) ||
            !IsPositiveFinite(viewportHeight) ||
            !IsPositiveFinite(mediaWidth) ||
            !IsPositiveFinite(mediaHeight))
        {
            return MediaPreviewPlacement.Empty;
        }

        var normalizedFocusX = Math.Clamp(focusX, 0, 1);
        var normalizedFocusY = Math.Clamp(focusY, 0, 1);

        if (fit == WallpaperFit.Stretch)
        {
            return new MediaPreviewPlacement(
                viewportWidth,
                viewportHeight,
                OffsetX: 0,
                OffsetY: 0);
        }

        var horizontalScale = viewportWidth / mediaWidth;
        var verticalScale = viewportHeight / mediaHeight;
        var scale = fit == WallpaperFit.Contain
            ? Math.Min(horizontalScale, verticalScale)
            : Math.Max(horizontalScale, verticalScale);
        var renderedWidth = mediaWidth * scale;
        var renderedHeight = mediaHeight * scale;

        if (fit == WallpaperFit.Contain)
        {
            return new MediaPreviewPlacement(
                renderedWidth,
                renderedHeight,
                (viewportWidth - renderedWidth) / 2,
                (viewportHeight - renderedHeight) / 2);
        }

        return new MediaPreviewPlacement(
            renderedWidth,
            renderedHeight,
            (viewportWidth - renderedWidth) * normalizedFocusX,
            (viewportHeight - renderedHeight) * normalizedFocusY);
    }

    private static bool IsPositiveFinite(double value) =>
        double.IsFinite(value) && value > 0;
}

public readonly record struct MediaPreviewPlacement(
    double Width,
    double Height,
    double OffsetX,
    double OffsetY)
{
    public static MediaPreviewPlacement Empty { get; } = new(0, 0, 0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;
}
