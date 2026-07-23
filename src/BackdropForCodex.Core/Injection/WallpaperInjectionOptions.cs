using System.Text;

namespace BackdropForCodex.Core.Injection;

public enum WallpaperMediaKind
{
    Image = 0,
    Video,
}

public enum WallpaperObjectFit
{
    Cover = 0,
    Contain,
    Fill,
}

public sealed record GlassEffectOptions
{
    public GlassEffectOptions(
        byte red = 16,
        byte green = 18,
        byte blue = 24,
        double opacity = 0.36,
        double blurPixels = 20,
        double saturation = 1.15)
    {
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), "Opacity must be between 0 and 1.");
        }

        if (!double.IsFinite(blurPixels) || blurPixels is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blurPixels),
                "Glass blur must be between 0 and 100 pixels.");
        }

        if (!double.IsFinite(saturation) || saturation is < 0.25 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(saturation),
                "Glass saturation must be between 0.25 and 3.");
        }

        Red = red;
        Green = green;
        Blue = blue;
        Opacity = opacity;
        BlurPixels = blurPixels;
        Saturation = saturation;
    }

    public byte Red { get; }

    public byte Green { get; }

    public byte Blue { get; }

    public double Opacity { get; }

    public double BlurPixels { get; }

    public double Saturation { get; }
}

public sealed record WallpaperCompositionOptions
{
    public const double MaximumOverlayOpacity = 0.60;

    public WallpaperCompositionOptions(
        double focusX = 0.5,
        double focusY = 0.5,
        double darkOverlay = 0.30,
        double lightOverlay = 0.18)
    {
        ValidateUnitInterval(focusX, nameof(focusX));
        ValidateUnitInterval(focusY, nameof(focusY));
        ValidateOverlay(darkOverlay, nameof(darkOverlay));
        ValidateOverlay(lightOverlay, nameof(lightOverlay));

        FocusX = focusX;
        FocusY = focusY;
        DarkOverlay = darkOverlay;
        LightOverlay = lightOverlay;
    }

    public double FocusX { get; }

    public double FocusY { get; }

    public double DarkOverlay { get; }

    public double LightOverlay { get; }

    private static void ValidateUnitInterval(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The composition value must be between 0 and 1.");
        }
    }

    private static void ValidateOverlay(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value is < 0 or > MaximumOverlayOpacity)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Overlay opacity must be between 0 and {MaximumOverlayOpacity}.");
        }
    }
}

public sealed record WallpaperInjectionOptions
{
    public WallpaperInjectionOptions(
        long generation,
        Uri source,
        string localMediaPath,
        long expectedContentLength,
        WallpaperMediaKind mediaKind,
        WallpaperObjectFit objectFit = WallpaperObjectFit.Cover,
        double mediaOpacity = 1,
        GlassEffectOptions? glass = null,
        WallpaperCompositionOptions? composition = null)
    {
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), "Generation must be positive.");
        }

        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsAbsoluteUri || !IsAllowedSourceScheme(source.Scheme))
        {
            throw new ArgumentException(
                "Wallpaper source must use an absolute file, http, or https URI.",
                nameof(source));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(localMediaPath);
        if (!Path.IsPathFullyQualified(localMediaPath))
        {
            throw new ArgumentException(
                "The local wallpaper media path must be fully qualified.",
                nameof(localMediaPath));
        }

        if (expectedContentLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedContentLength),
                "Expected media content length must be positive.");
        }

        if (!Enum.IsDefined(mediaKind))
        {
            throw new ArgumentOutOfRangeException(nameof(mediaKind));
        }

        if (!Enum.IsDefined(objectFit))
        {
            throw new ArgumentOutOfRangeException(nameof(objectFit));
        }

        if (!double.IsFinite(mediaOpacity) || mediaOpacity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mediaOpacity),
                "Media opacity must be between 0 and 1.");
        }

        Generation = generation;
        Source = source;
        LocalMediaPath = localMediaPath;
        ExpectedContentLength = expectedContentLength;
        MediaKind = mediaKind;
        ObjectFit = objectFit;
        MediaOpacity = mediaOpacity;
        Glass = glass ?? new GlassEffectOptions();
        Composition = composition ?? new WallpaperCompositionOptions();
    }

    public long Generation { get; }

    public Uri Source { get; }

    public string LocalMediaPath { get; }

    public long ExpectedContentLength { get; }

    public WallpaperMediaKind MediaKind { get; }

    public WallpaperObjectFit ObjectFit { get; }

    public double MediaOpacity { get; }

    public GlassEffectOptions Glass { get; }

    public WallpaperCompositionOptions Composition { get; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Generation = ");
        builder.Append(Generation);
        builder.Append(", Source = <redacted>");
        builder.Append(", LocalMediaPath = <redacted>");
        builder.Append(", ExpectedContentLength = ");
        builder.Append(ExpectedContentLength);
        builder.Append(", MediaKind = ");
        builder.Append(MediaKind);
        builder.Append(", ObjectFit = ");
        builder.Append(ObjectFit);
        builder.Append(", MediaOpacity = ");
        builder.Append(MediaOpacity);
        builder.Append(", Glass = ");
        builder.Append(Glass);
        builder.Append(", Composition = ");
        builder.Append(Composition);
        return true;
    }

    private static bool IsAllowedSourceScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
