using System.Text.Json;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperEngineSourceProviderTests
{
    [Fact]
    public async Task LocalDiscoveryUsesOnlyMyProjectsAndBackupAndRejectsApplications()
    {
        using var fixture = new ProjectFixture();
        _ = fixture.CreateLocalProject(
            "myprojects",
            "video-project",
            "video",
            "movie.mp4",
            "My video",
            Mp4Bytes());
        _ = fixture.CreateLocalProject(
            "backup",
            "web-project",
            "web",
            "index.html",
            "My web",
            "<html></html>"u8.ToArray());
        _ = fixture.CreateLocalProject(
            "myprojects",
            "application-project",
            "application",
            "unsafe.exe",
            "Must not appear",
            "MZ"u8.ToArray());
        _ = fixture.CreateLocalProject(
            "defaultprojects",
            "default-scene",
            "scene",
            "scene.json",
            "Must not appear either",
            "{}"u8.ToArray());
        var provider = fixture.CreateLocalProvider();

        var descriptors = await provider.DiscoverAsync();

        Assert.Equal(2, descriptors.Count);
        Assert.Collection(
            descriptors.OrderBy(item => item.DisplayName, StringComparer.Ordinal),
            descriptor =>
            {
                Assert.Equal("My video", descriptor.DisplayName);
                Assert.Equal(WallpaperContentKind.Video, descriptor.ContentKind);
                Assert.Equal(WallpaperDeliveryKind.DirectMedia, descriptor.DeliveryKind);
            },
            descriptor =>
            {
                Assert.Equal("My web", descriptor.DisplayName);
                Assert.Equal(WallpaperContentKind.Web, descriptor.ContentKind);
                Assert.Equal(
                    WallpaperDeliveryKind.WallpaperEngineWindow,
                    descriptor.DeliveryKind);
            });
    }

    [Fact]
    public async Task ProjectTitlesCannotInjectControlOrBidirectionalFormattingCharacters()
    {
        using var fixture = new ProjectFixture();
        var projectRoot = fixture.CreateLocalProject(
            "myprojects",
            "safe-title",
            "video",
            "movie.mp4",
            "  壁纸\r\n\u0001\u202e 🌌安全\u0085\u2066  ",
            Mp4Bytes());
        var provider = fixture.CreateLocalProvider();

        var descriptor = Assert.Single(await provider.DiscoverAsync());
        var resolution = await provider.ResolveAsync(fixture.LocalReference(projectRoot));

        Assert.Equal("壁纸 🌌安全", descriptor.DisplayName);
        Assert.Equal(
            descriptor.DisplayName,
            resolution.CanonicalReference.LastKnownDisplayName);
    }

    [Fact]
    public async Task LocalDiscoveryRejectsMoreThanTheGlobalProjectLimitAcrossContainers()
    {
        using var fixture = new ProjectFixture();
        var firstContainerCount =
            WallpaperEngineProjectSourceProviderBase.MaximumDiscoveredProjects / 2;
        fixture.CreateEmptyLocalProjectDirectories("myprojects", firstContainerCount);
        fixture.CreateEmptyLocalProjectDirectories(
            "backup",
            WallpaperEngineProjectSourceProviderBase.MaximumDiscoveredProjects -
                firstContainerCount +
                1);
        var provider = fixture.CreateLocalProvider();

        var exception = await Assert.ThrowsAsync<WallpaperEngineProjectUnavailableException>(
            () => provider.DiscoverAsync().AsTask());

        Assert.Equal(
            WallpaperEngineProjectUnavailableReason.AmbiguousIdentity,
            exception.Reason);
    }

    [Fact]
    public async Task WorkshopDiscoveryUsesPublishedFileIdAcrossLibrariesAndRejectsDuplicates()
    {
        using var fixture = new ProjectFixture();
        _ = fixture.CreateWorkshopProject(
            fixture.PrimaryLibrary,
            100,
            "video",
            "movie.mp4",
            "Workshop video",
            Mp4Bytes());
        _ = fixture.CreateWorkshopProject(
            fixture.SecondaryLibrary,
            200,
            "web",
            "index.html",
            "Workshop web",
            "<html></html>"u8.ToArray());
        _ = fixture.CreateWorkshopProject(
            fixture.PrimaryLibrary,
            300,
            "video",
            "movie.mp4",
            "Duplicate A",
            Mp4Bytes());
        _ = fixture.CreateWorkshopProject(
            fixture.SecondaryLibrary,
            300,
            "video",
            "movie.mp4",
            "Duplicate B",
            Mp4Bytes());
        var provider = fixture.CreateWorkshopProvider();

        var descriptors = await provider.DiscoverAsync();
        var duplicate = await Assert.ThrowsAsync<WallpaperEngineProjectUnavailableException>(
            () => provider.ResolveAsync(fixture.WorkshopReference(300)).AsTask());

        Assert.Equal(["100", "200"], descriptors
            .Select(item => item.SourceIdentifier)
            .Order(StringComparer.Ordinal));
        Assert.All(
            descriptors,
            descriptor => Assert.Equal(
                MediaSourceKind.WallpaperEngineWorkshopProject,
                descriptor.SourceKind));
        Assert.Equal(
            WallpaperEngineProjectUnavailableReason.AmbiguousIdentity,
            duplicate.Reason);
    }

    [Fact]
    public async Task WorkshopDiscoveryRejectsMoreThanTheGlobalProjectLimitAcrossLibraries()
    {
        using var fixture = new ProjectFixture();
        var firstLibraryCount =
            WallpaperEngineProjectSourceProviderBase.MaximumDiscoveredProjects / 2;
        fixture.CreateEmptyWorkshopProjectDirectories(
            fixture.PrimaryLibrary,
            firstPublishedFileId: 1,
            firstLibraryCount);
        fixture.CreateEmptyWorkshopProjectDirectories(
            fixture.SecondaryLibrary,
            firstPublishedFileId: 100_000,
            WallpaperEngineProjectSourceProviderBase.MaximumDiscoveredProjects -
                firstLibraryCount +
                1);
        var provider = fixture.CreateWorkshopProvider();

        var exception = await Assert.ThrowsAsync<WallpaperEngineProjectUnavailableException>(
            () => provider.DiscoverAsync().AsTask());

        Assert.Equal(
            WallpaperEngineProjectUnavailableReason.AmbiguousIdentity,
            exception.Reason);
    }

    [Fact]
    public async Task VideoLeasePreservesProjectIdentityAndPinsManifestAndMedia()
    {
        using var fixture = new ProjectFixture();
        var projectRoot = fixture.CreateWorkshopProject(
            fixture.SecondaryLibrary,
            987654321,
            "video",
            "movie.mp4",
            "Pinned video",
            Mp4Bytes());
        var provider = fixture.CreateWorkshopProvider();
        var reference = fixture.WorkshopReference(987654321);

        var lease = await provider.AcquireDirectMediaLeaseAsync(reference);
        try
        {
            Assert.Equal(
                MediaSourceKind.WallpaperEngineWorkshopProject,
                lease.Reference.SourceKind);
            Assert.Equal("987654321", lease.Reference.SourceIdentifier);
            Assert.Equal(MediaKind.Video, lease.Reference.LastKnownKind);
            Assert.Equal(
                WallpaperContentKind.Video,
                lease.Reference.LastKnownContentKind);
            Assert.Equal("Pinned video", lease.Reference.LastKnownDisplayName);
            Assert.Equal(MediaFormat.Mp4, lease.Metadata.Format);
            Assert.DoesNotContain(
                projectRoot,
                lease.ToString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Throws<IOException>(() => File.Delete(Path.Combine(projectRoot, "movie.mp4")));
            Assert.Throws<IOException>(() => File.Delete(Path.Combine(projectRoot, "project.json")));
        }
        finally
        {
            await lease.DisposeAsync();
        }

        File.Delete(Path.Combine(projectRoot, "movie.mp4"));
        File.Delete(Path.Combine(projectRoot, "project.json"));
    }

    [Fact]
    public async Task SceneLeasesUseProjectJsonAndUniqueWorkshopPackageFallback()
    {
        using var fixture = new ProjectFixture();
        var localRoot = fixture.CreateLocalProject(
            "myprojects",
            "editable-scene",
            "scene",
            "scene.json",
            "Editable scene",
            "{}"u8.ToArray());
        var workshopRoot = fixture.CreateWorkshopProject(
            fixture.SecondaryLibrary,
            444,
            "scene",
            "scene.json",
            "Packaged scene",
            entryBytes: null);
        await File.WriteAllBytesAsync(
            Path.Combine(workshopRoot, "scene.pkg"),
            "package"u8.ToArray());

        await using var localLease = await fixture.CreateLocalProvider()
            .AcquireProjectLeaseAsync(fixture.LocalReference(localRoot));
        await using var workshopLease = await fixture.CreateWorkshopProvider()
            .AcquireProjectLeaseAsync(fixture.WorkshopReference(444));

        Assert.Equal(
            Path.Combine(localRoot, "project.json"),
            localLease.LaunchPath,
            ignoreCase: true);
        Assert.Equal(
            Path.Combine(workshopRoot, "scene.pkg"),
            workshopLease.LaunchPath,
            ignoreCase: true);
        WallpaperEngineProjectLeaseContract.Validate(localLease);
        WallpaperEngineProjectLeaseContract.Validate(workshopLease);
    }

    [Fact]
    public async Task ProjectLeaseRetriesFailedProjectCleanupWithoutRepeatingEntryCleanup()
    {
        using var fixture = new ProjectFixture();
        var projectRoot = fixture.CreateLocalProject(
            "myprojects",
            "retryable-scene",
            "scene",
            "scene.json",
            "Retryable scene",
            "{}"u8.ToArray());
        var resolution = await fixture.CreateLocalProvider()
            .ResolveAsync(fixture.LocalReference(projectRoot));
        var projectCleanup = new FailOnceAsyncDisposable();
        var entryCleanup = new RecordingDisposable();
        var lease = new WallpaperEngineProjectSourceProviderBase.WallpaperEngineProjectLease(
            resolution,
            Path.Combine(projectRoot, "project.json"),
            projectCleanup,
            entryCleanup);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lease.DisposeAsync().AsTask());

        Assert.Equal(1, projectCleanup.DisposeAttempts);
        Assert.Equal(1, entryCleanup.DisposeAttempts);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(2, projectCleanup.DisposeAttempts);
        Assert.Equal(1, entryCleanup.DisposeAttempts);
    }

    [Fact]
    public async Task ProjectLeaseRetriesFailedEntryCleanupWithoutRepeatingProjectCleanup()
    {
        using var fixture = new ProjectFixture();
        var projectRoot = fixture.CreateLocalProject(
            "myprojects",
            "retryable-entry-scene",
            "scene",
            "scene.json",
            "Retryable entry scene",
            "{}"u8.ToArray());
        var resolution = await fixture.CreateLocalProvider()
            .ResolveAsync(fixture.LocalReference(projectRoot));
        var projectCleanup = new RecordingAsyncDisposable();
        var entryCleanup = new FailOnceDisposable();
        var lease = new WallpaperEngineProjectSourceProviderBase.WallpaperEngineProjectLease(
            resolution,
            Path.Combine(projectRoot, "project.json"),
            projectCleanup,
            entryCleanup);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lease.DisposeAsync().AsTask());

        Assert.Equal(1, projectCleanup.DisposeAttempts);
        Assert.Equal(1, entryCleanup.DisposeAttempts);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(1, projectCleanup.DisposeAttempts);
        Assert.Equal(2, entryCleanup.DisposeAttempts);
    }

    [Fact]
    public async Task PinnedManifestRetriesFailedStreamCleanupUntilSuccess()
    {
        var stream = new FailOnceDisposeStream();
        var pinned = new PinnedWallpaperEngineProjectManifest(
            @"C:\WallpaperEngine\projects\wallpaper",
            @"C:\WallpaperEngine\projects\wallpaper\project.json",
            new WallpaperEngineProjectManifest(
                "Retryable scene",
                WallpaperContentKind.Scene,
                "scene.json",
                PreviewRelativePath: null),
            stream);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pinned.DisposeAsync().AsTask());
        Assert.Equal(1, stream.DisposeAttempts);

        await pinned.DisposeAsync();
        await pinned.DisposeAsync();

        Assert.Equal(2, stream.DisposeAttempts);
    }

    [Fact]
    public async Task SceneFallbackRejectsAmbiguousPackagesAndWebUsesMainHtml()
    {
        using var fixture = new ProjectFixture();
        var ambiguousRoot = fixture.CreateWorkshopProject(
            fixture.PrimaryLibrary,
            555,
            "scene",
            "scene.json",
            "Ambiguous scene",
            entryBytes: null);
        await File.WriteAllBytesAsync(Path.Combine(ambiguousRoot, "one.pkg"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(ambiguousRoot, "two.pkg"), [2]);
        var webRoot = fixture.CreateWorkshopProject(
            fixture.SecondaryLibrary,
            556,
            "web",
            "site/index.html",
            "Web project",
            "<html></html>"u8.ToArray());
        var provider = fixture.CreateWorkshopProvider();

        var ambiguous = await Assert.ThrowsAsync<WallpaperEngineProjectUnavailableException>(
            () => provider.AcquireProjectLeaseAsync(
                fixture.WorkshopReference(555)).AsTask());
        await using var webLease = await provider.AcquireProjectLeaseAsync(
            fixture.WorkshopReference(556));

        Assert.Equal(
            WallpaperEngineProjectUnavailableReason.AmbiguousScenePackage,
            ambiguous.Reason);
        Assert.Equal(
            Path.Combine(webRoot, "site", "index.html"),
            webLease.LaunchPath,
            ignoreCase: true);
        Assert.Equal(WallpaperContentKind.Web, webLease.Resolution.Descriptor.ContentKind);
    }

    [Fact]
    public async Task MissingSceneFallbackIsTypedAndDoesNotHideValidNeighbours()
    {
        using var fixture = new ProjectFixture();
        _ = fixture.CreateWorkshopProject(
            fixture.PrimaryLibrary,
            557,
            "scene",
            "scene.json",
            "Missing scene package",
            entryBytes: null);
        _ = fixture.CreateWorkshopProject(
            fixture.PrimaryLibrary,
            558,
            "video",
            "movie.mp4",
            "Valid neighbour",
            Mp4Bytes());
        var provider = fixture.CreateWorkshopProvider();

        var descriptors = await provider.DiscoverAsync();
        var exception = await Assert.ThrowsAsync<WallpaperEngineProjectUnavailableException>(
            () => provider.ResolveAsync(fixture.WorkshopReference(557)).AsTask());

        Assert.Equal("Valid neighbour", Assert.Single(descriptors).DisplayName);
        Assert.Equal(
            WallpaperEngineProjectUnavailableReason.EntryPointMissing,
            exception.Reason);
    }

    [Fact]
    public void SceneFallbackDirectoryDisappearanceIsTypedUnavailable()
    {
        var missingRoot = Path.Combine(
            Path.GetTempPath(),
            $"BackdropForCodex-missing-scene-{Guid.NewGuid():N}");

        var exception = Assert.Throws<WallpaperEngineProjectUnavailableException>(
            () => WallpaperEngineProjectSourceProviderBase.FindUniqueScenePackage(
                missingRoot,
                MediaSourceKind.WallpaperEngineWorkshopProject));

        Assert.Equal(
            WallpaperEngineProjectUnavailableReason.EntryPointMissing,
            exception.Reason);
        Assert.DoesNotContain(missingRoot, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplicationAndUnknownProjectsNeverProduceUsableLeases()
    {
        using var fixture = new ProjectFixture();
        var applicationRoot = fixture.CreateLocalProject(
            "myprojects",
            "application",
            "application",
            "program.exe",
            "Application",
            "MZ"u8.ToArray());
        var unknownRoot = fixture.CreateLocalProject(
            "myprojects",
            "unknown",
            "future-kind",
            "content.bin",
            "Unknown",
            [1, 2, 3]);
        var provider = fixture.CreateLocalProvider();

        await Assert.ThrowsAsync<WallpaperContentNotSupportedException>(
            () => provider.ResolveAsync(fixture.LocalReference(applicationRoot)).AsTask());
        await Assert.ThrowsAsync<WallpaperContentNotSupportedException>(
            () => provider.ResolveAsync(fixture.LocalReference(unknownRoot)).AsTask());
        await Assert.ThrowsAsync<WallpaperContentNotSupportedException>(
            () => provider.AcquireProjectLeaseAsync(
                fixture.LocalReference(applicationRoot)).AsTask());
    }

    [Fact]
    public async Task ProjectEntryTraversalAndReparseTargetsFailClosed()
    {
        using var fixture = new ProjectFixture();
        var outsidePath = Path.Combine(fixture.RootPath, "outside.mp4");
        await File.WriteAllBytesAsync(outsidePath, Mp4Bytes());
        var traversalRoot = fixture.CreateLocalProject(
            "myprojects",
            "traversal",
            "video",
            @"..\..\..\outside.mp4",
            "Traversal",
            entryBytes: null);
        var provider = fixture.CreateLocalProvider();

        var traversal = await Assert.ThrowsAsync<WallpaperEngineProjectUnavailableException>(
            () => provider.ResolveAsync(fixture.LocalReference(traversalRoot)).AsTask());
        Assert.Equal(
            WallpaperEngineProjectUnavailableReason.UnsafePath,
            traversal.Reason);

        var networkReference = fixture.LocalReference(@"\\server\share\project");
        var network = await Assert.ThrowsAsync<WallpaperEngineProjectUnavailableException>(
            () => provider.ResolveAsync(networkReference).AsTask());
        Assert.Equal(WallpaperEngineProjectUnavailableReason.UnsafePath, network.Reason);

        var reparseRoot = fixture.CreateLocalProject(
            "myprojects",
            "reparse",
            "video",
            "linked.mp4",
            "Reparse",
            entryBytes: null);
        try
        {
            File.CreateSymbolicLink(Path.Combine(reparseRoot, "linked.mp4"), outsidePath);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var reparse = await Assert.ThrowsAsync<WallpaperEngineProjectUnavailableException>(
            () => provider.ResolveAsync(fixture.LocalReference(reparseRoot)).AsTask());
        Assert.Equal(WallpaperEngineProjectUnavailableReason.UnsafePath, reparse.Reason);
    }

    [Fact]
    public async Task ThumbnailLeaseSupportsGifWithoutExposingProviderPath()
    {
        using var fixture = new ProjectFixture();
        var projectRoot = fixture.CreateWorkshopProject(
            fixture.PrimaryLibrary,
            777,
            "video",
            "movie.mp4",
            "Thumbnail project",
            Mp4Bytes(),
            previewRelativePath: "preview.gif");
        var previewPath = Path.Combine(projectRoot, "preview.gif");
        await File.WriteAllBytesAsync(previewPath, "GIF89a fixture"u8.ToArray());
        var provider = fixture.CreateWorkshopProvider();

        var lease = await provider.AcquireThumbnailLeaseAsync(
            fixture.WorkshopReference(777));
        Assert.NotNull(lease);
        await using (lease)
        {
            Assert.Equal("image/gif", lease.ContentType);
            Assert.True(lease.ContentStream.CanRead);
            Assert.True(lease.ContentStream.CanSeek);
            Assert.DoesNotContain(
                lease.GetType().GetProperties(),
                property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                projectRoot,
                lease.ToString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Throws<IOException>(() => File.Delete(previewPath));
        }

        File.Delete(previewPath);
    }

    [Fact]
    public async Task OversizedJsonIsSkippedDuringDiscoveryAndFailsTypedResolution()
    {
        using var fixture = new ProjectFixture();
        var projectRoot = fixture.CreateLocalProject(
            "myprojects",
            "oversized",
            "video",
            "movie.mp4",
            "Oversized",
            Mp4Bytes());
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "project.json"),
            new string(' ', 256 * 1024 + 1));
        var provider = fixture.CreateLocalProvider();

        var discovered = await provider.DiscoverAsync();
        var exception = await Assert.ThrowsAsync<WallpaperEngineProjectUnavailableException>(
            () => provider.ResolveAsync(fixture.LocalReference(projectRoot)).AsTask());

        Assert.Empty(discovered);
        Assert.Equal(
            WallpaperEngineProjectUnavailableReason.InvalidManifest,
            exception.Reason);
    }

    [Theory]
    [MemberData(nameof(InvalidManifestDocuments))]
    public async Task InvalidManifestDoesNotHideValidNeighboursAndFailsTypedResolution(
        string scenario,
        string invalidDocument)
    {
        using var fixture = new ProjectFixture();
        var invalidRoot = fixture.CreateLocalProject(
            "myprojects",
            $"invalid-{scenario}",
            "video",
            "movie.mp4",
            "Invalid manifest",
            Mp4Bytes());
        await File.WriteAllTextAsync(
            Path.Combine(invalidRoot, "project.json"),
            invalidDocument);
        _ = fixture.CreateLocalProject(
            "myprojects",
            $"valid-neighbour-{scenario}",
            "video",
            "movie.mp4",
            "Valid neighbour",
            Mp4Bytes());
        var provider = fixture.CreateLocalProvider();

        var descriptors = await provider.DiscoverAsync();
        var exception = await Assert.ThrowsAsync<WallpaperEngineProjectUnavailableException>(
            () => provider.ResolveAsync(fixture.LocalReference(invalidRoot)).AsTask());

        Assert.Equal("Valid neighbour", Assert.Single(descriptors).DisplayName);
        Assert.Equal(
            WallpaperEngineProjectUnavailableReason.InvalidManifest,
            exception.Reason);
    }

    [Fact]
    public void ProjectUnavailableExceptionRedactsProviderPathsFromFullDiagnosticText()
    {
        const string sensitivePath = @"C:\\SensitiveUser\\SteamLibrary\\project.json";
        var exception = new WallpaperEngineProjectUnavailableException(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            WallpaperEngineProjectUnavailableReason.InvalidManifest,
            new IOException($"Could not read '{sensitivePath}'."));

        Assert.DoesNotContain(sensitivePath, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(exception.InnerException);
        Assert.Equal(nameof(IOException), exception.DiagnosticExceptionType);
        Assert.NotEqual(0, exception.DiagnosticHResult);
    }

    public static TheoryData<string, string> InvalidManifestDocuments => new()
    {
        { "malformed", "not-json" },
        { "truncated", """{"type":"video""" },
        {
            "wrong-known-field-type",
            """{"type":"video","title":42,"file":"movie.mp4"}"""
        },
        {
            "case-variant-duplicate",
            """{"type":"video","Type":"web","title":"Duplicate","file":"movie.mp4"}"""
        },
        {
            "maximum-depth",
            CreateExcessivelyDeepManifest()
        },
        {
            "maximum-string-length",
            JsonSerializer.Serialize(new
            {
                type = "video",
                title = new string('x', WallpaperSourceDescriptor.MaximumDisplayNameLength + 1),
                file = "movie.mp4",
            })
        },
    };

    private static string CreateExcessivelyDeepManifest()
    {
        const int depth = WallpaperEngineProjectManifestReader.MaximumDepth + 1;
        return """{"type":"video","title":"Deep","file":"movie.mp4","future":""" +
            new string('[', depth) +
            "0" +
            new string(']', depth) +
            "}";
    }

    private static byte[] Mp4Bytes() =>
    [
        0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70,
        0x69, 0x73, 0x6F, 0x6D, 0x00, 0x00, 0x00, 0x00,
        0x69, 0x73, 0x6F, 0x6D,
    ];

    private sealed class FixedInstallationLocator(WallpaperEngineInstallation installation)
        : IWallpaperEngineInstallationLocator
    {
        public ValueTask<WallpaperEngineInstallation> LocateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(installation);
        }
    }

    private sealed class FailOnceAsyncDisposable : IAsyncDisposable
    {
        public int DisposeAttempts { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeAttempts++;
            return DisposeAttempts == 1
                ? ValueTask.FromException(
                    new InvalidOperationException("Synthetic project cleanup failure."))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDisposable : IDisposable
    {
        public int DisposeAttempts { get; private set; }

        public void Dispose() => DisposeAttempts++;
    }

    private sealed class RecordingAsyncDisposable : IAsyncDisposable
    {
        public int DisposeAttempts { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeAttempts++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceDisposable : IDisposable
    {
        public int DisposeAttempts { get; private set; }

        public void Dispose()
        {
            DisposeAttempts++;
            if (DisposeAttempts == 1)
            {
                throw new InvalidOperationException("Synthetic entry cleanup failure.");
            }
        }
    }

    private sealed class FailOnceDisposeStream : MemoryStream
    {
        public int DisposeAttempts { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeAttempts++;
            if (DisposeAttempts == 1)
            {
                throw new InvalidOperationException("Synthetic manifest cleanup failure.");
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ProjectFixture : IDisposable
    {
        private readonly FixedInstallationLocator _locator;

        public ProjectFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "BackdropForCodex.Core.Tests",
                Guid.NewGuid().ToString("N"));
            PrimaryLibrary = Path.Combine(RootPath, "PrimaryLibrary");
            SecondaryLibrary = Path.Combine(RootPath, "SecondaryLibrary");
            InstallRoot = Path.Combine(
                PrimaryLibrary,
                "steamapps",
                "common",
                "wallpaper_engine");
            Directory.CreateDirectory(InstallRoot);
            Directory.CreateDirectory(SecondaryLibrary);
            var executablePath = Path.Combine(InstallRoot, "wallpaper64.exe");
            File.WriteAllText(executablePath, "fixture");
            _locator = new FixedInstallationLocator(
                new WallpaperEngineInstallation(
                    PrimaryLibrary,
                    InstallRoot,
                    executablePath,
                    [PrimaryLibrary, SecondaryLibrary]));
        }

        public string RootPath { get; }

        public string PrimaryLibrary { get; }

        public string SecondaryLibrary { get; }

        public string InstallRoot { get; }

        public WallpaperEngineLocalProjectSourceProvider CreateLocalProvider() =>
            new(_locator);

        public WallpaperEngineWorkshopProjectSourceProvider CreateWorkshopProvider() =>
            new(_locator);

        public string CreateLocalProject(
            string container,
            string projectName,
            string type,
            string entryRelativePath,
            string title,
            byte[]? entryBytes,
            string? previewRelativePath = null)
        {
            var projectRoot = Path.Combine(
                InstallRoot,
                "projects",
                container,
                projectName);
            return CreateProject(
                projectRoot,
                type,
                entryRelativePath,
                title,
                entryBytes,
                previewRelativePath);
        }

        public void CreateEmptyLocalProjectDirectories(string container, int count)
        {
            EnsureActive();
            var containerRoot = Path.Combine(InstallRoot, "projects", container);
            for (var index = 0; index < count; index++)
            {
                Directory.CreateDirectory(Path.Combine(
                    containerRoot,
                    $"empty-{index:D5}"));
            }
        }

        public string CreateWorkshopProject(
            string library,
            ulong publishedFileId,
            string type,
            string entryRelativePath,
            string title,
            byte[]? entryBytes,
            string? previewRelativePath = null)
        {
            EnsureActive();
            var projectRoot = Path.Combine(
                library,
                "steamapps",
                "workshop",
                "content",
                "431960",
                publishedFileId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return CreateProject(
                projectRoot,
                type,
                entryRelativePath,
                title,
                entryBytes,
                previewRelativePath);
        }

        public void CreateEmptyWorkshopProjectDirectories(
            string library,
            ulong firstPublishedFileId,
            int count)
        {
            EnsureActive();
            var contentRoot = Path.Combine(
                library,
                "steamapps",
                "workshop",
                "content",
                "431960");
            for (var index = 0; index < count; index++)
            {
                Directory.CreateDirectory(Path.Combine(
                    contentRoot,
                    (firstPublishedFileId + (ulong)index).ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        public MediaReference LocalReference(string projectRoot)
        {
            EnsureActive();
            return new MediaReference
            {
                MediaId = Guid.CreateVersion7(),
                SourceKind = MediaSourceKind.WallpaperEngineLocalProject,
                SourceIdentifier = projectRoot,
                LastKnownKind = MediaKind.None,
                LastKnownContentKind = WallpaperContentKind.Unknown,
            };
        }

        public MediaReference WorkshopReference(ulong publishedFileId)
        {
            EnsureActive();
            return new MediaReference
            {
                MediaId = Guid.CreateVersion7(),
                SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
                SourceIdentifier = publishedFileId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                LastKnownKind = MediaKind.None,
                LastKnownContentKind = WallpaperContentKind.Unknown,
            };
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        private void EnsureActive() =>
            ObjectDisposedException.ThrowIf(!Directory.Exists(RootPath), this);

        private static string CreateProject(
            string projectRoot,
            string type,
            string entryRelativePath,
            string title,
            byte[]? entryBytes,
            string? previewRelativePath)
        {
            Directory.CreateDirectory(projectRoot);
            var manifest = new Dictionary<string, object?>
            {
                ["type"] = type,
                ["title"] = title,
                ["file"] = entryRelativePath,
                ["preview"] = previewRelativePath,
                ["future"] = new
                {
                    nested = new[] { 1, 2, 3 },
                    ignored = true,
                },
            };
            File.WriteAllText(
                Path.Combine(projectRoot, "project.json"),
                JsonSerializer.Serialize(manifest));
            if (entryBytes is not null)
            {
                var entryPath = Path.GetFullPath(Path.Combine(projectRoot, entryRelativePath));
                Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
                File.WriteAllBytes(entryPath, entryBytes);
            }

            return Path.GetFullPath(projectRoot);
        }
    }
}
