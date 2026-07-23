using System.Text.Json;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Settings;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task LoadAsyncReturnsDefaultsWhenDocumentDoesNotExist()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            using var store = new SettingsStore(Path.Combine(directoryPath, "settings.json"));

            var settings = await store.LoadAsync();

            Assert.Equal(SettingsV1.CreateDefault(), settings);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncRoundTripsTheVersionedContract()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "nested", "settings.json");
            var mediaPath = Path.GetFullPath(Path.Combine(directoryPath, "wallpaper.webm"));
            var recentPath = Path.GetFullPath(Path.Combine(directoryPath, "previous.png"));
            using var store = new SettingsStore(settingsPath);
            var expected = new SettingsV1
            {
                MediaPath = mediaPath,
                MediaKind = MediaKind.Video,
                Fit = WallpaperFit.Stretch,
                FocusX = 0.25,
                FocusY = 0.75,
                PanelOpacity = 0.9,
                BlurPx = 8,
                DarkOverlay = 0.4,
                LightOverlay = 0.1,
                RecentMediaPaths = [mediaPath, recentPath],
                AcceptedCdpRisk = true,
                LastCompatibilityProfileId = "codex-reviewed-profile",
            };

            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();

            Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
            Assert.Equal(expected.MediaPath, actual.MediaPath);
            Assert.Equal(expected.MediaKind, actual.MediaKind);
            Assert.Equal(expected.Fit, actual.Fit);
            Assert.Equal(expected.FocusX, actual.FocusX);
            Assert.Equal(expected.FocusY, actual.FocusY);
            Assert.Equal(expected.PanelOpacity, actual.PanelOpacity);
            Assert.Equal(expected.BlurPx, actual.BlurPx);
            Assert.Equal(expected.DarkOverlay, actual.DarkOverlay);
            Assert.Equal(expected.LightOverlay, actual.LightOverlay);
            Assert.Equal(expected.RecentMediaPaths, actual.RecentMediaPaths);
            Assert.Equal(expected.AcceptedCdpRisk, actual.AcceptedCdpRisk);
            Assert.Equal(expected.LastCompatibilityProfileId, actual.LastCompatibilityProfileId);

            var json = await File.ReadAllTextAsync(settingsPath);
            Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
            Assert.Contains("\"mediaKind\": \"Video\"", json, StringComparison.Ordinal);
            Assert.Contains("\"fit\": \"Stretch\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("volume", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("playbackRate", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("muted", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("loop", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncClampsLegacyOverlayValuesWithoutChangingSchema()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            using var store = new SettingsStore(settingsPath);
            var legacySettings = SettingsV1.CreateDefault() with
            {
                DarkOverlay = 0.85,
                LightOverlay = 1,
            };

            await store.SaveAsync(legacySettings);
            var loaded = await store.LoadAsync();

            Assert.Equal(1, loaded.SchemaVersion);
            Assert.Equal(SettingsV1.MaximumEffectiveOverlay, loaded.DarkOverlay);
            Assert.Equal(SettingsV1.MaximumEffectiveOverlay, loaded.LightOverlay);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Theory]
    [InlineData("Cover", WallpaperFit.Cover)]
    [InlineData("Contain", WallpaperFit.Contain)]
    public async Task LoadAsyncReadsLegacyFitNamesWithoutSchemaMigration(
        string fitName,
        WallpaperFit expectedFit)
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            await File.WriteAllTextAsync(
                settingsPath,
                $$"""
                {
                  "schemaVersion": 1,
                  "mediaPath": null,
                  "mediaKind": "None",
                  "fit": "{{fitName}}",
                  "focusX": 0.5,
                  "focusY": 0.5,
                  "panelOpacity": 0.78,
                  "blurPx": 14,
                  "darkOverlay": 0.3,
                  "lightOverlay": 0.18,
                  "recentMediaPaths": [],
                  "acceptedCdpRisk": false
                }
                """);
            using var store = new SettingsStore(settingsPath);

            var loaded = await store.LoadAsync();

            Assert.Equal(1, loaded.SchemaVersion);
            Assert.Equal(expectedFit, loaded.Fit);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncAtomicallyReplacesAnExistingDocumentAndCleansTemporaryFiles()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            using var store = new SettingsStore(settingsPath);
            await store.SaveAsync(SettingsV1.CreateDefault());

            var replacement = SettingsV1.CreateDefault() with
            {
                AcceptedCdpRisk = true,
                PanelOpacity = 0.91,
            };
            await store.SaveAsync(replacement);

            var loaded = await store.LoadAsync();
            Assert.True(loaded.AcceptedCdpRisk);
            Assert.Equal(0.91, loaded.PanelOpacity);
            Assert.Empty(Directory.GetFiles(directoryPath, "*.tmp", SearchOption.TopDirectoryOnly));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task LoadAsyncRejectsInvalidJsonWithoutEchoingTheSettingsPath()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "private-settings.json");
            await File.WriteAllTextAsync(settingsPath, "{ definitely not json }");
            using var store = new SettingsStore(settingsPath);

            var exception = await Assert.ThrowsAsync<SettingsStoreException>(() => store.LoadAsync());

            Assert.DoesNotContain(settingsPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task LoadAsyncRejectsUnknownPropertiesAndOversizedDocuments()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            using var store = new SettingsStore(settingsPath);

            await File.WriteAllTextAsync(settingsPath, "{\"schemaVersion\":1,\"unexpected\":true}");
            await Assert.ThrowsAsync<SettingsStoreException>(() => store.LoadAsync());

            var oversized = new string(' ', checked((int)SettingsStore.MaximumDocumentBytes + 1));
            await File.WriteAllTextAsync(settingsPath, oversized);
            await Assert.ThrowsAsync<SettingsStoreException>(() => store.LoadAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncRejectsInvalidSettingsBeforeReplacingCurrentDocument()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            using var store = new SettingsStore(settingsPath);
            await store.SaveAsync(SettingsV1.CreateDefault());
            var originalJson = await File.ReadAllTextAsync(settingsPath);
            var invalid = SettingsV1.CreateDefault() with { BlurPx = 25 };

            await Assert.ThrowsAsync<SettingsValidationException>(() => store.SaveAsync(invalid));

            Assert.Equal(originalJson, await File.ReadAllTextAsync(settingsPath));
            Assert.Empty(Directory.GetFiles(directoryPath, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "BackdropForCodex.Core.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

    private static void DeleteTemporaryDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}
