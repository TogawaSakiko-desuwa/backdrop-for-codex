using BackdropForCodex.App.Services.Media;
using BackdropForCodex.App.Services.Preferences;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class WallpaperEngineInstallationPreferenceCoordinatorTests
{
    [Fact]
    public async Task SelectKeepsValidatedRootInMemoryWithoutPersistingPreferences()
    {
        var root = CreateNormalizedRoot("process-only");
        var manager = new RecordingPreferenceManager(root);
        var coordinator = new WallpaperEngineInstallationPreferenceCoordinator(manager);
        var current = AppPreferencesV1.CreateDefault();

        var next = await coordinator.SelectAsync(current, root);

        Assert.Equal(current, next);
        Assert.Equal(root, manager.PreferredInstallRootPath, ignoreCase: true);
    }

    [Fact]
    public async Task SelectValidatesThenPublishesToSharedConsumers()
    {
        var root = CreateNormalizedRoot("selected");
        var events = new List<string>();
        var manager = new RecordingPreferenceManager(root, events);
        var coordinator = new WallpaperEngineInstallationPreferenceCoordinator(manager);
        var current = AppPreferencesV1.CreateDefault();

        var next = await coordinator.SelectAsync(current, Path.Combine(root, "wallpaper64.exe"));

        Assert.Equal(current, next);
        Assert.Equal(root, manager.PreferredInstallRootPath, ignoreCase: true);
        Assert.Equal(["validate", "publish"], events);
    }

    [Fact]
    public async Task ValidationFailureLeavesPublishedPreferenceUnchanged()
    {
        var previous = CreateNormalizedRoot("previous");
        var manager = new RecordingPreferenceManager(previous)
        {
            ValidationFailure = new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.PreferredInstallationInvalid),
        };
        manager.SetPreferredInstallRootPath(previous);
        manager.Events.Clear();
        var coordinator = new WallpaperEngineInstallationPreferenceCoordinator(manager);
        var current = AppPreferencesV1.CreateDefault();

        await Assert.ThrowsAsync<WallpaperEngineUnavailableException>(
            () => coordinator.SelectAsync(current, CreateNormalizedRoot("invalid")));

        Assert.Equal(previous, manager.PreferredInstallRootPath, ignoreCase: true);
        Assert.Equal(["validate"], manager.Events);
    }

    [Fact]
    public async Task InvalidPreferencesCannotPublishAProcessLocalSelection()
    {
        var root = CreateNormalizedRoot("selected");
        var manager = new RecordingPreferenceManager(root);
        var coordinator = new WallpaperEngineInstallationPreferenceCoordinator(manager);
        var invalid = AppPreferencesV1.CreateDefault() with
        {
            ThemeMode = (ThemeMode)int.MaxValue,
        };

        await Assert.ThrowsAsync<AppPreferencesValidationException>(
            () => coordinator.SelectAsync(invalid, root));

        Assert.Null(manager.PreferredInstallRootPath);
        Assert.Empty(manager.Events);
    }

    [Fact]
    public void ApplyingLoadedPreferencesClearsAnyProcessLocalSelection()
    {
        var previous = CreateNormalizedRoot("previous");
        var manager = new RecordingPreferenceManager(previous);
        manager.SetPreferredInstallRootPath(previous);
        manager.Events.Clear();
        var coordinator = new WallpaperEngineInstallationPreferenceCoordinator(manager);

        coordinator.ApplyLoadedPreferences(AppPreferencesV1.CreateDefault());

        Assert.False(coordinator.HasPreferredWallpaperEngineInstallation);
        Assert.Null(manager.PreferredInstallRootPath);
        Assert.Equal(["clear"], manager.Events);
    }

    [Fact]
    public async Task ClearRestoresAutomaticDiscoveryWithoutPersistingPreferences()
    {
        var previous = CreateNormalizedRoot("previous");
        var manager = new RecordingPreferenceManager(previous);
        manager.SetPreferredInstallRootPath(previous);
        manager.Events.Clear();
        var coordinator = new WallpaperEngineInstallationPreferenceCoordinator(manager);
        var current = AppPreferencesV1.CreateDefault();

        var next = await coordinator.ClearAsync(current);

        Assert.Equal(current, next);
        Assert.Null(manager.PreferredInstallRootPath);
        Assert.Equal(["clear"], manager.Events);
    }

    private static string CreateNormalizedRoot(string name) =>
        Path.GetFullPath(
            Path.Combine(
                Path.GetTempPath(),
                "BackdropForCodex.Core.Tests",
                name,
                "steamapps",
                "common",
                "wallpaper_engine"));

    private sealed class RecordingPreferenceManager
        : IWallpaperEngineInstallationPreferenceManager
    {
        private readonly string _validatedRoot;

        public RecordingPreferenceManager(
            string validatedRoot,
            List<string>? events = null)
        {
            _validatedRoot = validatedRoot;
            Events = events ?? [];
        }

        public List<string> Events { get; }

        public Exception? ValidationFailure { get; init; }

        public string? PreferredInstallRootPath { get; private set; }

        public ValueTask<string> ValidateSelectionAsync(
            string selectedPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("validate");
            return ValidationFailure is null
                ? ValueTask.FromResult(_validatedRoot)
                : ValueTask.FromException<string>(ValidationFailure);
        }

        public void SetPreferredInstallRootPath(string preferredInstallRootPath)
        {
            PreferredInstallRootPath = preferredInstallRootPath;
            Events.Add("publish");
        }

        public void ClearPreferredInstallRootPath()
        {
            PreferredInstallRootPath = null;
            Events.Add("clear");
        }
    }
}
