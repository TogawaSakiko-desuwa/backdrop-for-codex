using BackdropForCodex.App.Services.Errors;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.Services.Preferences;
using BackdropForCodex.App.Services.Wallpaper;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.App.Models;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using BackdropForCodex.Core.Shortcuts;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class MainWindowViewModelTests
{
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
                PanelOpacity = 0.84,
                BlurPx = 7,
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
            Assert.Equal(0.84, viewModel.PanelOpacity);
            Assert.Equal(7, viewModel.BlurPx);
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
            viewModel.PanelOpacity = 0.86;
            viewModel.BlurPx = 6;

            var applied = await viewModel.ApplyAsync();

            Assert.False(applied);
            Assert.Equal(1, wallpaper.ApplyCallCount);
            Assert.False(viewModel.IsActive);
            Assert.Null(viewModel.ActiveSnapshot);
            Assert.True(viewModel.IsSavedButInactive);
            Assert.Equal(mediaPath, viewModel.SavedDesired.MediaPath);
            Assert.Equal(MediaKind.Image, viewModel.SavedDesired.MediaKind);
            Assert.Equal(WallpaperFit.Contain, viewModel.SavedDesired.Fit);
            Assert.Equal(0.86, viewModel.SavedDesired.PanelOpacity);
            Assert.Equal(6, viewModel.SavedDesired.BlurPx);
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
        using var stream = File.Create(path);
        return path;
    }

    private sealed class FakeWallpaperApplicationService :
        IWallpaperApplicationService
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

        public bool PersistApplyRequestBeforeFailure { get; init; }

        public int SaveCallCount { get; private set; }

        public int ApplyCallCount { get; private set; }

        public SettingsV1? LastSavedSettings { get; private set; }

        public Task<SettingsV1> LoadSettingsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        public DesktopShortcutWriteResult CreateOrUpdateShortcut() =>
            throw new NotSupportedException();

        public DesktopShortcutDeleteResult DeleteOwnedShortcut() =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
