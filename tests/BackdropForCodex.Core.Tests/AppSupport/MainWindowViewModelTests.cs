using BackdropForCodex.App.Services.Errors;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.Services.Preferences;
using BackdropForCodex.App.Services.Wallpaper;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.App.Models;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using BackdropForCodex.Core.Shortcuts;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void WallpaperCompatibility_ExposesCapabilitySourceSnapshot()
    {
        var compatibility = WallpaperCompatibilitySnapshot.NotEvaluated with
        {
            CodexVersion = new Version(26, 805, 14, 3),
        };
        var wallpaper = new FakeCapabilityWallpaperApplicationService(
            SettingsV1.CreateDefault(),
            compatibility);
        using var viewModel = CreateViewModel(
            wallpaper,
            new FakeAppPreferencesStore());

        Assert.Same(compatibility, viewModel.WallpaperCompatibility);
    }

    [Fact]
    public async Task ComposedSettingsStateOwnsConfigurationPreferencesAndRecents()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var persisted = SettingsV1.CreateDefault() with
            {
                RecentMediaPaths = [mediaPath],
            };
            var wallpaper = new FakeWallpaperApplicationService(persisted);
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);

            await viewModel.InitializeAsync();
            viewModel.SelectMedia(mediaPath);
            await viewModel.SetThemeModeAsync(ThemeMode.Dark);

            Assert.Same(viewModel.Settings.ConfigurationState, viewModel.ConfigurationState);
            Assert.Same(viewModel.Settings.Preferences, viewModel.Preferences);
            Assert.Same(viewModel.Settings.Recents, viewModel.Recents);
            Assert.Equal(mediaPath, viewModel.Settings.ConfigurationState.Draft.MediaPath);
            Assert.Equal(ThemeMode.Dark, viewModel.Settings.ThemeMode);
            Assert.Single(viewModel.Settings.Recents);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task InitializeAsyncHydratesPersistedUiStateWithoutStartingRuntime()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var missingRecentPath = Path.Combine(
                Path.GetTempPath(),
                $"{Guid.NewGuid():N}.webm");
            var settings = SettingsV1.CreateDefault() with
            {
                MediaPath = mediaPath,
                MediaKind = MediaKind.Image,
                Fit = WallpaperFit.Contain,
                FocusX = 0.25,
                FocusY = 0.75,
                PanelOpacity = 0.84,
                BlurPx = 7,
                DarkOverlay = 0.42,
                LightOverlay = 0.21,
                AcceptedCdpRisk = true,
                RecentMediaPaths = [mediaPath, missingRecentPath],
            };
            var preferences = AppPreferencesV1.CreateDefault() with
            {
                ThemeMode = ThemeMode.Dark,
                HasShownTrayTip = true,
            };
            var wallpaper = new FakeWallpaperApplicationService(settings);
            using var preferencesStore = new FakeAppPreferencesStore(preferences);
            var viewModel = CreateViewModel(wallpaper, preferencesStore);

            await viewModel.InitializeAsync();

            Assert.True(
                WallpaperConfigurationState.AreEquivalent(
                    settings,
                    viewModel.SavedDesired));
            Assert.Equal(mediaPath, viewModel.SelectedMediaPath);
            Assert.Equal(MediaKind.Image, viewModel.SelectedMediaKind);
            Assert.Equal(WallpaperFit.Contain, viewModel.Fit);
            Assert.Equal(0.25, viewModel.FocusX);
            Assert.Equal(0.75, viewModel.FocusY);
            Assert.Equal(0.84, viewModel.PanelOpacity);
            Assert.Equal(7, viewModel.BlurPx);
            Assert.Equal(0.42, viewModel.DarkOverlay);
            Assert.Equal(0.21, viewModel.LightOverlay);
            Assert.True(viewModel.AcceptedCdpRisk);
            Assert.False(viewModel.IsMediaMissing);
            Assert.False(viewModel.IsDraftDirty);
            Assert.Equal(ThemeMode.Dark, viewModel.ThemeMode);
            Assert.True(viewModel.HasShownTrayTip);
            Assert.Collection(
                viewModel.Recents,
                recent =>
                {
                    Assert.Equal(mediaPath, recent.Path);
                    Assert.Equal(MediaKind.Image, recent.Kind);
                    Assert.True(recent.Exists);
                },
                recent =>
                {
                    Assert.Equal(missingRecentPath, recent.Path);
                    Assert.Equal(MediaKind.Video, recent.Kind);
                    Assert.False(recent.Exists);
                });
            Assert.Equal(0, wallpaper.ApplyCallCount);
            Assert.False(viewModel.IsActive);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task SelectMediaMarksOnlyTheDraftDirty()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var persisted = SettingsV1.CreateDefault() with
            {
                AcceptedCdpRisk = true,
            };
            var wallpaper = new FakeWallpaperApplicationService(persisted);
            using var preferencesStore = new FakeAppPreferencesStore();
            var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();

            viewModel.SelectMedia(mediaPath);

            Assert.Equal(mediaPath, viewModel.SelectedMediaPath);
            Assert.Equal(MediaKind.Image, viewModel.SelectedMediaKind);
            Assert.False(viewModel.IsMediaMissing);
            Assert.True(viewModel.IsDraftDirty);
            Assert.True(
                WallpaperConfigurationState.AreEquivalent(
                    persisted,
                    viewModel.SavedDesired));
            Assert.Equal(0, wallpaper.SaveCallCount);
            Assert.Equal(0, wallpaper.ApplyCallCount);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task ApplyAsyncWhenRuntimeFailsAfterPersistenceReportsSavedButInactive()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var wallpaper = new FakeWallpaperApplicationService(
                SettingsV1.CreateDefault() with { AcceptedCdpRisk = true })
            {
                ApplyFailure = new IOException("Simulated runtime startup failure."),
                PersistApplyRequestBeforeFailure = true,
            };
            using var preferencesStore = new FakeAppPreferencesStore();
            var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();
            viewModel.SelectMedia(mediaPath);
            viewModel.Fit = WallpaperFit.Contain;
            viewModel.SetFocus(0.2, 0.8);
            viewModel.PanelOpacity = 0.86;
            viewModel.BlurPx = 6;
            viewModel.DarkOverlay = 0.44;
            viewModel.LightOverlay = 0.16;

            var applied = await viewModel.ApplyAsync();

            Assert.False(applied);
            Assert.Equal(1, wallpaper.ApplyCallCount);
            Assert.False(viewModel.IsActive);
            Assert.Null(viewModel.ActiveSnapshot);
            Assert.True(viewModel.IsSavedButInactive);
            Assert.Equal(mediaPath, viewModel.SavedDesired.MediaPath);
            Assert.Equal(MediaKind.Image, viewModel.SavedDesired.MediaKind);
            Assert.Equal(WallpaperFit.Contain, viewModel.SavedDesired.Fit);
            Assert.Equal(0.2, viewModel.SavedDesired.FocusX);
            Assert.Equal(0.8, viewModel.SavedDesired.FocusY);
            Assert.Equal(0.86, viewModel.SavedDesired.PanelOpacity);
            Assert.Equal(6, viewModel.SavedDesired.BlurPx);
            Assert.Equal(0.44, viewModel.SavedDesired.DarkOverlay);
            Assert.Equal(0.16, viewModel.SavedDesired.LightOverlay);
            Assert.False(viewModel.IsDraftDirty);
            Assert.Equal(UiStatusTone.Error, viewModel.StatusTone);
            Assert.Equal("Wallpaper could not be applied", viewModel.StatusTitle);
            Assert.Contains("simulated wallpaper runtime", viewModel.StatusMessage);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task AcceptRiskAsyncPersistsAcknowledgementImmediately()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV1.CreateDefault());
        using var preferencesStore = new FakeAppPreferencesStore();
        var viewModel = CreateViewModel(wallpaper, preferencesStore);

        await viewModel.AcceptRiskAsync();

        Assert.Equal(1, wallpaper.SaveCallCount);
        Assert.NotNull(wallpaper.LastSavedSettings);
        Assert.True(wallpaper.LastSavedSettings.AcceptedCdpRisk);
        Assert.True(viewModel.SavedDesired.AcceptedCdpRisk);
        Assert.True(viewModel.AcceptedCdpRisk);
        Assert.False(viewModel.IsDraftDirty);
        Assert.Equal(UiStatusTone.Success, viewModel.StatusTone);
    }

    [Fact]
    public async Task LegacyOverlaysAreClampedInTheEditorAndNormalizedOnTheNextSave()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV1.CreateDefault() with
            {
                DarkOverlay = 0.90,
                LightOverlay = 0.75,
            });
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(wallpaper, preferencesStore);

        await viewModel.InitializeAsync();

        Assert.Equal(MainWindowViewModel.MaximumOverlay, viewModel.DarkOverlay);
        Assert.Equal(MainWindowViewModel.MaximumOverlay, viewModel.LightOverlay);
        Assert.Equal(0.90, viewModel.SavedDesired.DarkOverlay);
        Assert.Equal(0.75, viewModel.SavedDesired.LightOverlay);
        Assert.True(viewModel.IsDraftDirty);

        await viewModel.AcceptRiskAsync();

        Assert.NotNull(wallpaper.LastSavedSettings);
        Assert.Equal(
            MainWindowViewModel.MaximumOverlay,
            wallpaper.LastSavedSettings.DarkOverlay);
        Assert.Equal(
            MainWindowViewModel.MaximumOverlay,
            wallpaper.LastSavedSettings.LightOverlay);
        Assert.Equal(
            MainWindowViewModel.MaximumOverlay,
            viewModel.SavedDesired.DarkOverlay);
        Assert.Equal(
            MainWindowViewModel.MaximumOverlay,
            viewModel.SavedDesired.LightOverlay);
        Assert.False(viewModel.IsDraftDirty);
    }

    [Fact]
    public async Task CropFocusEditingIsCoverOnlyAndClampsKeyboardStyleNudges()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var wallpaper = new FakeWallpaperApplicationService(
                SettingsV1.CreateDefault());
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();
            viewModel.SelectMedia(mediaPath);

            Assert.True(viewModel.CanAdjustFocus);

            viewModel.SetFocus(0.98, 0.02);
            viewModel.NudgeFocus(0.10, -0.10);

            Assert.Equal(1, viewModel.FocusX);
            Assert.Equal(0, viewModel.FocusY);

            viewModel.ResetFocus();

            Assert.Equal(0.5, viewModel.FocusX);
            Assert.Equal(0.5, viewModel.FocusY);

            viewModel.Fit = WallpaperFit.Contain;

            Assert.False(viewModel.CanAdjustFocus);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task VideoDraftDoesNotEnablePauseForAnActiveImage()
    {
        var imagePath = CreateTemporaryMediaFile(".png");
        var videoPath = CreateTemporaryMediaFile(".mp4");
        try
        {
            var wallpaper = new FakeWallpaperApplicationService(
                SettingsV1.CreateDefault() with { AcceptedCdpRisk = true });
            using var preferencesStore = new FakeAppPreferencesStore();
            var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();
            viewModel.SelectMedia(imagePath);

            Assert.True(await viewModel.ApplyAsync());

            viewModel.SelectMedia(videoPath);

            Assert.True(viewModel.IsActive);
            Assert.Equal(MediaKind.Image, viewModel.ActiveSnapshot?.MediaKind);
            Assert.False(viewModel.TogglePauseCommand.CanExecute(null));
        }
        finally
        {
            File.Delete(imagePath);
            File.Delete(videoPath);
        }
    }

    [Fact]
    public async Task DisableFailureKeepsTheRuntimeActiveWhenServiceReportsItActive()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var settings = SettingsV1.CreateDefault() with
            {
                MediaPath = mediaPath,
                MediaKind = MediaKind.Image,
                AcceptedCdpRisk = true,
            };
            var wallpaper = new FakeWallpaperApplicationService(settings)
            {
                IsActive = true,
                DisableFailure = new IOException("Simulated restore failure."),
            };
            using var preferencesStore = new FakeAppPreferencesStore();
            var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();

            await viewModel.DisableAsync();

            Assert.True(viewModel.IsActive);
            Assert.Equal(UiStatusTone.Error, viewModel.StatusTone);
            Assert.True(viewModel.DisableCommand.CanExecute(null));
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task ConcurrentPreferenceUpdatesPreserveThemeAndTrayTip()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV1.CreateDefault());
        using var preferencesStore = new FakeAppPreferencesStore
        {
            BlockFirstSave = true,
        };
        using var viewModel = CreateViewModel(wallpaper, preferencesStore);
        await viewModel.InitializeAsync();

        var themeTask = viewModel.SetThemeModeAsync(ThemeMode.Dark);
        await preferencesStore.FirstSaveEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var trayTipTask = viewModel.MarkTrayTipShownAsync();
        preferencesStore.ReleaseFirstSave.TrySetResult();

        await Task.WhenAll(themeTask, trayTipTask);

        Assert.Equal(ThemeMode.Dark, preferencesStore.Current.ThemeMode);
        Assert.True(preferencesStore.Current.HasShownTrayTip);
        Assert.Equal(ThemeMode.Dark, viewModel.ThemeMode);
        Assert.True(viewModel.HasShownTrayTip);
    }

    [Fact]
    public async Task RecoveryRequiredSettingsAreReadOnlyUntilExplicitBackupRestore()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var backupSettings = SettingsV1.CreateDefault() with
            {
                MediaPath = mediaPath,
                MediaKind = MediaKind.Image,
                AcceptedCdpRisk = true,
            };
            var wallpaper = new FakeWallpaperApplicationService(SettingsV1.CreateDefault())
            {
                LoadFailure = new SettingsRecoveryRequiredException(
                    SettingsRecoveryReason.InvalidDocument,
                    hasVersion1Backup: true),
                BackupSettings = backupSettings,
            };
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);

            await viewModel.InitializeAsync();

            Assert.True(viewModel.HasProtectedSettings);
            Assert.True(viewModel.HasVersion1Backup);
            Assert.False(viewModel.CanEdit);
            Assert.True(viewModel.CanOpenSettings);
            Assert.True(viewModel.CanRestoreVersion1Backup);
            viewModel.SelectMedia(mediaPath);
            await viewModel.AcceptRiskAsync();
            Assert.Null(viewModel.SelectedMediaPath);
            Assert.Equal(0, wallpaper.SaveCallCount);

            await viewModel.RestoreVersion1BackupAsync();

            Assert.Equal(1, wallpaper.RestoreBackupCallCount);
            Assert.False(viewModel.HasProtectedSettings);
            Assert.True(viewModel.CanEdit);
            Assert.Equal(mediaPath, viewModel.SelectedMediaPath);
            Assert.True(viewModel.AcceptedCdpRisk);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task ProjectionIncompatibleV2SettingsStayReadOnlyAndCannotBeSaved()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var wallpaper = new FakeWallpaperApplicationService(SettingsV1.CreateDefault())
            {
                LoadFailure = new SettingsProjectionException(
                    "A non-local Global selection cannot be represented."),
            };
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);

            await viewModel.InitializeAsync();

            Assert.True(viewModel.HasProtectedSettings);
            Assert.False(viewModel.CanEdit);
            Assert.False(viewModel.CanRestoreVersion1Backup);
            viewModel.SelectMedia(mediaPath);
            await viewModel.AcceptRiskAsync();
            Assert.False(await viewModel.ApplyAsync());
            Assert.Null(viewModel.SelectedMediaPath);
            Assert.Equal(0, wallpaper.SaveCallCount);
            Assert.Equal(0, wallpaper.ApplyCallCount);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task LaterReloadFailureTransitionsEditorIntoProtectedRecoveryState()
    {
        var wallpaper = new FakeWallpaperApplicationService(SettingsV1.CreateDefault());
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(wallpaper, preferencesStore);
        await viewModel.InitializeAsync();
        Assert.False(viewModel.HasProtectedSettings);

        wallpaper.LoadFailure = new FutureSettingsVersionException(
            schemaVersion: 99,
            hasVersion1Backup: true);

        await Assert.ThrowsAsync<FutureSettingsVersionException>(
            () => viewModel.Settings.LoadWallpaperSettingsAsync(CancellationToken.None));

        Assert.True(viewModel.HasProtectedSettings);
        Assert.True(viewModel.HasVersion1Backup);
        Assert.False(viewModel.CanEdit);
        Assert.True(viewModel.CanRestoreVersion1Backup);
    }

    private static MainWindowViewModel CreateViewModel(
        IWallpaperApplicationService wallpaper,
        IAppPreferencesStore preferencesStore) =>
        new(
            wallpaper,
            preferencesStore,
            new StubErrorMapper(),
            new FallbackTextProvider());

    private static string CreateTemporaryMediaFile(string extension)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"backdrop-view-model-{Guid.NewGuid():N}{extension}");
        byte[] bytes = extension.ToLowerInvariant() switch
        {
            ".png" =>
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            ],
            ".mp4" =>
            [
                0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70,
                0x69, 0x73, 0x6F, 0x6D,
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(extension),
                "The test helper only creates reviewed media fixtures."),
        };
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private class FakeWallpaperApplicationService :
        IWallpaperApplicationService,
        IWallpaperSettingsRecoveryService
    {
        private SettingsV1 _persistedSettings;

        public FakeWallpaperApplicationService(SettingsV1 persistedSettings)
        {
            _persistedSettings = persistedSettings;
        }

        public event EventHandler<WallpaperRuntimeStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public bool IsActive { get; set; }

        public bool IsPaused { get; set; }

        public Exception? ApplyFailure { get; init; }

        public Exception? DisableFailure { get; init; }

        public Exception? LoadFailure { get; set; }

        public SettingsV1? BackupSettings { get; init; }

        public bool PersistApplyRequestBeforeFailure { get; init; }

        public int SaveCallCount { get; private set; }

        public int ApplyCallCount { get; private set; }

        public int RestoreBackupCallCount { get; private set; }

        public SettingsV1? LastSavedSettings { get; private set; }

        public Task<SettingsV1> LoadSettingsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LoadFailure is not null)
            {
                return Task.FromException<SettingsV1>(LoadFailure);
            }

            return Task.FromResult(_persistedSettings);
        }

        public Task<SettingsV1> SaveSettingsAsync(
            SettingsV1 settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            settings.Validate();
            SaveCallCount++;
            LastSavedSettings = settings;
            _persistedSettings = settings;
            return Task.FromResult(settings);
        }

        public Task<WallpaperApplyResult> ApplyAsync(
            SettingsV1 settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            settings.Validate();
            ApplyCallCount++;
            if (PersistApplyRequestBeforeFailure)
            {
                _persistedSettings = settings.AddRecentMediaPath(settings.MediaPath!);
            }

            if (ApplyFailure is not null)
            {
                return Task.FromException<WallpaperApplyResult>(ApplyFailure);
            }

            IsActive = true;
            _persistedSettings = settings.AddRecentMediaPath(settings.MediaPath!);
            return Task.FromResult(
                new WallpaperApplyResult(_persistedSettings, ShortcutReady: true));
        }

        public Task SetPausedAsync(
            bool paused,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsPaused = paused;
            return Task.CompletedTask;
        }

        public Task DisableAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DisableFailure is not null)
            {
                return Task.FromException(DisableFailure);
            }

            IsActive = false;
            IsPaused = false;
            return Task.CompletedTask;
        }

        public Task<SettingsV1> RestoreVersion1BackupAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreBackupCallCount++;
            _persistedSettings = BackupSettings ?? SettingsV1.CreateDefault();
            LoadFailure = null;
            return Task.FromResult(_persistedSettings);
        }

        public Task<SettingsV1> ResetWallpaperSettingsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _persistedSettings = SettingsV1.CreateDefault();
            LoadFailure = null;
            return Task.FromResult(_persistedSettings);
        }

        public DesktopShortcutWriteResult CreateOrUpdateShortcut() =>
            throw new NotSupportedException();

        public DesktopShortcutDeleteResult DeleteOwnedShortcut() =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCapabilityWallpaperApplicationService :
        FakeWallpaperApplicationService,
        IWallpaperApplicationCapabilitySource
    {
        public FakeCapabilityWallpaperApplicationService(
            SettingsV1 persistedSettings,
            WallpaperCompatibilitySnapshot compatibility)
            : base(persistedSettings)
        {
            Compatibility = compatibility;
        }

        public event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>?
            CapabilitiesChanged
        {
            add { }
            remove { }
        }

        public CompatibilityCapabilities Capabilities => Compatibility.Capabilities;

        public WallpaperCompatibilitySnapshot Compatibility { get; }
    }

    private sealed class FakeAppPreferencesStore : IAppPreferencesStore
    {
        private AppPreferencesV1 _preferences;
        private int _saveCallCount;

        public FakeAppPreferencesStore(AppPreferencesV1? preferences = null)
        {
            _preferences = preferences ?? AppPreferencesV1.CreateDefault();
        }

        public bool BlockFirstSave { get; init; }

        public TaskCompletionSource FirstSaveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AppPreferencesV1 Current => _preferences;

        public Task<AppPreferencesV1> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_preferences);
        }

        public async Task SaveAsync(
            AppPreferencesV1 preferences,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _saveCallCount) == 1 &&
                BlockFirstSave)
            {
                FirstSaveEntered.TrySetResult();
                await ReleaseFirstSave.Task.WaitAsync(cancellationToken);
            }

            _preferences = preferences;
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _preferences = AppPreferencesV1.CreateDefault();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FallbackTextProvider : IAppTextProvider
    {
        public string GetString(string key) => key;
    }

    private sealed class StubErrorMapper : IUserFacingErrorMapper
    {
        public UserFacingError Map(
            Exception exception,
            UserFacingOperation operation = UserFacingOperation.General)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return new UserFacingError(
                UserFacingErrorCode.WallpaperApplyFailed,
                "Wallpaper could not be applied",
                "The simulated wallpaper runtime failed.",
                "Retry after resolving the runtime issue.",
                CanRetry: true);
        }
    }
}
