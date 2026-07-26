using System.Text.Json;
using System.Text.Json.Serialization;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Settings;

public sealed class SettingsV2CompatibilityTests
{
    private const string GoldenResourceSuffix =
        "Settings.Fixtures.settings-v1.3.5-schema2.json";

    private static readonly JsonSerializerOptions Version135Options =
        CreateVersion135Options();

    [Fact]
    public async Task Version135SchemaTwoGoldenFileIsBidirectionallyCompatible()
    {
        var goldenBytes = ReadGoldenBytes();
        var version135 = JsonSerializer.Deserialize<Version135Settings>(
            goldenBytes,
            Version135Options);
        Assert.NotNull(version135);

        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"BackdropForCodex-V2-compat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        try
        {
            var settingsPath = Path.Combine(directoryPath, "settings.json");
            await File.WriteAllBytesAsync(settingsPath, goldenBytes);
            using var repository = new SettingsRepository(settingsPath);

            var loadedResult = await repository.LoadAsync();
            var loaded = Assert.IsType<SettingsLoadResult.Ready>(loadedResult).Settings;

            Assert.Equal(2, loaded.Profiles.Count);
            Assert.Equal(
                "Hidden region",
                loaded.ResolveProfile(SemanticRegion.Conversation).Name);
            Assert.True(loaded.ResolveProfile(SemanticRegion.Global).SoundEnabled);
            Assert.Equal(
                PerformancePolicy.PreferQuality,
                loaded.ResolveProfile(SemanticRegion.Global).PerformancePolicy);
            var sharedMediaId =
                Guid.Parse("0198f1a2-5678-7abc-8def-1234567890ab");
            var orphanMediaId =
                Guid.Parse("0198f1a2-def0-7abc-8def-1234567890ab");
            Assert.All(
                loaded.Profiles,
                profile => Assert.Equal(sharedMediaId, profile.MediaId));
            Assert.Contains(
                loaded.MediaCatalog,
                media => media.MediaId == orphanMediaId);
            Assert.DoesNotContain(
                loaded.Profiles,
                profile => profile.MediaId == orphanMediaId);
            Assert.Equal(
                "stable-v1.3.5-marker",
                GetLastCompatibilityProfileId(loaded));

            await repository.SaveAsync(loaded);
            var version140Bytes = await File.ReadAllBytesAsync(settingsPath);

            var readableByVersion135 =
                JsonSerializer.Deserialize<Version135Settings>(
                    version140Bytes,
                    Version135Options);
            Assert.NotNull(readableByVersion135);
            Assert.Equal(version135.Profiles.Count, readableByVersion135.Profiles.Count);
            Assert.Equal(
                version135.LastCompatibilityProfileId,
                readableByVersion135.LastCompatibilityProfileId);
            Assert.Equal(2, readableByVersion135.MediaCatalog.Count);
            Assert.All(
                readableByVersion135.Profiles,
                profile => Assert.Equal(sharedMediaId, profile.MediaId));
            Assert.Contains(
                readableByVersion135.MediaCatalog,
                media => media.MediaId == orphanMediaId);

            var version135WriterBytes = JsonSerializer.SerializeToUtf8Bytes(
                readableByVersion135,
                Version135Options);
            await File.WriteAllBytesAsync(settingsPath, version135WriterBytes);

            var roundTripped = Assert.IsType<SettingsLoadResult.Ready>(
                await repository.LoadAsync()).Settings;
            Assert.True(SettingsV2Comparer.DurableEquals(loaded, roundTripped));
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static byte[] ReadGoldenBytes()
    {
        var assembly = typeof(SettingsV2CompatibilityTests).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .Single(name => name.EndsWith(
                GoldenResourceSuffix,
                StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                "The 1.3.5 schema-two golden fixture is missing.");
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static JsonSerializerOptions CreateVersion135Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

#pragma warning disable CS0618 // Compatibility test intentionally covers the deprecated field.
    private static string? GetLastCompatibilityProfileId(SettingsV2 settings) =>
        settings.LastCompatibilityProfileId;
#pragma warning restore CS0618

    private sealed class Version135Settings
    {
        public int SchemaVersion { get; set; }

        public List<Version135Profile> Profiles { get; set; } = [];

        public List<Version135MediaReference> MediaCatalog { get; set; } = [];

        public List<Guid> RecentMediaIds { get; set; } = [];

        public Dictionary<SemanticRegion, Guid> RegionBindings { get; set; } = [];

        public bool AcceptedCdpRisk { get; set; }

        public string? LastCompatibilityProfileId { get; set; }
    }

    private sealed class Version135Profile
    {
        public Guid ProfileId { get; set; }

        public string Name { get; set; } = string.Empty;

        public Guid? MediaId { get; set; }

        public WallpaperFit Fit { get; set; }

        public double FocusX { get; set; }

        public double FocusY { get; set; }

        public double PanelOpacity { get; set; }

        public double BlurPx { get; set; }

        public double DarkOverlay { get; set; }

        public double LightOverlay { get; set; }

        public bool SoundEnabled { get; set; }

        public double Volume { get; set; }

        public PerformancePolicy PerformancePolicy { get; set; }
    }

    private sealed class Version135MediaReference
    {
        public Guid MediaId { get; set; }

        public MediaSourceKind SourceKind { get; set; }

        public string SourceIdentifier { get; set; } = string.Empty;

        public MediaKind LastKnownKind { get; set; }
    }
}
