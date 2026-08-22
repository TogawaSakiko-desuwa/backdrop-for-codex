using BackdropForCodex.Core.Media;

namespace BackdropForCodex.App.Services.Media;

/// <summary>
/// The application-wide wallpaper source composition. Runtime, workspace, and preview consumers
/// receive this same immutable registry snapshot.
/// </summary>
public static class AppWallpaperSources
{
    public static AppWallpaperEngineInstallationLocator WallpaperEngineLocator { get; } =
        new();

    public static IWallpaperSourceProviderRegistry Registry { get; } =
        new WallpaperSourceProviderRegistry(
            [
                new LocalFileWallpaperSourceProvider(),
                new WallpaperEngineLocalProjectSourceProvider(WallpaperEngineLocator),
                new WallpaperEngineWorkshopProjectSourceProvider(WallpaperEngineLocator),
            ]);

    public static SafeMediaPreviewService Preview { get; } =
        new SafeMediaPreviewService(Registry);

    public static WallpaperThumbnailPreviewService Thumbnails { get; } =
        new(Registry);
}
