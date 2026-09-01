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
            Assert.Equal(
                new[] { preferencesPath },
                Directory.GetFiles(Path.GetDirectoryName(preferencesPath)!));
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
    public async Task LoadAsyncDoesNotOverwriteACompetingWriteDuringDeprecatedPropertyCleanup()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(
                directoryPath,
                AppPreferencesStore.SettingsFileName);
            await File.WriteAllTextAsync(
                preferencesPath,
                """
                {
                  "schemaVersion": 1,
                  "themeMode": "Dark",
                  "wallpaperEngineInstallRootPath": "private-install-root"
                }
                """);
            var competingBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"themeMode\":\"Light\",\"hasShownTrayTip\":true}");
            using var store = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    AfterTemporaryFilePrepared: () =>
                        File.WriteAllBytes(preferencesPath, competingBytes)));

            var exception = await Assert.ThrowsAsync<AppPreferencesStoreException>(
                () => store.LoadAsync());

            Assert.Equal(AppPreferencesStoreOperation.Write, exception.Operation);
            Assert.Equal(competingBytes, await File.ReadAllBytesAsync(preferencesPath));
            Assert.Equal(new[] { preferencesPath }, Directory.GetFiles(directoryPath));
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

            var unknownProperty = await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
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

            var tooLarge = await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
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
    public async Task SaveAsyncRefusesToReplaceFuturePreferencesDocument()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(
                """
                {
                  "schemaVersion": 7,
                  "futurePreference": ["must", "survive"]
                }
                """);
            await File.WriteAllBytesAsync(preferencesPath, originalBytes);
            using var store = new AppPreferencesStore(preferencesPath);

            var exception = await Assert.ThrowsAsync<FuturePreferencesVersionException>(
                () => store.SaveAsync(
                    AppPreferencesV1.CreateDefault() with { ThemeMode = ThemeMode.Dark }));

            Assert.Equal(AppPreferencesStoreOperation.Write, exception.Operation);
            Assert.Equal(7, exception.SchemaVersion);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(preferencesPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncProtectsOversizedFutureDocumentUntilExplicitReset()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            var oversizedPayload = new string(
                'x',
                checked((int)AppPreferencesStore.MaximumDocumentBytes));
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(
                $$"""
                {
                  "schemaVersion": 2,
                  "futurePayload": "{{oversizedPayload}}"
                }
                """);
            Assert.True(originalBytes.LongLength > AppPreferencesStore.MaximumDocumentBytes);
            await File.WriteAllBytesAsync(preferencesPath, originalBytes);
            using var store = new AppPreferencesStore(preferencesPath);

            var exception = await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Equal(AppPreferencesStoreOperation.Write, exception.Operation);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(preferencesPath));

            await store.ResetAsync();
            var replacement = AppPreferencesV1.CreateDefault() with
            {
                ThemeMode = ThemeMode.Dark,
            };
            await store.SaveAsync(replacement);

            Assert.Equal(replacement, await store.LoadAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncProtectsUnclassifiableDocumentFromImplicitReplacement()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            byte[] originalBytes = [0x7B, 0x22, 0x73, 0x63, 0x68, 0x65, 0x6D, 0x61];
            await File.WriteAllBytesAsync(preferencesPath, originalBytes);
            using var store = new AppPreferencesStore(preferencesPath);

            var exception = await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Equal(AppPreferencesStoreOperation.Write, exception.Operation);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(preferencesPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":\"future\"}")]
    [InlineData("{\"schemaVersion\":0}")]
    public async Task SaveAsyncProtectsJsonThatIsNotAValidPreferencesVersion(
        string document)
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(document);
            await File.WriteAllBytesAsync(preferencesPath, originalBytes);
            using var store = new AppPreferencesStore(preferencesPath);

            await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(preferencesPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncDiagnosesOutOfRangePositiveIntegerSchemaAsUnsupportedVersion()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            const string schemaVersion = "2147483648";
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(
                $"{{\"schemaVersion\":{schemaVersion}}}");
            await File.WriteAllBytesAsync(preferencesPath, originalBytes);
            using var store = new AppPreferencesStore(preferencesPath);

            var exception = await Assert.ThrowsAsync<FuturePreferencesVersionException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Equal(AppPreferencesStoreOperation.Write, exception.Operation);
            Assert.Contains(schemaVersion, exception.Message, StringComparison.Ordinal);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(preferencesPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncBoundsOutOfRangeSchemaVersionDiagnostics()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            var schemaVersion = new string('9', 128);
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(
                $"{{\"schemaVersion\":{schemaVersion}}}");
            await File.WriteAllBytesAsync(preferencesPath, originalBytes);
            using var store = new AppPreferencesStore(preferencesPath);

            var exception = await Assert.ThrowsAsync<FuturePreferencesVersionException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Matches("^[0-9]{64}\\.\\.\\.$", exception.SchemaVersionDisplay);
            Assert.DoesNotContain(schemaVersion, exception.Message, StringComparison.Ordinal);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(preferencesPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncTreatsFractionalSchemaAsInvalidInsteadOfGuessingFutureVersion()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            const string document = "{\"schemaVersion\":1.5}";
            await File.WriteAllTextAsync(preferencesPath, document);
            using var store = new AppPreferencesStore(preferencesPath);

            var exception = await Assert.ThrowsAnyAsync<AppPreferencesStoreException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.IsNotType<FuturePreferencesVersionException>(exception);
            Assert.Equal(document, await File.ReadAllTextAsync(preferencesPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncRejectsAnExistingDocumentChangedWhileTheTemporaryFileIsPrepared()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            await File.WriteAllTextAsync(
                preferencesPath,
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\"}");
            var competingBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":7,\"futurePreference\":true}");
            var afterReplaceCount = 0;
            using var store = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    AfterTemporaryFilePrepared: () =>
                        File.WriteAllBytes(preferencesPath, competingBytes),
                    AfterReplace: () => afterReplaceCount++));

            await Assert.ThrowsAsync<FuturePreferencesVersionException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Equal(0, afterReplaceCount);
            Assert.Equal(competingBytes, await File.ReadAllBytesAsync(preferencesPath));
            Assert.Equal(new[] { preferencesPath }, Directory.GetFiles(directoryPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncRejectsAMissingDocumentCreatedWhileTheTemporaryFileIsPrepared()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            var competingBytes = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1");
            using var store = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    AfterTemporaryFilePrepared: () =>
                        File.WriteAllBytes(preferencesPath, competingBytes)));

            await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Equal(competingBytes, await File.ReadAllBytesAsync(preferencesPath));
            Assert.Equal(new[] { preferencesPath }, Directory.GetFiles(directoryPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Theory]
    [InlineData(
        "{\"schemaVersion\":7,\"futurePreference\":[\"must\",\"survive\"]}",
        typeof(FuturePreferencesVersionException))]
    [InlineData("{\"schemaVersion\":1", typeof(ProtectedPreferencesDocumentException))]
    [InlineData(
        "{\"schemaVersion\":1,\"themeMode\":\"Light\",\"hasShownTrayTip\":true}",
        typeof(AppPreferencesStoreException))]
    public async Task SaveAsyncRollsBackAReplacementThatRacesTheFinalPublish(
        string competingDocument,
        Type expectedExceptionType)
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            await File.WriteAllTextAsync(
                preferencesPath,
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\"}");
            var competingBytes = System.Text.Encoding.UTF8.GetBytes(competingDocument);
            var afterReplaceCount = 0;
            using var store = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    BeforePublish: () => File.WriteAllBytes(preferencesPath, competingBytes),
                    AfterReplace: () => afterReplaceCount++));

            var exception = await Record.ExceptionAsync(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.NotNull(exception);
            Assert.Equal(expectedExceptionType, exception.GetType());
            Assert.Equal(1, afterReplaceCount);
            Assert.Equal(competingBytes, await File.ReadAllBytesAsync(preferencesPath));
            Assert.Equal(new[] { preferencesPath }, Directory.GetFiles(directoryPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncRefusesADocumentCreatedWhileAMissingTargetIsPrepared()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            var competingBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":9,\"futurePreference\":true}");
            using var store = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    BeforePublish: () => File.WriteAllBytes(preferencesPath, competingBytes)));

            var exception = await Assert.ThrowsAsync<FuturePreferencesVersionException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Equal(AppPreferencesStoreOperation.Write, exception.Operation);
            Assert.Equal(competingBytes, await File.ReadAllBytesAsync(preferencesPath));
            Assert.Equal(new[] { preferencesPath }, Directory.GetFiles(directoryPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task PendingRecoveryAfterReplacementProtectsNewStoresUntilExplicitReset()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\"}");
            await File.WriteAllBytesAsync(preferencesPath, originalBytes);
            using (var interruptedStore = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    AfterReplace: () => throw new SimulatedProcessInterruptionException())))
            {
                await Assert.ThrowsAsync<SimulatedProcessInterruptionException>(
                    () => interruptedStore.SaveAsync(AppPreferencesV1.CreateDefault()));
            }

            var candidateBytes = await File.ReadAllBytesAsync(preferencesPath);
            var pendingRecoveryPath = Assert.Single(
                Directory.GetFiles(directoryPath, "*.pending-recovery"));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(pendingRecoveryPath));

            using var protectedStore = new AppPreferencesStore(preferencesPath);
            var loadException = await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
                () => protectedStore.LoadAsync());
            var saveException = await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
                () => protectedStore.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Equal(AppPreferencesStoreOperation.Read, loadException.Operation);
            Assert.Equal(AppPreferencesStoreOperation.Write, saveException.Operation);
            Assert.Equal(candidateBytes, await File.ReadAllBytesAsync(preferencesPath));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(pendingRecoveryPath));

            await protectedStore.ResetAsync();

            Assert.Empty(Directory.GetFiles(directoryPath));
            Assert.Equal(AppPreferencesV1.CreateDefault(), await protectedStore.LoadAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task CompetingStoreCannotDeleteSidecarsOwnedByAnInFlightSave()
    {
        var directoryPath = CreateTemporaryDirectory();
        using var competingAtPublish = new ManualResetEventSlim();
        using var releaseCompeting = new ManualResetEventSlim();
        using var ownerAfterReplace = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        Task? ownerTask = null;
        Task? competingTask = null;
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"themeMode\":\"System\"}");
            await File.WriteAllBytesAsync(preferencesPath, originalBytes);
            using var competingStore = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    BeforePublish: () =>
                    {
                        competingAtPublish.Set();
                        releaseCompeting.Wait();
                    }));
            using var ownerStore = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    AfterReplace: () =>
                    {
                        ownerAfterReplace.Set();
                        releaseOwner.Wait();
                    }));

            competingTask = Task.Run(
                () => competingStore.SaveAsync(
                    AppPreferencesV1.CreateDefault() with { HasShownTrayTip = true }));
            Assert.True(competingAtPublish.Wait(TimeSpan.FromSeconds(10)));

            ownerTask = Task.Run(
                () => ownerStore.SaveAsync(
                    AppPreferencesV1.CreateDefault() with { ThemeMode = ThemeMode.Light }));
            Assert.True(ownerAfterReplace.Wait(TimeSpan.FromSeconds(10)));
            var guardPath = Assert.Single(
                Directory.GetFiles(directoryPath, "*.transaction-in-progress"));
            var recoveryPath = Assert.Single(
                Directory.GetFiles(directoryPath, "*.pending-recovery"));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(recoveryPath));

            releaseCompeting.Set();
            var exception = await Assert.ThrowsAsync<AppPreferencesStoreException>(
                () => competingTask);

            Assert.Equal(AppPreferencesStoreOperation.Write, exception.Operation);
            Assert.True(File.Exists(guardPath));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(recoveryPath));
            using (var protectedStore = new AppPreferencesStore(preferencesPath))
            {
                await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
                    () => protectedStore.LoadAsync());
            }

            releaseOwner.Set();
            await ownerTask;

            Assert.Equal(new[] { preferencesPath }, Directory.GetFiles(directoryPath));
        }
        finally
        {
            releaseCompeting.Set();
            releaseOwner.Set();
            if (competingTask is not null)
            {
                try
                {
                    await competingTask;
                }
                catch (AppPreferencesStoreException)
                {
                }
            }

            if (ownerTask is not null)
            {
                await ownerTask;
            }

            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task InitialReplaceFailureWithoutStateChangeCleansOwnedSidecarsAndCanRetry()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\"}");
            await File.WriteAllBytesAsync(preferencesPath, originalBytes);
            using (var failingStore = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    ReplaceFile: (_, _, _) => throw new IOException("Injected failure."))))
            {
                var exception = await Assert.ThrowsAsync<AppPreferencesStoreException>(
                    () => failingStore.SaveAsync(AppPreferencesV1.CreateDefault()));
                Assert.Equal(AppPreferencesStoreOperation.Write, exception.Operation);
            }

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(preferencesPath));
            Assert.Equal(new[] { preferencesPath }, Directory.GetFiles(directoryPath));

            using var retryStore = new AppPreferencesStore(preferencesPath);
            var replacement = AppPreferencesV1.CreateDefault() with
            {
                ThemeMode = ThemeMode.Light,
            };
            await retryStore.SaveAsync(replacement);

            Assert.Equal(replacement, await retryStore.LoadAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task PartialInitialReplaceFailurePreservesGuardAndOriginalRecoveryBytes()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\"}");
            await File.WriteAllBytesAsync(preferencesPath, originalBytes);
            using var store = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    ReplaceFile: (_, destinationPath, backupPath) =>
                    {
                        File.Delete(backupPath);
                        File.Move(destinationPath, backupPath);
                        throw new IOException("Injected partial replacement failure.");
                    }));

            var exception = await Assert.ThrowsAsync<AppPreferencesStoreException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Equal(AppPreferencesStoreOperation.Write, exception.Operation);
            Assert.False(File.Exists(preferencesPath));
            Assert.Single(Directory.GetFiles(directoryPath, "*.transaction-in-progress"));
            var recoveryPath = Assert.Single(
                Directory.GetFiles(directoryPath, "*.pending-recovery"));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(recoveryPath));

            using var protectedStore = new AppPreferencesStore(preferencesPath);
            await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
                () => protectedStore.LoadAsync());
            await protectedStore.ResetAsync();
            Assert.Empty(Directory.GetFiles(directoryPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task PartialRollbackFailurePreservesAllFixedTransactionPayloads()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            await File.WriteAllTextAsync(
                preferencesPath,
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\"}");
            var competingBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"themeMode\":\"Light\"}");
            var replaceCount = 0;
            using var store = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    BeforePublish: () => File.WriteAllBytes(preferencesPath, competingBytes),
                    ReplaceFile: (sourcePath, destinationPath, backupPath) =>
                    {
                        if (Interlocked.Increment(ref replaceCount) == 1)
                        {
                            File.Replace(
                                sourcePath,
                                destinationPath,
                                backupPath,
                                ignoreMetadataErrors: true);
                            return;
                        }

                        File.Delete(backupPath);
                        File.Move(destinationPath, backupPath);
                        throw new IOException("Injected partial rollback failure.");
                    }));

            var exception = await Assert.ThrowsAsync<AppPreferencesStoreException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Equal(AppPreferencesStoreOperation.Write, exception.Operation);
            Assert.Equal(2, replaceCount);
            Assert.False(File.Exists(preferencesPath));
            Assert.Single(Directory.GetFiles(directoryPath, "*.transaction-in-progress"));
            var recoveryPath = Assert.Single(
                Directory.GetFiles(directoryPath, "*.pending-recovery"));
            var rollbackPath = Assert.Single(
                Directory.GetFiles(directoryPath, "*.pending-rollback"));
            Assert.Equal(competingBytes, await File.ReadAllBytesAsync(recoveryPath));
            Assert.NotEmpty(await File.ReadAllBytesAsync(rollbackPath));

            using var protectedStore = new AppPreferencesStore(preferencesPath);
            await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
                () => protectedStore.LoadAsync());
            await protectedStore.ResetAsync();
            Assert.Empty(Directory.GetFiles(directoryPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task LoadAsyncDoesNotReturnCandidateFromAnInFlightCompetingSave()
    {
        var directoryPath = CreateTemporaryDirectory();
        using var loadPassedInitialCheck = new ManualResetEventSlim();
        using var candidatePublished = new ManualResetEventSlim();
        using var releaseSave = new ManualResetEventSlim();
        Task<AppPreferencesV1>? loadTask = null;
        Task? saveTask = null;
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            await File.WriteAllTextAsync(
                preferencesPath,
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\"}");
            using var loadingStore = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    AfterInitialRecoveryCheck: () =>
                    {
                        loadPassedInitialCheck.Set();
                        candidatePublished.Wait();
                    }));
            using var savingStore = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    AfterReplace: () =>
                    {
                        candidatePublished.Set();
                        releaseSave.Wait();
                    }));

            loadTask = Task.Run(() => loadingStore.LoadAsync());
            Assert.True(loadPassedInitialCheck.Wait(TimeSpan.FromSeconds(10)));
            saveTask = Task.Run(
                () => savingStore.SaveAsync(
                    AppPreferencesV1.CreateDefault() with { ThemeMode = ThemeMode.Light }));
            Assert.True(candidatePublished.Wait(TimeSpan.FromSeconds(10)));

            var exception = await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
                () => loadTask);

            Assert.Equal(AppPreferencesStoreOperation.Read, exception.Operation);
            Assert.Single(Directory.GetFiles(directoryPath, "*.transaction-in-progress"));
            Assert.Single(Directory.GetFiles(directoryPath, "*.pending-recovery"));

            releaseSave.Set();
            await saveTask;

            Assert.Equal(new[] { preferencesPath }, Directory.GetFiles(directoryPath));
        }
        finally
        {
            candidatePublished.Set();
            releaseSave.Set();
            if (loadTask is not null)
            {
                try
                {
                    await loadTask;
                }
                catch (ProtectedPreferencesDocumentException)
                {
                }
            }

            if (saveTask is not null)
            {
                await saveTask;
            }

            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncDoesNotRollBackOverAChangeMadeAfterReplacement()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\"}");
            await File.WriteAllBytesAsync(preferencesPath, originalBytes);
            var afterReplaceBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"themeMode\":\"Light\"}");
            using var store = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    AfterReplace: () => File.WriteAllBytes(preferencesPath, afterReplaceBytes)));

            var exception = await Assert.ThrowsAsync<AppPreferencesStoreException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Equal(AppPreferencesStoreOperation.Write, exception.Operation);
            Assert.Equal(afterReplaceBytes, await File.ReadAllBytesAsync(preferencesPath));
            var recoveryPath = Assert.Single(
                Directory.GetFiles(directoryPath, "*.pending-recovery"));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(recoveryPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncPreservesAChangeThatRacesRollbackInPendingRecovery()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            await File.WriteAllTextAsync(
                preferencesPath,
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\"}");
            var beforeReplaceBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"themeMode\":\"Light\"}");
            var beforeRollbackBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\",\"hasShownTrayTip\":true}");
            using var store = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    BeforePublish: () =>
                        File.WriteAllBytes(preferencesPath, beforeReplaceBytes),
                    BeforeRollback: () =>
                        File.WriteAllBytes(preferencesPath, beforeRollbackBytes)));

            var exception = await Assert.ThrowsAsync<AppPreferencesStoreException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Equal(AppPreferencesStoreOperation.Write, exception.Operation);
            Assert.Equal(beforeReplaceBytes, await File.ReadAllBytesAsync(preferencesPath));
            var pendingRecoveryPath = Assert.Single(
                Directory.GetFiles(directoryPath, "*.pending-recovery"));
            Assert.Equal(
                beforeRollbackBytes,
                await File.ReadAllBytesAsync(pendingRecoveryPath));

            using var protectedStore = new AppPreferencesStore(preferencesPath);
            await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
                () => protectedStore.LoadAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task InterruptionAfterRollbackKeepsFixedGuardAndRollbackPayloadProtected()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            await File.WriteAllTextAsync(
                preferencesPath,
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\"}");
            var beforeReplaceBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"themeMode\":\"Light\"}");
            var beforeRollbackBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\",\"hasShownTrayTip\":true}");
            using (var interruptedStore = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    BeforePublish: () =>
                        File.WriteAllBytes(preferencesPath, beforeReplaceBytes),
                    BeforeRollback: () =>
                        File.WriteAllBytes(preferencesPath, beforeRollbackBytes),
                    AfterRollback: () => throw new SimulatedProcessInterruptionException())))
            {
                await Assert.ThrowsAsync<SimulatedProcessInterruptionException>(
                    () => interruptedStore.SaveAsync(AppPreferencesV1.CreateDefault()));
            }

            Assert.Equal(beforeReplaceBytes, await File.ReadAllBytesAsync(preferencesPath));
            var guardPath = Assert.Single(
                Directory.GetFiles(directoryPath, "*.transaction-in-progress"));
            var rollbackPath = Assert.Single(
                Directory.GetFiles(directoryPath, "*.pending-rollback"));
            Assert.Equal(beforeRollbackBytes, await File.ReadAllBytesAsync(rollbackPath));

            using var protectedStore = new AppPreferencesStore(preferencesPath);
            var loadException = await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
                () => protectedStore.LoadAsync());
            var saveException = await Assert.ThrowsAsync<ProtectedPreferencesDocumentException>(
                () => protectedStore.SaveAsync(AppPreferencesV1.CreateDefault()));

            Assert.Equal(AppPreferencesStoreOperation.Read, loadException.Operation);
            Assert.Equal(AppPreferencesStoreOperation.Write, saveException.Operation);
            Assert.True(File.Exists(guardPath));
            Assert.Equal(beforeRollbackBytes, await File.ReadAllBytesAsync(rollbackPath));

            await protectedStore.ResetAsync();

            Assert.Empty(Directory.GetFiles(directoryPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncHonorsCancellationAtTheFinalPublishBoundaryAndCleansTemporaryFiles()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var preferencesPath = Path.Combine(directoryPath, "ui-settings.json");
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"themeMode\":\"Dark\"}");
            await File.WriteAllBytesAsync(preferencesPath, originalBytes);
            using var cancellation = new CancellationTokenSource();
            using var store = new AppPreferencesStore(
                preferencesPath,
                serializerOptions: null,
                testHooks: new AppPreferencesStoreTestHooks(
                    BeforePublish: cancellation.Cancel));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.SaveAsync(AppPreferencesV1.CreateDefault(), cancellation.Token));

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(preferencesPath));
            Assert.Equal(new[] { preferencesPath }, Directory.GetFiles(directoryPath));
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

    private sealed class SimulatedProcessInterruptionException : Exception
    {
    }
}
