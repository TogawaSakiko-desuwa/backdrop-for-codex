using BackdropForCodex.App.Services.Media;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class AppWallpaperEngineInstallationLocatorTests
{
    [Fact]
    public async Task ValidationDoesNotPublishTheSelectionUntilTheCallerCommitsIt()
    {
        using var fixture = new InstallationPathFixture();
        var automatic = fixture.CreateInstallation(fixture.AutomaticRoot);
        var selected = fixture.CreateInstallation(fixture.SelectedRoot);
        var locator = new AppWallpaperEngineInstallationLocator(
            preferred => new StubLocator(
                preferred is null
                    ? automatic
                    : selected));

        var validatedRoot = await locator.ValidateSelectionAsync(
            selected.ControlExecutablePath);

        Assert.Equal(selected.InstallRootPath, validatedRoot, ignoreCase: true);
        Assert.Null(locator.PreferredInstallRootPath);
        Assert.Equal(
            automatic.InstallRootPath,
            (await locator.LocateAsync()).InstallRootPath,
            ignoreCase: true);

        locator.SetPreferredInstallRootPath(validatedRoot);

        Assert.Equal(selected.InstallRootPath, locator.PreferredInstallRootPath, ignoreCase: true);
        Assert.Equal(
            selected.InstallRootPath,
            (await locator.LocateAsync()).InstallRootPath,
            ignoreCase: true);
        Assert.DoesNotContain(
            selected.InstallRootPath,
            locator.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedValidationDoesNotReplaceTheLastCommittedSelection()
    {
        using var fixture = new InstallationPathFixture();
        var selected = fixture.CreateInstallation(fixture.SelectedRoot);
        var locator = new AppWallpaperEngineInstallationLocator(
            preferred => preferred is not null &&
                string.Equals(preferred, selected.InstallRootPath, StringComparison.OrdinalIgnoreCase)
                    ? new StubLocator(selected)
                    : new ThrowingLocator(
                        WallpaperEngineAvailabilityReason.PreferredInstallationInvalid));
        locator.SetPreferredInstallRootPath(selected.InstallRootPath);
        var invalidExecutable = Path.Combine(fixture.InvalidRoot, "wallpaper64.exe");
        Directory.CreateDirectory(fixture.InvalidRoot);
        await File.WriteAllTextAsync(invalidExecutable, "untrusted fixture");

        var exception = await Assert.ThrowsAsync<WallpaperEngineUnavailableException>(
            () => locator.ValidateSelectionAsync(invalidExecutable).AsTask());

        Assert.Equal(
            WallpaperEngineAvailabilityReason.PreferredInstallationInvalid,
            exception.Reason);
        Assert.Equal(selected.InstallRootPath, locator.PreferredInstallRootPath, ignoreCase: true);
        Assert.Equal(
            selected.InstallRootPath,
            (await locator.LocateAsync()).InstallRootPath,
            ignoreCase: true);
    }

    [Fact]
    public async Task ClearReturnsEveryConsumerToAutomaticDiscovery()
    {
        using var fixture = new InstallationPathFixture();
        var automatic = fixture.CreateInstallation(fixture.AutomaticRoot);
        var selected = fixture.CreateInstallation(fixture.SelectedRoot);
        var locator = new AppWallpaperEngineInstallationLocator(
            preferred => new StubLocator(preferred is null ? automatic : selected));
        locator.SetPreferredInstallRootPath(selected.InstallRootPath);

        locator.ClearPreferredInstallRootPath();

        Assert.Null(locator.PreferredInstallRootPath);
        Assert.Equal(
            automatic.InstallRootPath,
            (await locator.LocateAsync()).InstallRootPath,
            ignoreCase: true);
    }

    [Theory]
    [InlineData("not-wallpaper-engine.exe")]
    [InlineData("wallpaper32.exe")]
    public async Task SelectionRejectsAnExecutableThatIsNotWallpaper64(string fileName)
    {
        using var fixture = new InstallationPathFixture();
        var locator = new AppWallpaperEngineInstallationLocator(
            _ => throw new InvalidOperationException("Validation must not run."));
        var filePath = Path.Combine(fixture.InvalidRoot, fileName);
        Directory.CreateDirectory(fixture.InvalidRoot);
        await File.WriteAllTextAsync(filePath, "fixture");

        var exception = await Assert.ThrowsAsync<WallpaperEngineUnavailableException>(
            () => locator.ValidateSelectionAsync(filePath).AsTask());

        Assert.Equal(
            WallpaperEngineAvailabilityReason.PreferredInstallationInvalid,
            exception.Reason);
        Assert.DoesNotContain(filePath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubLocator(WallpaperEngineInstallation installation)
        : IWallpaperEngineInstallationLocator
    {
        public ValueTask<WallpaperEngineInstallation> LocateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(installation);
        }
    }

    private sealed class ThrowingLocator(WallpaperEngineAvailabilityReason reason)
        : IWallpaperEngineInstallationLocator
    {
        public ValueTask<WallpaperEngineInstallation> LocateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<WallpaperEngineInstallation>(
                new WallpaperEngineUnavailableException(reason));
        }
    }

    private sealed class InstallationPathFixture : IDisposable
    {
        public InstallationPathFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "BackdropForCodex.Core.Tests",
                Guid.NewGuid().ToString("N"));
            AutomaticRoot = Path.Combine(RootPath, "automatic", "steamapps", "common", "wallpaper_engine");
            SelectedRoot = Path.Combine(RootPath, "selected", "steamapps", "common", "wallpaper_engine");
            InvalidRoot = Path.Combine(RootPath, "invalid");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string AutomaticRoot { get; }

        public string SelectedRoot { get; }

        public string InvalidRoot { get; }

        public WallpaperEngineInstallation CreateInstallation(string installRoot)
        {
            ObjectDisposedException.ThrowIf(!Directory.Exists(RootPath), this);
            Directory.CreateDirectory(installRoot);
            var executable = Path.Combine(installRoot, "wallpaper64.exe");
            File.WriteAllText(executable, "fixture");
            var library = Directory.GetParent(Directory.GetParent(installRoot)!.FullName)!.FullName;
            return new WallpaperEngineInstallation(
                library,
                installRoot,
                executable,
                [library]);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
