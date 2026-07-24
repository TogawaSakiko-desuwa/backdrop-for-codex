namespace BackdropForCodex.Core.Injection;

/// <summary>
/// Stable page-cleanup ABI shared with earlier local builds. These identifiers must not follow
/// display branding or an older process may leave owned DOM resources behind.
/// </summary>
internal static class InjectionOwnershipContract
{
    internal const string Owner = "codex-wallpaper";
    internal const string RootElementId = "codex-wallpaper-owned-root";
    internal const string StyleElementId = "codex-wallpaper-owned-style";
    internal const string FileInputElementId = "codex-wallpaper-owned-file-input";
    internal const string StateProperty = "__codexWallpaperOwnedState_v1";
}
