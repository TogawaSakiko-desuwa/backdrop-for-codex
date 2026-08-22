using System.Globalization;

namespace BackdropForCodex.Core.Media;

internal readonly record struct WallpaperEngineOwnedWindowName
{
    internal const int MaximumLength = 96;
    private const string Prefix = "BackdropForCodex-";

    internal WallpaperEngineOwnedWindowName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > MaximumLength ||
            !value.StartsWith(Prefix, StringComparison.Ordinal) ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException(
                "The Wallpaper Engine window name is not a valid owned identifier.",
                nameof(value));
        }

        Value = value;
    }

    internal string Value { get; }

    internal static WallpaperEngineOwnedWindowName Create(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);

        return new WallpaperEngineOwnedWindowName(
            $"{Prefix}{generation.ToString(CultureInfo.InvariantCulture)}-" +
            Guid.CreateVersion7().ToString("D", CultureInfo.InvariantCulture));
    }

    public override string ToString() =>
        $"{nameof(WallpaperEngineOwnedWindowName)} {{ Value = <redacted> }}";
}

internal static class WallpaperEngineControlCommandBuilder
{
    internal static IReadOnlyList<string> BuildOpenWindow(
        string launchPath,
        WallpaperEngineOwnedWindowName windowName,
        WallpaperEngineWindowOptions options,
        int x,
        int y)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(launchPath))
        {
            throw new ArgumentException(
                "The Wallpaper Engine launch path must be fully qualified.",
                nameof(launchPath));
        }

        return new[]
        {
            "-control",
            "openWallpaper",
            "-file",
            Path.GetFullPath(launchPath),
            "-playInWindow",
            windowName.Value,
            "-width",
            options.Width.ToString(CultureInfo.InvariantCulture),
            "-height",
            options.Height.ToString(CultureInfo.InvariantCulture),
            "-x",
            x.ToString(CultureInfo.InvariantCulture),
            "-y",
            y.ToString(CultureInfo.InvariantCulture),
            "-borderless",
        };
    }

    internal static IReadOnlyList<string> BuildQueryWindow(
        WallpaperEngineOwnedWindowName windowName) =>
        new[]
        {
            "-control",
            "getWallpaper",
            "-location",
            windowName.Value,
        };

    internal static IReadOnlyList<string> BuildCloseWindow(
        WallpaperEngineOwnedWindowName windowName) =>
        new[]
        {
            "-control",
            "closeWallpaper",
            "-location",
            windowName.Value,
        };
}
