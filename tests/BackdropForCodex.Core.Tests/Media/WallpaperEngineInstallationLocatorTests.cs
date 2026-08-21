using BackdropForCodex.Core.Media;
using Microsoft.Win32;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperEngineInstallationLocatorTests
{
    [Fact]
    public async Task LocatorFindsManifestAcrossEveryValidatedSteamLibrary()
    {
        using var fixture = new SteamInstallationFixture();
        var secondaryLibrary = fixture.AddLibrary("SecondaryLibrary");
        fixture.WriteLibraryFolders([fixture.SteamRoot, secondaryLibrary]);
        var installRoot = fixture.InstallWallpaperEngine(secondaryLibrary);
        var locator = fixture.CreateLocator();

        var installation = await locator.LocateAsync();

        Assert.Equal(installRoot, installation.InstallRootPath, ignoreCase: true);
        Assert.Equal(
            Path.Combine(installRoot, "wallpaper64.exe"),
            installation.ControlExecutablePath,
            ignoreCase: true);
        Assert.Equal(2, installation.SteamLibraryPaths.Count);
        Assert.Contains(
            fixture.SteamRoot,
            installation.SteamLibraryPaths,
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            secondaryLibrary,
            installation.SteamLibraryPaths,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(installRoot, installation.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocatorRequiresSelectionWhenMultipleValidatedInstallsExist()
    {
        using var fixture = new SteamInstallationFixture();
        var secondaryLibrary = fixture.AddLibrary("SecondaryLibrary");
        fixture.WriteLibraryFolders([fixture.SteamRoot, secondaryLibrary]);
        _ = fixture.InstallWallpaperEngine(fixture.SteamRoot);
        var preferred = fixture.InstallWallpaperEngine(secondaryLibrary);

        var ambiguous = await Assert.ThrowsAsync<WallpaperEngineUnavailableException>(
            () => fixture.CreateLocator().LocateAsync().AsTask());
        var selected = await fixture.CreateLocator(preferred).LocateAsync();

        Assert.Equal(
            WallpaperEngineAvailabilityReason.MultipleInstallations,
            ambiguous.Reason);
        Assert.Equal(preferred, selected.InstallRootPath, ignoreCase: true);
    }

    [Fact]
    public async Task ExplicitSelectionCanRecoverWithoutRegistrySteamRoot()
    {
        using var fixture = new SteamInstallationFixture();
        var installRoot = fixture.InstallWallpaperEngine(fixture.SteamRoot);
        var locator = new WallpaperEngineInstallationLocator(
            Array.Empty<string>(),
            new AcceptingTrustVerifier(),
            installRoot);

        var installation = await locator.LocateAsync();

        Assert.Equal(installRoot, installation.InstallRootPath, ignoreCase: true);
        Assert.Contains(
            fixture.SteamRoot,
            installation.SteamLibraryPaths,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitSelectionIgnoresUnrelatedCorruptSteamMetadata()
    {
        using var corruptFixture = new SteamInstallationFixture();
        File.WriteAllText(
            corruptFixture.GetLibraryFoldersPath(),
            "\"libraryfolders\" { \"0\" {");
        using var selectedFixture = new SteamInstallationFixture();
        var installRoot = selectedFixture.InstallWallpaperEngine(
            selectedFixture.SteamRoot);
        var locator = new WallpaperEngineInstallationLocator(
            [corruptFixture.SteamRoot],
            new AcceptingTrustVerifier(),
            installRoot);

        var installation = await locator.LocateAsync();

        Assert.Equal(installRoot, installation.InstallRootPath, ignoreCase: true);
        Assert.Contains(
            selectedFixture.SteamRoot,
            installation.SteamLibraryPaths,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            corruptFixture.SteamRoot,
            installation.SteamLibraryPaths,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutomaticDiscoveryIgnoresUnrelatedCorruptSteamRootWhenOneValidInstallExists()
    {
        using var corruptFixture = new SteamInstallationFixture();
        File.WriteAllText(
            corruptFixture.GetLibraryFoldersPath(),
            "\"libraryfolders\" { \"0\" {");
        using var validFixture = new SteamInstallationFixture();
        var installRoot = validFixture.InstallWallpaperEngine(validFixture.SteamRoot);
        var locator = new WallpaperEngineInstallationLocator(
            [corruptFixture.SteamRoot, validFixture.SteamRoot],
            new AcceptingTrustVerifier());

        var installation = await locator.LocateAsync();

        Assert.Equal(installRoot, installation.InstallRootPath, ignoreCase: true);
        Assert.Contains(
            validFixture.SteamRoot,
            installation.SteamLibraryPaths,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            corruptFixture.SteamRoot,
            installation.SteamLibraryPaths,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutomaticDiscoveryIgnoresUnrelatedCorruptManifestWhenOneValidInstallExists()
    {
        using var corruptFixture = new SteamInstallationFixture();
        File.WriteAllText(
            Path.Combine(
                corruptFixture.SteamRoot,
                "steamapps",
                "appmanifest_431960.acf"),
            "\"AppState\" { \"appid\"");
        using var validFixture = new SteamInstallationFixture();
        var installRoot = validFixture.InstallWallpaperEngine(validFixture.SteamRoot);
        var locator = new WallpaperEngineInstallationLocator(
            [corruptFixture.SteamRoot, validFixture.SteamRoot],
            new AcceptingTrustVerifier());

        var installation = await locator.LocateAsync();

        Assert.Equal(installRoot, installation.InstallRootPath, ignoreCase: true);
    }

    [Theory]
    [InlineData(@"relative\Steam")]
    [InlineData(@"\\server\share\Steam")]
    [InlineData("C:\\Steam\0invalid")]
    public async Task AutomaticDiscoveryChecksLaterRegistryViewAfterMalformedRoot(
        string malformedRoot)
    {
        using var fixture = new SteamInstallationFixture();
        var installRoot = fixture.InstallWallpaperEngine(fixture.SteamRoot);
        var registrySource = new FakeSteamRegistryValueSource()
            .WithValue(
                RegistryHive.CurrentUser,
                RegistryView.Default,
                "SteamPath",
                malformedRoot)
            .WithValue(
                RegistryHive.LocalMachine,
                RegistryView.Registry64,
                "InstallPath",
                fixture.SteamRoot);
        var locator = new WallpaperEngineInstallationLocator(
            registrySource,
            new AcceptingTrustVerifier());

        var installation = await locator.LocateAsync();

        Assert.Equal(installRoot, installation.InstallRootPath, ignoreCase: true);
    }

    [Fact]
    public async Task AutomaticDiscoveryReportsMalformedOnlyRegistryRootAsInvalidConfiguration()
    {
        var registrySource = new FakeSteamRegistryValueSource()
            .WithValue(
                RegistryHive.CurrentUser,
                RegistryView.Default,
                "SteamPath",
                @"\\server\share\Steam");
        var locator = new WallpaperEngineInstallationLocator(
            registrySource,
            new AcceptingTrustVerifier());

        var exception = await Assert.ThrowsAsync<WallpaperEngineUnavailableException>(
            () => locator.LocateAsync().AsTask());

        Assert.Equal(
            WallpaperEngineAvailabilityReason.InvalidSteamConfiguration,
            exception.Reason);
    }

    [Fact]
    public async Task AutomaticDiscoveryReportsMissingRegistryRootsAsSteamNotFound()
    {
        var locator = new WallpaperEngineInstallationLocator(
            new FakeSteamRegistryValueSource(),
            new AcceptingTrustVerifier());

        var exception = await Assert.ThrowsAsync<WallpaperEngineUnavailableException>(
            () => locator.LocateAsync().AsTask());

        Assert.Equal(WallpaperEngineAvailabilityReason.SteamNotFound, exception.Reason);
    }

    [Fact]
    public void RegistryDiscoveryDoesNotSwallowCancellation()
    {
        var expected = new OperationCanceledException("Synthetic cancellation.");

        var actual = Assert.Throws<OperationCanceledException>(
            () => WindowsSteamRootDiscovery.Discover(
                new ThrowingSteamRegistryValueSource(expected)));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void RegistryDiscoveryDoesNotSwallowUnexpectedExceptions()
    {
        var expected = new InvalidOperationException("Synthetic unexpected failure.");

        var actual = Assert.Throws<InvalidOperationException>(
            () => WindowsSteamRootDiscovery.Discover(
                new ThrowingSteamRegistryValueSource(expected)));

        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData("999999", WallpaperEngineAvailabilityReason.InvalidSteamConfiguration)]
    [InlineData("431960", WallpaperEngineAvailabilityReason.ExecutableUntrusted)]
    public async Task LocatorReportsTypedManifestAndTrustFailures(
        string appId,
        WallpaperEngineAvailabilityReason expectedReason)
    {
        using var fixture = new SteamInstallationFixture();
        fixture.WriteLibraryFolders([fixture.SteamRoot]);
        _ = fixture.InstallWallpaperEngine(fixture.SteamRoot, appId);
        IWallpaperEngineExecutableTrustVerifier trustVerifier =
            expectedReason == WallpaperEngineAvailabilityReason.ExecutableUntrusted
            ? new RejectingTrustVerifier()
            : new AcceptingTrustVerifier();
        var locator = new WallpaperEngineInstallationLocator(
            [fixture.SteamRoot],
            trustVerifier);

        var exception = await Assert.ThrowsAsync<WallpaperEngineUnavailableException>(
            () => locator.LocateAsync().AsTask());

        Assert.Equal(expectedReason, exception.Reason);
        Assert.DoesNotContain(fixture.RootPath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocatorRejectsTraversalAndNetworkLibraryMetadata()
    {
        using var traversalFixture = new SteamInstallationFixture();
        traversalFixture.WriteLibraryFolders([traversalFixture.SteamRoot]);
        SteamInstallationFixture.WriteManifest(
            traversalFixture.SteamRoot,
            appId: "431960",
            installDirectory: @"..\outside");
        var traversal = await Assert.ThrowsAsync<WallpaperEngineUnavailableException>(
            () => traversalFixture.CreateLocator().LocateAsync().AsTask());

        using var networkFixture = new SteamInstallationFixture();
        networkFixture.WriteLibraryFolders([networkFixture.SteamRoot, @"\\server\share"]);
        var network = await Assert.ThrowsAsync<WallpaperEngineUnavailableException>(
            () => networkFixture.CreateLocator().LocateAsync().AsTask());

        Assert.Equal(
            WallpaperEngineAvailabilityReason.InvalidSteamConfiguration,
            traversal.Reason);
        Assert.Equal(
            WallpaperEngineAvailabilityReason.InvalidSteamConfiguration,
            network.Reason);
    }

    [Fact]
    public async Task LocatorRejectsReparseLibraryWhenSymbolicLinksAreAvailable()
    {
        using var fixture = new SteamInstallationFixture();
        var realLibrary = fixture.AddLibrary("RealLibrary");
        var linkedLibrary = Path.Combine(fixture.RootPath, "LinkedLibrary");
        try
        {
            Directory.CreateSymbolicLink(linkedLibrary, realLibrary);
        }
        catch (Exception linkException) when (
            linkException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        fixture.WriteLibraryFolders([fixture.SteamRoot, linkedLibrary]);
        var exception = await Assert.ThrowsAsync<WallpaperEngineUnavailableException>(
            () => fixture.CreateLocator().LocateAsync().AsTask());

        Assert.Equal(
            WallpaperEngineAvailabilityReason.InvalidSteamConfiguration,
            exception.Reason);
    }

    [Fact]
    public async Task LocatorBoundsValveKeyValuesInputAndToleratesUnknownEntries()
    {
        using var validFixture = new SteamInstallationFixture();
        validFixture.WriteLibraryFolders(
            [validFixture.SteamRoot],
            includeUnknownEntries: true);
        _ = validFixture.InstallWallpaperEngine(validFixture.SteamRoot);

        var installation = await validFixture.CreateLocator().LocateAsync();

        Assert.Equal(
            Path.Combine(
                validFixture.SteamRoot,
                "steamapps",
                "common",
                "wallpaper_engine"),
            installation.InstallRootPath,
            ignoreCase: true);

        using var oversizedFixture = new SteamInstallationFixture();
        var libraryFoldersPath = oversizedFixture.GetLibraryFoldersPath();
        Directory.CreateDirectory(Path.GetDirectoryName(libraryFoldersPath)!);
        await File.WriteAllTextAsync(
            libraryFoldersPath,
            new string(' ', ValveDocumentMaximumLengthForTest + 1));
        var oversized = await Assert.ThrowsAsync<WallpaperEngineUnavailableException>(
            () => oversizedFixture.CreateLocator().LocateAsync().AsTask());
        Assert.Equal(
            WallpaperEngineAvailabilityReason.InvalidSteamConfiguration,
            oversized.Reason);
    }

    [Fact]
    public void AvailabilityExceptionRedactsPathBearingDiagnosticFailures()
    {
        var sensitivePath = Path.Combine(
            Path.GetTempPath(),
            "private-steam-library",
            "steamapps",
            "libraryfolders.vdf");
        var exception = new WallpaperEngineUnavailableException(
            WallpaperEngineAvailabilityReason.InvalidSteamConfiguration,
            new IOException($"Could not read {sensitivePath}."));

        Assert.DoesNotContain(
            sensitivePath,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(exception.InnerException);
        Assert.Equal(nameof(IOException), exception.DiagnosticExceptionType);
        Assert.NotEqual(0, exception.DiagnosticHResult);
    }

    private const int ValveDocumentMaximumLengthForTest = 1024 * 1024;

    private sealed class AcceptingTrustVerifier : IWallpaperEngineExecutableTrustVerifier
    {
        public bool IsTrusted(string executablePath) => File.Exists(executablePath);
    }

    private sealed class RejectingTrustVerifier : IWallpaperEngineExecutableTrustVerifier
    {
        public bool IsTrusted(string executablePath) => false;
    }

    private sealed class FakeSteamRegistryValueSource : IWindowsSteamRegistryValueSource
    {
        private readonly Dictionary<RegistryValueKey, string?> _values = [];

        public FakeSteamRegistryValueSource WithValue(
            RegistryHive hive,
            RegistryView view,
            string valueName,
            string? value)
        {
            _values[new RegistryValueKey(hive, view, valueName)] = value;
            return this;
        }

        public string? ReadValue(
            RegistryHive hive,
            RegistryView view,
            string subKeyName,
            string valueName) =>
            _values.GetValueOrDefault(new RegistryValueKey(hive, view, valueName));

        private readonly record struct RegistryValueKey(
            RegistryHive Hive,
            RegistryView View,
            string ValueName);
    }

    private sealed class ThrowingSteamRegistryValueSource(Exception failure)
        : IWindowsSteamRegistryValueSource
    {
        public string? ReadValue(
            RegistryHive hive,
            RegistryView view,
            string subKeyName,
            string valueName) => throw failure;
    }

    private sealed class SteamInstallationFixture : IDisposable
    {
        public SteamInstallationFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "BackdropForCodex.Core.Tests",
                Guid.NewGuid().ToString("N"));
            SteamRoot = Path.Combine(RootPath, "Steam");
            Directory.CreateDirectory(Path.Combine(SteamRoot, "steamapps"));
        }

        public string RootPath { get; }

        public string SteamRoot { get; }

        public string AddLibrary(string name)
        {
            var path = Path.Combine(RootPath, name);
            Directory.CreateDirectory(Path.Combine(path, "steamapps"));
            return path;
        }

        public string InstallWallpaperEngine(
            string library,
            string appId = "431960")
        {
            EnsureActive();
            WriteManifest(library, appId, "wallpaper_engine");
            var installRoot = Path.Combine(
                library,
                "steamapps",
                "common",
                "wallpaper_engine");
            Directory.CreateDirectory(installRoot);
            File.WriteAllText(Path.Combine(installRoot, "wallpaper64.exe"), "fixture");
            return Path.GetFullPath(installRoot);
        }

        public static void WriteManifest(
            string library,
            string appId,
            string installDirectory)
        {
            var steamApps = Path.Combine(library, "steamapps");
            Directory.CreateDirectory(steamApps);
            File.WriteAllText(
                Path.Combine(steamApps, "appmanifest_431960.acf"),
                $$"""
                "AppState"
                {
                    "appid" "{{appId}}"
                    "installdir" "{{EscapeVdf(installDirectory)}}"
                    "future_field" "ignored"
                }
                """);
        }

        public void WriteLibraryFolders(
            IReadOnlyList<string> libraries,
            bool includeUnknownEntries = false)
        {
            var entries = string.Join(
                Environment.NewLine,
                libraries.Select((library, index) => $$"""
                    "{{index}}"
                    {
                        "path" "{{EscapeVdf(library)}}"
                        "apps" { "431960" "1" }
                        {{(includeUnknownEntries ? "\"future\" \"ignored\"" : string.Empty)}}
                    }
                    """));
            File.WriteAllText(
                GetLibraryFoldersPath(),
                $$"""
                // Synthetic Valve KeyValues fixture.
                "libraryfolders"
                {
                    {{entries}}
                    "future_root" "ignored"
                }
                """);
        }

        public string GetLibraryFoldersPath() =>
            Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf");

        public WallpaperEngineInstallationLocator CreateLocator(
            string? preferredInstallRoot = null) =>
            new(
                [SteamRoot],
                new AcceptingTrustVerifier(),
                preferredInstallRoot);

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        private void EnsureActive() =>
            ObjectDisposedException.ThrowIf(!Directory.Exists(RootPath), this);

        private static string EscapeVdf(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
