using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.Services.Preferences;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;

namespace BackdropForCodex.App.Services.Errors;

public enum UserFacingErrorCode
{
    Unexpected = 0,
    OperationCanceled,
    PreferencesReadFailed,
    PreferencesWriteFailed,
    PreferencesResetFailed,
    WallpaperSettingsReadFailed,
    WallpaperSettingsWriteFailed,
    WallpaperConfigurationInvalid,
    MediaInvalid,
    RiskAcknowledgementRequired,
    CodexAlreadyRunning,
    CodexUnavailable,
    CodexVersionUnsupported,
    EndpointAmbiguous,
    EndpointTimedOut,
    MediaLoadFailed,
    WallpaperApplyFailed,
    WallpaperRestoreFailed,
    ShortcutCreateFailed,
    ShortcutRemoveFailed,
    UnsupportedPlatform,
    AccessDenied,
}

public enum UserFacingOperation
{
    General = 0,
    LoadWallpaperSettings,
    SaveWallpaperSettings,
    ApplyWallpaper,
    RestoreWallpaper,
    CreateShortcut,
    RemoveShortcut,
}

/// <summary>
/// Safe UI content derived from a stable error code. It never includes exception messages or paths.
/// </summary>
public sealed record UserFacingError(
    UserFacingErrorCode Code,
    string Title,
    string Message,
    string Recovery,
    bool CanRetry);

public interface IUserFacingErrorMapper
{
    UserFacingError Map(
        Exception exception,
        UserFacingOperation operation = UserFacingOperation.General);
}

public sealed class UserFacingErrorMapper : IUserFacingErrorMapper
{
    private readonly IAppTextProvider _text;

    public UserFacingErrorMapper(IAppTextProvider text)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public UserFacingError Map(
        Exception exception,
        UserFacingOperation operation = UserFacingOperation.General)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var error = Unwrap(exception);
        var code = MapCode(error, operation);
        return new UserFacingError(
            code,
            _text.GetString("Error_Title"),
            _text.GetString($"Error_{code}_Message"),
            _text.GetString($"Error_{code}_Recovery"),
            CanRetry(code));
    }

    private static UserFacingErrorCode MapCode(
        Exception exception,
        UserFacingOperation operation)
    {
        if (exception is OperationCanceledException)
        {
            return UserFacingErrorCode.OperationCanceled;
        }

        if (exception is AppPreferencesStoreException preferencesException)
        {
            return preferencesException.Operation switch
            {
                AppPreferencesStoreOperation.Read =>
                    UserFacingErrorCode.PreferencesReadFailed,
                AppPreferencesStoreOperation.Write =>
                    UserFacingErrorCode.PreferencesWriteFailed,
                AppPreferencesStoreOperation.Reset =>
                    UserFacingErrorCode.PreferencesResetFailed,
                _ => UserFacingErrorCode.Unexpected,
            };
        }

        if (exception is CdpRiskNotAcceptedException)
        {
            return UserFacingErrorCode.RiskAcknowledgementRequired;
        }

        if (exception is CodexAlreadyRunningException)
        {
            return UserFacingErrorCode.CodexAlreadyRunning;
        }

        if (exception is UnsupportedCodexVersionException)
        {
            return UserFacingErrorCode.CodexVersionUnsupported;
        }

        if (exception is CodexPackageDiscoveryException)
        {
            return UserFacingErrorCode.CodexUnavailable;
        }

        if (exception is AmbiguousCdpEndpointException)
        {
            return UserFacingErrorCode.EndpointAmbiguous;
        }

        if (exception is CdpEndpointTimeoutException)
        {
            return UserFacingErrorCode.EndpointTimedOut;
        }

        if (exception is WallpaperMediaLoadException)
        {
            return UserFacingErrorCode.MediaLoadFailed;
        }

        if (exception is SettingsValidationException)
        {
            return UserFacingErrorCode.WallpaperConfigurationInvalid;
        }

        if (exception is MediaValidationException)
        {
            return UserFacingErrorCode.MediaInvalid;
        }

        if (exception is WallpaperInjectionException or LoopbackMediaServerException)
        {
            return UserFacingErrorCode.WallpaperApplyFailed;
        }

        if (exception is WallpaperNotActiveException)
        {
            return UserFacingErrorCode.WallpaperRestoreFailed;
        }

        if (exception is SettingsStoreException)
        {
            return operation == UserFacingOperation.LoadWallpaperSettings
                ? UserFacingErrorCode.WallpaperSettingsReadFailed
                : UserFacingErrorCode.WallpaperSettingsWriteFailed;
        }

        if (exception is PlatformNotSupportedException)
        {
            return UserFacingErrorCode.UnsupportedPlatform;
        }

        var contextualCode = operation switch
        {
            UserFacingOperation.LoadWallpaperSettings =>
                UserFacingErrorCode.WallpaperSettingsReadFailed,
            UserFacingOperation.SaveWallpaperSettings =>
                UserFacingErrorCode.WallpaperSettingsWriteFailed,
            UserFacingOperation.ApplyWallpaper =>
                UserFacingErrorCode.WallpaperApplyFailed,
            UserFacingOperation.RestoreWallpaper =>
                UserFacingErrorCode.WallpaperRestoreFailed,
            UserFacingOperation.CreateShortcut =>
                UserFacingErrorCode.ShortcutCreateFailed,
            UserFacingOperation.RemoveShortcut =>
                UserFacingErrorCode.ShortcutRemoveFailed,
            _ => UserFacingErrorCode.Unexpected,
        };
        if (contextualCode != UserFacingErrorCode.Unexpected)
        {
            return contextualCode;
        }

        if (exception is UnauthorizedAccessException)
        {
            return UserFacingErrorCode.AccessDenied;
        }

        return UserFacingErrorCode.Unexpected;
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: > 0 } aggregate)
        {
            exception = aggregate.InnerExceptions[0];
        }

        return exception;
    }

    private static bool CanRetry(UserFacingErrorCode code) => code switch
    {
        UserFacingErrorCode.OperationCanceled => true,
        UserFacingErrorCode.PreferencesReadFailed => true,
        UserFacingErrorCode.PreferencesWriteFailed => true,
        UserFacingErrorCode.PreferencesResetFailed => true,
        UserFacingErrorCode.WallpaperSettingsReadFailed => true,
        UserFacingErrorCode.WallpaperSettingsWriteFailed => true,
        UserFacingErrorCode.WallpaperConfigurationInvalid => true,
        UserFacingErrorCode.MediaInvalid => true,
        UserFacingErrorCode.RiskAcknowledgementRequired => true,
        UserFacingErrorCode.CodexAlreadyRunning => true,
        UserFacingErrorCode.CodexUnavailable => true,
        UserFacingErrorCode.CodexVersionUnsupported => false,
        UserFacingErrorCode.EndpointAmbiguous => true,
        UserFacingErrorCode.EndpointTimedOut => true,
        UserFacingErrorCode.MediaLoadFailed => true,
        UserFacingErrorCode.WallpaperApplyFailed => true,
        UserFacingErrorCode.WallpaperRestoreFailed => true,
        UserFacingErrorCode.ShortcutCreateFailed => true,
        UserFacingErrorCode.ShortcutRemoveFailed => true,
        UserFacingErrorCode.UnsupportedPlatform => false,
        UserFacingErrorCode.AccessDenied => true,
        _ => true,
    };
}
