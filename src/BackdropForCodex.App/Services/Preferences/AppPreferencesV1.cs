namespace BackdropForCodex.App.Services.Preferences;

/// <summary>
/// Controls how the application follows or overrides the Windows app theme.
/// </summary>
public enum ThemeMode
{
    System = 0,
    Light,
    Dark,
}

/// <summary>
/// Versioned UI-only preferences. Wallpaper settings are owned by the SettingsV3 workspace.
/// </summary>
public sealed record AppPreferencesV1
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public ThemeMode ThemeMode { get; init; } = ThemeMode.System;

    public bool HasShownTrayTip { get; init; }

    /// <summary>
    /// Records the machine-local acknowledgement that a third-party Web wallpaper may access the
    /// network from Wallpaper Engine. Backdrop only captures pixels and does not forward scripts,
    /// pointer input, or Codex content.
    /// </summary>
    public bool HasAcknowledgedWebWallpaperPrivacyNotice { get; init; }

    public static AppPreferencesV1 CreateDefault() => new();

    public AppPreferencesV1 Snapshot()
    {
        Validate();
        return this with { };
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new AppPreferencesValidationException(
                $"SchemaVersion must be {CurrentSchemaVersion}.");
        }

        if (!Enum.IsDefined(ThemeMode))
        {
            throw new AppPreferencesValidationException("ThemeMode is not supported.");
        }
    }

    public override string ToString() =>
        $"{nameof(AppPreferencesV1)} {{ SchemaVersion = {SchemaVersion}, " +
        $"ThemeMode = {ThemeMode}, HasShownTrayTip = {HasShownTrayTip}, " +
        $"HasAcknowledgedWebWallpaperPrivacyNotice = " +
        $"{HasAcknowledgedWebWallpaperPrivacyNotice} }}";
}

public sealed class AppPreferencesValidationException : Exception
{
    public AppPreferencesValidationException(string message)
        : base(message)
    {
    }
}
