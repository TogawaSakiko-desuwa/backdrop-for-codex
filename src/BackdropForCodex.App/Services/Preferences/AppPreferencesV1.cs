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
/// Versioned UI-only preferences. Wallpaper settings intentionally remain in SettingsV1.
/// </summary>
public sealed record AppPreferencesV1
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public ThemeMode ThemeMode { get; init; } = ThemeMode.System;

    public bool HasShownTrayTip { get; init; }

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
}

public sealed class AppPreferencesValidationException : Exception
{
    public AppPreferencesValidationException(string message)
        : base(message)
    {
    }
}
