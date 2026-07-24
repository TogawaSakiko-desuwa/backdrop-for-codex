using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Settings;

public sealed class SettingsRepositoryTests
{
    private static readonly JsonSerializerOptions Version1SerializerOptions =
        CreateVersion1SerializerOptions();

    [Fact]
    public async Task LoadAsyncReturnsFreshV2DefaultsWhenDocumentDoesNotExist()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            using var repository = CreateRepository(directoryPath);

            var result = await repository.LoadAsync();

            var ready = Assert.IsType<SettingsLoadResult.Ready>(result);
            Assert.False(ready.MigratedFromVersion1);
            Assert.Equal(2, ready.Settings.SchemaVersion);
            Assert.Equal(7, Assert.Single(ready.Settings.Profiles).ProfileId.Version);
            Assert.False(File.Exists(Path.Combine(directoryPath, "settings.json")));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncReturnsAndPersistsTheCanonicalV2Snapshot()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "nested", "settings.json");
            using var repository = new SettingsRepository(settingsPath);
            var media = new MediaReference
            {
                MediaId = Guid.CreateVersion7(),
                SourceKind = MediaSourceKind.LocalFile,
                SourceIdentifier = Path.Combine(directoryPath, ".", "wallpaper.png"),
                LastKnownKind = MediaKind.Image,
            };
            var profile = WallpaperProfile.CreateDefault("  Custom profile  ") with
            {
                MediaId = media.MediaId,
                Fit = WallpaperFit.Stretch,
                FocusX = 0.25,
                FocusY = 0.75,
                PanelOpacity = 0.9,
                BlurPx = 8,
                DarkOverlay = 0.85,
                LightOverlay = 0.9,
                SoundEnabled = true,
                Volume = 0.35,
                PerformancePolicy = PerformancePolicy.PreferQuality,
            };
            var settings = new SettingsV2
            {
                Profiles = [profile],
                MediaCatalog = [media],
                RecentMediaIds = [media.MediaId],
                RegionBindings = new Dictionary<SemanticRegion, Guid>
                {
                    [SemanticRegion.Global] = profile.ProfileId,
                    [SemanticRegion.Home] = profile.ProfileId,
                },
                AcceptedCdpRisk = true,
                LastCompatibilityProfileId = "reviewed-profile",
            };

            var canonical = await repository.SaveAsync(settings);
            var loadedResult = await repository.LoadAsync();

            var loaded = Assert.IsType<SettingsLoadResult.Ready>(loadedResult).Settings;
            var canonicalProfile = Assert.Single(canonical.Profiles);
            Assert.Equal("Custom profile", canonicalProfile.Name);
            Assert.Equal(0.85, canonicalProfile.DarkOverlay);
            Assert.Equal(0.9, canonicalProfile.LightOverlay);
            Assert.Equal(Path.GetFullPath(media.SourceIdentifier), Assert.Single(canonical.MediaCatalog).SourceIdentifier);
            Assert.Equal(canonicalProfile, Assert.Single(loaded.Profiles));
            Assert.Equal(canonical.MediaCatalog, loaded.MediaCatalog);
            Assert.Equal(canonical.RecentMediaIds, loaded.RecentMediaIds);
            Assert.Equal(canonical.RegionBindings, loaded.RegionBindings);
            Assert.True(loaded.AcceptedCdpRisk);
            Assert.Equal("reviewed-profile", loaded.LastCompatibilityProfileId);

            var json = await File.ReadAllTextAsync(settingsPath);
            Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);
            Assert.Contains("\"sourceKind\": \"LocalFile\"", json, StringComparison.Ordinal);
            Assert.Contains("\"Global\"", json, StringComparison.Ordinal);
            Assert.Contains("\"performancePolicy\": \"PreferQuality\"", json, StringComparison.Ordinal);
            Assert.Empty(
                Directory.GetFiles(
                    Path.GetDirectoryName(settingsPath)!,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task LoadAsyncMigratesV1WithoutAccessingMediaAndPreservesRawBackup()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var selectedPath = Path.Combine(directoryPath, "does-not-exist.webm");
            var olderPath = Path.Combine(directoryPath, "also-missing.png");
            var version1 = new SettingsV1
            {
                MediaPath = selectedPath,
                MediaKind = MediaKind.Video,
                Fit = WallpaperFit.Stretch,
                FocusX = 0.2,
                FocusY = 0.8,
                PanelOpacity = 0.91,
                BlurPx = 7,
                DarkOverlay = 0.85,
                LightOverlay = 0.9,
                RecentMediaPaths = [olderPath, selectedPath.ToUpperInvariant()],
                AcceptedCdpRisk = true,
                LastCompatibilityProfileId = "legacy-reviewed-profile",
            };
            var originalBytes = SerializeVersion1(version1, includeSchemaVersion: false);
            Assert.DoesNotContain(
                "schemaVersion",
                Encoding.UTF8.GetString(originalBytes),
                StringComparison.OrdinalIgnoreCase);
            await File.WriteAllBytesAsync(settingsPath, originalBytes);
            using var repository = new SettingsRepository(settingsPath);

            var result = await repository.LoadAsync();

            var ready = Assert.IsType<SettingsLoadResult.Ready>(result);
            Assert.True(ready.MigratedFromVersion1);
            var migrated = ready.Settings;
            var profile = Assert.Single(migrated.Profiles);
            Assert.Equal(selectedPath, migrated.FindMedia(profile.MediaId!.Value)!.SourceIdentifier);
            Assert.Equal(MediaKind.Video, migrated.FindMedia(profile.MediaId.Value)!.LastKnownKind);
            Assert.Equal(WallpaperFit.Stretch, profile.Fit);
            Assert.Equal(0.2, profile.FocusX);
            Assert.Equal(0.8, profile.FocusY);
            Assert.Equal(0.91, profile.PanelOpacity);
            Assert.Equal(7, profile.BlurPx);
            Assert.Equal(0.85, profile.DarkOverlay);
            Assert.Equal(0.9, profile.LightOverlay);
            Assert.False(profile.SoundEnabled);
            Assert.Equal(0.5, profile.Volume);
            Assert.Equal(PerformancePolicy.Automatic, profile.PerformancePolicy);
            Assert.True(migrated.AcceptedCdpRisk);
            Assert.Equal("legacy-reviewed-profile", migrated.LastCompatibilityProfileId);
            Assert.Single(migrated.RegionBindings);
            Assert.Equal(profile.ProfileId, migrated.RegionBindings[SemanticRegion.Global]);
            Assert.Equal(2, migrated.MediaCatalog.Count);
            Assert.Equal(2, migrated.RecentMediaIds.Count);
            Assert.Equal(
                olderPath,
                migrated.FindMedia(migrated.RecentMediaIds[0])!.SourceIdentifier);
            Assert.Equal(profile.MediaId, migrated.RecentMediaIds[1]);
            Assert.Equal(
                MediaKind.None,
                migrated.FindMedia(migrated.RecentMediaIds[0])!.LastKnownKind);
            Assert.False(File.Exists(selectedPath));
            Assert.False(File.Exists(olderPath));

            var backupPath = Path.Combine(
                directoryPath,
                SettingsRepository.Version1BackupFileName);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backupPath));
            Assert.True(File.GetAttributes(backupPath).HasFlag(FileAttributes.ReadOnly));
            using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(settingsPath)))
            {
                Assert.Equal(
                    SettingsV2.CurrentSchemaVersion,
                    document.RootElement.GetProperty("schemaVersion").GetInt32());
            }

            var secondLoad = Assert.IsType<SettingsLoadResult.Ready>(
                await repository.LoadAsync());
            Assert.False(secondLoad.MigratedFromVersion1);
            Assert.Equal(profile.ProfileId, Assert.Single(secondLoad.Settings.Profiles).ProfileId);
            Assert.Equal(
                migrated.MediaCatalog.Select(media => media.MediaId),
                secondLoad.Settings.MediaCatalog.Select(media => media.MediaId));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncAtomicallyReplacesAnExistingV2AndCleansTemporaryFiles()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            using var repository = new SettingsRepository(settingsPath);
            var original = await repository.SaveAsync(SettingsV2.CreateDefault());
            var replacement = original with
            {
                AcceptedCdpRisk = true,
                LastCompatibilityProfileId = "replacement-profile",
            };

            await repository.SaveAsync(replacement);

            var loaded = Assert.IsType<SettingsLoadResult.Ready>(
                await repository.LoadAsync()).Settings;
            Assert.True(loaded.AcceptedCdpRisk);
            Assert.Equal("replacement-profile", loaded.LastCompatibilityProfileId);
            Assert.Empty(
                Directory.GetFiles(
                    directoryPath,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly));
            using var document = JsonDocument.Parse(
                await File.ReadAllBytesAsync(settingsPath));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task MigrationDoesNotAddSelectedMediaToLegacyRecents()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var selectedPath = Path.Combine(directoryPath, "selected.png");
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var version1 = SettingsV1.CreateDefault() with
            {
                MediaPath = selectedPath,
                MediaKind = MediaKind.Image,
                RecentMediaPaths = [],
            };
            await File.WriteAllBytesAsync(settingsPath, SerializeVersion1(version1));
            using var repository = new SettingsRepository(settingsPath);

            var migrated = Assert.IsType<SettingsLoadResult.Ready>(
                await repository.LoadAsync()).Settings;

            Assert.Single(migrated.MediaCatalog);
            Assert.Empty(migrated.RecentMediaIds);
            Assert.NotNull(Assert.Single(migrated.Profiles).MediaId);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task MigrationDeduplicatesRecentPathAliasesAfterNormalization()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var canonicalPath = Path.Combine(directoryPath, "wallpaper.png");
            var aliasPath = Path.Combine(directoryPath, "unused", "..", "wallpaper.png");
            var version1 = SettingsV1.CreateDefault() with
            {
                RecentMediaPaths = [aliasPath, canonicalPath],
            };
            await File.WriteAllBytesAsync(settingsPath, SerializeVersion1(version1));
            using var repository = new SettingsRepository(settingsPath);

            var migrated = Assert.IsType<SettingsLoadResult.Ready>(
                await repository.LoadAsync()).Settings;

            var media = Assert.Single(migrated.MediaCatalog);
            Assert.Equal(canonicalPath, media.SourceIdentifier);
            Assert.Equal([media.MediaId], migrated.RecentMediaIds);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task MigrationReusesAnExactExistingBackupAndMakesItReadOnly()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var backupPath = Path.Combine(
                directoryPath,
                SettingsRepository.Version1BackupFileName);
            var originalBytes = SerializeVersion1(SettingsV1.CreateDefault());
            await File.WriteAllBytesAsync(settingsPath, originalBytes);
            await File.WriteAllBytesAsync(backupPath, originalBytes);
            using var repository = new SettingsRepository(settingsPath);

            var result = await repository.LoadAsync();

            Assert.IsType<SettingsLoadResult.Ready>(result);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backupPath));
            Assert.True(File.GetAttributes(backupPath).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task MigrationRefusesADifferentExistingBackupWithoutChangingEitherFile()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var backupPath = Path.Combine(
                directoryPath,
                SettingsRepository.Version1BackupFileName);
            var originalBytes = SerializeVersion1(SettingsV1.CreateDefault() with
            {
                AcceptedCdpRisk = true,
            });
            var conflictingBytes = Encoding.UTF8.GetBytes("{\"different\":true}");
            await File.WriteAllBytesAsync(settingsPath, originalBytes);
            await File.WriteAllBytesAsync(backupPath, conflictingBytes);
            using var repository = new SettingsRepository(settingsPath);

            var result = await repository.LoadAsync();

            var recovery = Assert.IsType<SettingsLoadResult.RecoveryRequired>(result);
            Assert.Equal(SettingsRecoveryReason.Version1BackupConflict, recovery.Reason);
            Assert.True(recovery.HasVersion1Backup);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(settingsPath));
            Assert.Equal(conflictingBytes, await File.ReadAllBytesAsync(backupPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task MigrationFailureKeepsV1AndDoesNotPublishV2()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var originalBytes = SerializeVersion1(SettingsV1.CreateDefault());
            await File.WriteAllBytesAsync(settingsPath, originalBytes);
            Directory.CreateDirectory(
                Path.Combine(directoryPath, SettingsRepository.Version1BackupFileName));
            using var repository = new SettingsRepository(settingsPath);

            var result = await repository.LoadAsync();

            var recovery = Assert.IsType<SettingsLoadResult.RecoveryRequired>(result);
            Assert.Equal(SettingsRecoveryReason.MigrationFailed, recovery.Reason);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(settingsPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task InvalidAndUnknownDocumentsRequireRecoveryAndAreNeverOverwritten()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            using var repository = new SettingsRepository(settingsPath);
            var invalidBytes = Encoding.UTF8.GetBytes("{ definitely not json }");
            await File.WriteAllBytesAsync(settingsPath, invalidBytes);

            var invalidResult = await repository.LoadAsync();

            var invalidRecovery =
                Assert.IsType<SettingsLoadResult.RecoveryRequired>(invalidResult);
            Assert.Equal(SettingsRecoveryReason.InvalidDocument, invalidRecovery.Reason);
            await Assert.ThrowsAsync<SettingsRepositoryException>(
                () => repository.SaveAsync(SettingsV2.CreateDefault()));
            Assert.Equal(invalidBytes, await File.ReadAllBytesAsync(settingsPath));

            var unknownBytes = Encoding.UTF8.GetBytes(
                """
                {
                  "schemaVersion": 2,
                  "profiles": [],
                  "mediaCatalog": [],
                  "recentMediaIds": [],
                  "regionBindings": {},
                  "acceptedCdpRisk": false,
                  "unexpected": true
                }
                """);
            await File.WriteAllBytesAsync(settingsPath, unknownBytes);

            var unknownResult = await repository.LoadAsync();

            Assert.Equal(
                SettingsRecoveryReason.InvalidDocument,
                Assert.IsType<SettingsLoadResult.RecoveryRequired>(unknownResult).Reason);
            Assert.Equal(unknownBytes, await File.ReadAllBytesAsync(settingsPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task FutureSchemaIsReadOnlyEvenWithUnknownFieldsAndCaseVariantEnvelope()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var backupPath = Path.Combine(
                directoryPath,
                SettingsRepository.Version1BackupFileName);
            var futureBytes = Encoding.UTF8.GetBytes(
                """
                {
                  "SchemaVersion": 99,
                  "futureShape": {
                    "anything": true
                  }
                }
                """);
            await File.WriteAllBytesAsync(settingsPath, futureBytes);
            await File.WriteAllBytesAsync(
                backupPath,
                SerializeVersion1(SettingsV1.CreateDefault()));
            File.SetAttributes(
                backupPath,
                File.GetAttributes(backupPath) | FileAttributes.ReadOnly);
            using var repository = new SettingsRepository(settingsPath);

            var result = await repository.LoadAsync();

            var future = Assert.IsType<SettingsLoadResult.FutureReadOnly>(result);
            Assert.Equal(99, future.SchemaVersion);
            Assert.True(future.HasVersion1Backup);
            await Assert.ThrowsAsync<SettingsRepositoryException>(
                () => repository.SaveAsync(SettingsV2.CreateDefault()));
            Assert.Equal(futureBytes, await File.ReadAllBytesAsync(settingsPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task OversizedDocumentRequiresRecoveryWithoutParsingOrOverwriting()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var oversized = new byte[SettingsRepository.MaximumDocumentBytes + 1];
            await File.WriteAllBytesAsync(settingsPath, oversized);
            using var repository = new SettingsRepository(settingsPath);

            var result = await repository.LoadAsync();

            Assert.Equal(
                SettingsRecoveryReason.DocumentTooLarge,
                Assert.IsType<SettingsLoadResult.RecoveryRequired>(result).Reason);
            Assert.Equal(oversized.Length, new FileInfo(settingsPath).Length);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task StrictV1ValidationRunsBeforeBackupCreation()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var invalidV1 = Encoding.UTF8.GetBytes(
                """
                {
                  "schemaVersion": 1,
                  "unexpected": true
                }
                """);
            await File.WriteAllBytesAsync(settingsPath, invalidV1);
            using var repository = new SettingsRepository(settingsPath);

            var result = await repository.LoadAsync();

            Assert.Equal(
                SettingsRecoveryReason.InvalidDocument,
                Assert.IsType<SettingsLoadResult.RecoveryRequired>(result).Reason);
            Assert.False(
                File.Exists(
                    Path.Combine(
                        directoryPath,
                        SettingsRepository.Version1BackupFileName)));
            Assert.Equal(invalidV1, await File.ReadAllBytesAsync(settingsPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task RestoreVersion1BackupIsExplicitAndLeavesBackupUntouched()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var backupPath = Path.Combine(
                directoryPath,
                SettingsRepository.Version1BackupFileName);
            var backupBytes = SerializeVersion1(SettingsV1.CreateDefault() with
            {
                AcceptedCdpRisk = true,
                LastCompatibilityProfileId = "backup-profile",
            });
            await File.WriteAllTextAsync(settingsPath, "{ corrupt }");
            await File.WriteAllBytesAsync(backupPath, backupBytes);
            File.SetAttributes(
                backupPath,
                File.GetAttributes(backupPath) | FileAttributes.ReadOnly);
            using var repository = new SettingsRepository(settingsPath);

            var result = await repository.RestoreVersion1BackupAsync();

            var ready = Assert.IsType<SettingsLoadResult.Ready>(result);
            Assert.True(ready.MigratedFromVersion1);
            Assert.True(ready.Settings.AcceptedCdpRisk);
            Assert.Equal("backup-profile", ready.Settings.LastCompatibilityProfileId);
            Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backupPath));
            Assert.True(File.GetAttributes(backupPath).HasFlag(FileAttributes.ReadOnly));
            Assert.IsType<SettingsLoadResult.Ready>(await repository.LoadAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task ResetAsyncDeletesV2AndReadOnlyBackupButDoesNotPersistDefaults()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var backupPath = Path.Combine(
                directoryPath,
                SettingsRepository.Version1BackupFileName);
            using var repository = new SettingsRepository(settingsPath);
            await repository.SaveAsync(SettingsV2.CreateDefault());
            await File.WriteAllTextAsync(backupPath, "backup");
            File.SetAttributes(
                backupPath,
                File.GetAttributes(backupPath) | FileAttributes.ReadOnly);

            var defaults = await repository.ResetAsync();

            defaults.Validate();
            Assert.False(File.Exists(settingsPath));
            Assert.False(File.Exists(backupPath));
            Assert.Equal(
                7,
                Assert.Single(defaults.Profiles).ProfileId.Version);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task ResetAsyncPreservesBackupWhenSettingsDocumentCannotBeDeleted()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var backupPath = Path.Combine(
                directoryPath,
                SettingsRepository.Version1BackupFileName);
            await File.WriteAllTextAsync(settingsPath, "{\"schemaVersion\":2}");
            await File.WriteAllTextAsync(backupPath, "preserved-backup");
            File.SetAttributes(
                backupPath,
                File.GetAttributes(backupPath) | FileAttributes.ReadOnly);
            using var settingsLock = new FileStream(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            using var repository = CreateRepository(directoryPath);

            await Assert.ThrowsAsync<SettingsRepositoryException>(
                () => repository.ResetAsync());

            Assert.True(File.Exists(settingsPath));
            Assert.True(File.Exists(backupPath));
            Assert.Equal("preserved-backup", await File.ReadAllTextAsync(backupPath));
            Assert.True(File.GetAttributes(backupPath).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task ResetAsyncRestoresReadOnlyAttributeWhenBackupDeletionFails()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var backupPath = Path.Combine(
                directoryPath,
                SettingsRepository.Version1BackupFileName);
            using var repository = new SettingsRepository(settingsPath);
            await repository.SaveAsync(SettingsV2.CreateDefault());
            await File.WriteAllTextAsync(backupPath, "preserved-backup");
            File.SetAttributes(
                backupPath,
                File.GetAttributes(backupPath) | FileAttributes.ReadOnly);
            using var backupLock = new FileStream(
                backupPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            await Assert.ThrowsAsync<SettingsRepositoryException>(
                () => repository.ResetAsync());

            Assert.False(File.Exists(settingsPath));
            Assert.True(File.Exists(backupPath));
            Assert.True(File.GetAttributes(backupPath).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncPreservesNativeV2OverlayValuesAcrossAllProfiles()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var global = WallpaperProfile.CreateDefault() with
            {
                DarkOverlay = 0.85,
                LightOverlay = 0.75,
            };
            var hidden = WallpaperProfile.CreateDefault("Hidden") with
            {
                DarkOverlay = 0.95,
                LightOverlay = 0.90,
            };
            var settings = new SettingsV2
            {
                Profiles = [global, hidden],
                RegionBindings = new Dictionary<SemanticRegion, Guid>
                {
                    [SemanticRegion.Global] = global.ProfileId,
                    [SemanticRegion.Home] = hidden.ProfileId,
                },
            };
            using var repository = CreateRepository(directoryPath);

            await repository.SaveAsync(settings);
            var result = Assert.IsType<SettingsLoadResult.Ready>(
                await repository.LoadAsync());

            Assert.Equal(0.85, result.Settings.Profiles[0].DarkOverlay);
            Assert.Equal(0.75, result.Settings.Profiles[0].LightOverlay);
            Assert.Equal(0.95, result.Settings.Profiles[1].DarkOverlay);
            Assert.Equal(0.90, result.Settings.Profiles[1].LightOverlay);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncRejectsInvalidSettingsBeforeReplacingCurrentV2()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            using var repository = new SettingsRepository(settingsPath);
            await repository.SaveAsync(SettingsV2.CreateDefault());
            var originalBytes = await File.ReadAllBytesAsync(settingsPath);
            var invalid = SettingsV2.CreateDefault() with
            {
                Profiles = [],
            };

            await Assert.ThrowsAsync<SettingsValidationException>(
                () => repository.SaveAsync(invalid));

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(settingsPath));
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
    public async Task SaveAsyncRejectsAnOversizedCanonicalDocumentBeforePublication()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            using var repository = new SettingsRepository(settingsPath);
            var profiles = Enumerable.Range(0, 4_000)
                .Select(index => WallpaperProfile.CreateDefault(
                    $"{index:D4}-{new string('x', 100)}"))
                .ToArray();
            var oversized = new SettingsV2
            {
                Profiles = profiles,
                RegionBindings = new Dictionary<SemanticRegion, Guid>
                {
                    [SemanticRegion.Global] = profiles[0].ProfileId,
                },
            };

            await Assert.ThrowsAsync<SettingsRepositoryException>(
                () => repository.SaveAsync(oversized));

            Assert.False(File.Exists(settingsPath));
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
    public async Task DisposeDoesNotMaskAnAlreadyActiveSuccessfulSave()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            using var converter = new BlockingSettingsV2Converter();
            var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            serializerOptions.Converters.Add(converter);
            var repository = new SettingsRepository(settingsPath, serializerOptions);

            var saveTask = Task.Run(
                () => repository.SaveAsync(SettingsV2.CreateDefault()));
            Assert.True(converter.WriteEntered.Wait(TimeSpan.FromSeconds(5)));

            repository.Dispose();
            converter.AllowWrite.Set();

            var saved = await saveTask.WaitAsync(TimeSpan.FromSeconds(5));
            saved.Validate();
            Assert.True(File.Exists(settingsPath));
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => repository.LoadAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task SaveAsyncRefusesDocumentChangedAfterValidation()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            using (var initialRepository = new SettingsRepository(settingsPath))
            {
                await initialRepository.SaveAsync(SettingsV2.CreateDefault());
            }

            using var converter = new BlockingSettingsV2Converter();
            var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            serializerOptions.Converters.Add(converter);
            using var repository = new SettingsRepository(settingsPath, serializerOptions);
            var saveTask = Task.Run(
                () => repository.SaveAsync(
                    SettingsV2.CreateDefault() with { AcceptedCdpRisk = true }));
            Assert.True(converter.WriteEntered.Wait(TimeSpan.FromSeconds(5)));
            var futureBytes = Encoding.UTF8.GetBytes(
                """
                {
                  "schemaVersion": 99,
                  "futureShape": true
                }
                """);
            await File.WriteAllBytesAsync(settingsPath, futureBytes);

            converter.AllowWrite.Set();

            await Assert.ThrowsAsync<SettingsRepositoryException>(
                () => saveTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(futureBytes, await File.ReadAllBytesAsync(settingsPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task DuplicateSchemaEnvelopeRequiresRecovery()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            var duplicateEnvelope = Encoding.UTF8.GetBytes(
                """
                {
                  "schemaVersion": 2,
                  "SchemaVersion": 3
                }
                """);
            await File.WriteAllBytesAsync(settingsPath, duplicateEnvelope);
            using var repository = new SettingsRepository(settingsPath);

            var result = await repository.LoadAsync();

            Assert.Equal(
                SettingsRecoveryReason.InvalidDocument,
                Assert.IsType<SettingsLoadResult.RecoveryRequired>(result).Reason);
            Assert.Equal(duplicateEnvelope, await File.ReadAllBytesAsync(settingsPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    private static SettingsRepository CreateRepository(string directoryPath) =>
        new(Path.Combine(directoryPath, "settings.json"));

    private static byte[] SerializeVersion1(
        SettingsV1 settings,
        bool includeSchemaVersion = true)
    {
        settings.Validate();
        var json = JsonSerializer.Serialize(settings, Version1SerializerOptions);
        if (!includeSchemaVersion)
        {
            var line = $"  \"schemaVersion\": {SettingsV1.CurrentSchemaVersion},";
            json = json.Replace(
                $"{line}\r\n",
                string.Empty,
                StringComparison.Ordinal);
            json = json.Replace(
                $"{line}\n",
                string.Empty,
                StringComparison.Ordinal);
        }

        return Encoding.UTF8.GetBytes(json);
    }

    private static JsonSerializerOptions CreateVersion1SerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class BlockingSettingsV2Converter : JsonConverter<SettingsV2>, IDisposable
    {
        private static readonly JsonSerializerOptions PassthroughOptions =
            CreatePassthroughOptions();

        public ManualResetEventSlim WriteEntered { get; } = new(initialState: false);

        public ManualResetEventSlim AllowWrite { get; } = new(initialState: false);

        public override SettingsV2 Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return JsonSerializer.Deserialize<SettingsV2>(
                       document.RootElement.GetRawText(),
                       PassthroughOptions)
                   ?? throw new JsonException("The settings document is empty.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            SettingsV2 value,
            JsonSerializerOptions options)
        {
            WriteEntered.Set();
            if (!AllowWrite.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test did not release settings serialization.");
            }

            JsonSerializer.Serialize(writer, value, PassthroughOptions);
        }

        public void Dispose()
        {
            WriteEntered.Dispose();
            AllowWrite.Dispose();
        }

        private static JsonSerializerOptions CreatePassthroughOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            nameof(SettingsRepositoryTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

    private static void DeleteTemporaryDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(
                     directoryPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(filePath);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(directoryPath, recursive: true);
    }
}
