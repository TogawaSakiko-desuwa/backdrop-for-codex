using System.Text.Json;
using BackdropForCodex.App.Services.Preferences;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class AppPreferencesStoreTests
{
    [Fact]
    public void PreferencesContractDoesNotExposeMachineLocalWallpaperEnginePaths()
    {
        Assert.DoesNotContain(
            typeof(AppPreferencesV1).GetProperties(),
            property => property.Name.Contains(
                "WallpaperEngineInstallRootPath",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsyncReturnsSystemDefaultsWhenDocumentDoesNotExist()
    {
        Assert.Equal("CodexWallpaper", AppPreferencesStore.SettingsDirectoryName);
        Assert.Equal("ui-settings.json", AppPreferencesStore.SettingsFileName);
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            using var store = new AppPreferencesStore(
                Path.Combine(directoryPath, AppPreferencesStore.SettingsFileName));

            var preferences = await store.LoadAsync();

            Assert.Equal(AppPreferencesV1.CurrentSchemaVersion, preferences.SchemaVersion);
            Assert.Equal(ThemeMode.System, preferences.ThemeMode);
            Assert.False(preferences.HasShownTrayTip);
            Assert.False(preferences.HasAcknowledgedWebWallpaperPrivacyNotice);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncAtomicallyRoundTripsUiOnlyPreferences()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(
                directoryPath,
                "nested",
                AppPreferencesStore.SettingsFileName);
            using var store = new AppPreferencesStore(preferencesPath);
            var expected = AppPreferencesV1.CreateDefault() with
            {
                ThemeMode = ThemeMode.Dark,
                HasShownTrayTip = true,
                HasAcknowledgedWebWallpaperPrivacyNotice = true,
            };

            await store.SaveAsync(expected);
            await store.SaveAsync(expected with { ThemeMode = ThemeMode.Light });
            var actual = await store.LoadAsync();

            Assert.Equal(ThemeMode.Light, actual.ThemeMode);
            Assert.True(actual.HasShownTrayTip);
            Assert.True(actual.HasAcknowledgedWebWallpaperPrivacyNotice);
            Assert.Empty(
                Directory.GetFiles(
                    Path.GetDirectoryName(preferencesPath)!,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly));
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(preferencesPath));
            Assert.Equal(
                AppPreferencesV1.CurrentSchemaVersion,
                document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                "Light",
                document.RootElement.GetProperty("themeMode").GetString());
            Assert.False(document.RootElement.TryGetProperty("mediaPath", out _));
            Assert.False(
                document.RootElement.TryGetProperty(
                    "wallpaperEngineInstallRootPath",
                    out _));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task LoadAsyncTreatsTheMissingWebPrivacyAcknowledgementAsNotAcknowledged()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(
                directoryPath,
                AppPreferencesStore.SettingsFileName);
            await File.WriteAllTextAsync(
                preferencesPath,
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\",\"hasShownTrayTip\":true}");
            using var store = new AppPreferencesStore(preferencesPath);

            var preferences = await store.LoadAsync();

            Assert.Equal(ThemeMode.Dark, preferences.ThemeMode);
            Assert.True(preferences.HasShownTrayTip);
            Assert.False(preferences.HasAcknowledgedWebWallpaperPrivacyNotice);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Theory]
    [InlineData("wallpaperEngineInstallRootPath")]
    [InlineData("WallpaperEngineInstallRootPath")]
    public async Task LoadAsyncAtomicallyRemovesTheDeprecatedWallpaperEngineRoot(
        string deprecatedPropertyName)
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(
                directoryPath,
                AppPreferencesStore.SettingsFileName);
            var privateInstallRoot = Path.Combine(
                directoryPath,
                "private-steam-library",
                "wallpaper_engine");
            await File.WriteAllTextAsync(
                preferencesPath,
                $$"""
                {
                  "schemaVersion": 1,
                  "themeMode": "Dark",
                  "hasShownTrayTip": true,
                  "hasAcknowledgedWebWallpaperPrivacyNotice": true,
                  {{JsonSerializer.Serialize(deprecatedPropertyName)}}: {{JsonSerializer.Serialize(privateInstallRoot)}}
                }
                """);
            using var store = new AppPreferencesStore(preferencesPath);

            var preferences = await store.LoadAsync();

            Assert.Equal(ThemeMode.Dark, preferences.ThemeMode);
            Assert.True(preferences.HasShownTrayTip);
            Assert.True(preferences.HasAcknowledgedWebWallpaperPrivacyNotice);
            var sanitized = await File.ReadAllTextAsync(preferencesPath);
            Assert.DoesNotContain(
                privateInstallRoot,
                sanitized,
                StringComparison.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(sanitized);
            Assert.False(
                document.RootElement.TryGetProperty(
                    "wallpaperEngineInstallRootPath",
                    out _));
            Assert.Empty(
                Directory.GetFiles(
                    directoryPath,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task LoadAsyncRejectsUnknownOrOversizedDocumentsWithoutEchoingPath()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(
                directoryPath,
                "private-ui-settings.json");
            using var store = new AppPreferencesStore(preferencesPath);
            await File.WriteAllTextAsync(
                preferencesPath,
                "{\"schemaVersion\":1,\"unexpected\":true}");

            var unknownProperty = await Assert.ThrowsAsync<AppPreferencesStoreException>(
                () => store.LoadAsync());

            Assert.Equal(AppPreferencesStoreOperation.Read, unknownProperty.Operation);
            Assert.DoesNotContain(
                preferencesPath,
                unknownProperty.Message,
                StringComparison.OrdinalIgnoreCase);

            var oversized = new string(
                ' ',
                checked((int)AppPreferencesStore.MaximumDocumentBytes + 1));
            await File.WriteAllTextAsync(preferencesPath, oversized);

            var tooLarge = await Assert.ThrowsAsync<AppPreferencesStoreException>(
                () => store.LoadAsync());

            Assert.Equal(AppPreferencesStoreOperation.Read, tooLarge.Operation);
            Assert.DoesNotContain(
                preferencesPath,
                tooLarge.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncRejectsInvalidPreferencesBeforeReplacingDocument()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(
                directoryPath,
                AppPreferencesStore.SettingsFileName);
            using var store = new AppPreferencesStore(preferencesPath);
            await store.SaveAsync(AppPreferencesV1.CreateDefault());
            var original = await File.ReadAllTextAsync(preferencesPath);
            var invalid = AppPreferencesV1.CreateDefault() with
            {
                ThemeMode = (ThemeMode)int.MaxValue,
            };

            await Assert.ThrowsAsync<AppPreferencesValidationException>(
                () => store.SaveAsync(invalid));

            Assert.Equal(original, await File.ReadAllTextAsync(preferencesPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task ResetAsyncRemovesOnlyTheUiPreferencesDocument()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(
                directoryPath,
                AppPreferencesStore.SettingsFileName);
            var wallpaperSettingsPath = Path.Combine(directoryPath, "settings.json");
            await File.WriteAllTextAsync(wallpaperSettingsPath, "wallpaper settings");
            using var store = new AppPreferencesStore(preferencesPath);
            await store.SaveAsync(
                AppPreferencesV1.CreateDefault() with { HasShownTrayTip = true });

            await store.ResetAsync();

            Assert.False(File.Exists(preferencesPath));
            Assert.True(File.Exists(wallpaperSettingsPath));
            Assert.Equal(AppPreferencesV1.CreateDefault(), await store.LoadAsync());
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
