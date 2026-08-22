namespace BackdropForCodex.Core.Media;

public sealed class WallpaperEngineLocalProjectSourceProvider
    : WallpaperEngineProjectSourceProviderBase
{
    public WallpaperEngineLocalProjectSourceProvider(
        IWallpaperEngineInstallationLocator? installationLocator = null,
        IMediaStreamInspector? mediaInspector = null)
        : base(
            MediaSourceKind.WallpaperEngineLocalProject,
            installationLocator ?? new WallpaperEngineInstallationLocator(),
            mediaInspector)
    {
    }

    protected override IReadOnlyList<ProjectIdentity> EnumerateProjectIdentities(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken)
    {
        var identities = new List<ProjectIdentity>();
        var expectedContainers = GetExpectedLocalProjectContainers(installation);
        var directories = EnumerateBoundedProjectDirectories(
            GetExistingLocalProjectContainers(installation),
            cancellationToken);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var projectRoot = WallpaperEngineLocalPath.ValidateExistingDirectory(directory);
                if (!expectedContainers.Any(
                        container => HasDirectParent(projectRoot, container)))
                {
                    continue;
                }

                identities.Add(new ProjectIdentity(projectRoot, projectRoot));
            }
            catch (Exception exception) when (IsUnsafePathException(exception))
            {
                // A single reparse or inaccessible project never authorizes an alternate path.
            }
        }

        return identities;
    }

    protected override string ResolveProjectRoot(
        MediaReference reference,
        WallpaperEngineInstallation installation)
    {
        string candidate;
        try
        {
            candidate = WallpaperEngineLocalPath.NormalizeAbsolutePath(
                reference.SourceIdentifier);
        }
        catch (Exception exception) when (IsUnsafePathException(exception))
        {
            throw new WallpaperEngineProjectUnavailableException(
                SourceKind,
                WallpaperEngineProjectUnavailableReason.UnsafePath,
                exception);
        }

        var expectedContainers = GetExpectedLocalProjectContainers(installation);
        if (!expectedContainers.Any(container => HasDirectParent(candidate, container)))
        {
            throw new WallpaperEngineProjectUnavailableException(
                SourceKind,
                WallpaperEngineProjectUnavailableReason.UnsafePath);
        }

        if (!Directory.Exists(candidate))
        {
            throw new WallpaperEngineProjectUnavailableException(
                SourceKind,
                WallpaperEngineProjectUnavailableReason.NotFound);
        }

        try
        {
            return WallpaperEngineLocalPath.ValidateExistingDirectory(candidate);
        }
        catch (Exception exception) when (IsUnsafePathException(exception))
        {
            throw new WallpaperEngineProjectUnavailableException(
                SourceKind,
                WallpaperEngineProjectUnavailableReason.UnsafePath,
                exception);
        }
    }

    private static IEnumerable<string> GetExistingLocalProjectContainers(
        WallpaperEngineInstallation installation)
    {
        foreach (var container in GetExpectedLocalProjectContainers(installation))
        {
            if (Directory.Exists(container))
            {
                yield return WallpaperEngineLocalPath.ValidateExistingDirectory(container);
            }
        }
    }

    private static IReadOnlyList<string> GetExpectedLocalProjectContainers(
        WallpaperEngineInstallation installation)
    {
        var projectsRoot = WallpaperEngineLocalPath.CombineContained(
            installation.InstallRootPath,
            "projects");
        return
        [
            WallpaperEngineLocalPath.CombineContained(projectsRoot, "myprojects"),
            WallpaperEngineLocalPath.CombineContained(projectsRoot, "backup"),
        ];
    }

    private static bool HasDirectParent(string candidate, string expectedParent)
    {
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(candidate));
        return parent is not null && string.Equals(
            WallpaperEngineLocalPath.NormalizeAbsolutePath(parent.FullName),
            WallpaperEngineLocalPath.NormalizeAbsolutePath(expectedParent),
            StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class WallpaperEngineWorkshopProjectSourceProvider
    : WallpaperEngineProjectSourceProviderBase
{
    public WallpaperEngineWorkshopProjectSourceProvider(
        IWallpaperEngineInstallationLocator? installationLocator = null,
        IMediaStreamInspector? mediaInspector = null)
        : base(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            installationLocator ?? new WallpaperEngineInstallationLocator(),
            mediaInspector)
    {
    }

    protected override IReadOnlyList<ProjectIdentity> EnumerateProjectIdentities(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken)
    {
        var identities = new List<ProjectIdentity>();
        var directories = EnumerateBoundedProjectDirectories(
            GetExistingWorkshopContentRoots(installation),
            cancellationToken);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identifier = Path.GetFileName(
                Path.TrimEndingDirectorySeparator(directory));
            if (!ulong.TryParse(
                    identifier,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var publishedFileId) ||
                publishedFileId == 0)
            {
                continue;
            }

            try
            {
                var projectRoot = WallpaperEngineLocalPath.ValidateExistingDirectory(directory);
                identities.Add(new ProjectIdentity(
                    publishedFileId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    projectRoot));
            }
            catch (Exception exception) when (IsUnsafePathException(exception))
            {
                // Unsafe Workshop items are not discoverable and are never followed.
            }
        }

        return identities
            .GroupBy(identity => identity.SourceIdentifier, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToArray();
    }

    protected override string ResolveProjectRoot(
        MediaReference reference,
        WallpaperEngineInstallation installation)
    {
        var candidates = new List<string>();
        foreach (var library in installation.SteamLibraryPaths)
        {
            var workshopRoot = GetWorkshopContentRoot(library);
            var candidate = WallpaperEngineLocalPath.CombineContained(
                workshopRoot,
                reference.SourceIdentifier);
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            try
            {
                candidates.Add(WallpaperEngineLocalPath.ValidateExistingDirectory(candidate));
            }
            catch (Exception exception) when (IsUnsafePathException(exception))
            {
                throw new WallpaperEngineProjectUnavailableException(
                    SourceKind,
                    WallpaperEngineProjectUnavailableReason.UnsafePath,
                    exception);
            }
        }

        if (candidates.Count == 0)
        {
            throw new WallpaperEngineProjectUnavailableException(
                SourceKind,
                WallpaperEngineProjectUnavailableReason.NotFound);
        }

        if (candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            throw new WallpaperEngineProjectUnavailableException(
                SourceKind,
                WallpaperEngineProjectUnavailableReason.AmbiguousIdentity);
        }

        return candidates[0];
    }

    private static string GetWorkshopContentRoot(string library) =>
        WallpaperEngineLocalPath.CombineContained(
            library,
            Path.Combine(
                "steamapps",
                "workshop",
                "content",
                WallpaperEngineInstallation.SteamApplicationId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)));

    private static IEnumerable<string> GetExistingWorkshopContentRoots(
        WallpaperEngineInstallation installation)
    {
        foreach (var library in installation.SteamLibraryPaths)
        {
            var workshopRoot = GetWorkshopContentRoot(library);
            if (Directory.Exists(workshopRoot))
            {
                yield return WallpaperEngineLocalPath.ValidateExistingDirectory(workshopRoot);
            }
        }
    }
}

public abstract class WallpaperEngineProjectSourceProviderBase :
    IDirectMediaSourceProvider,
    IWallpaperEngineProjectSourceProvider,
    IWallpaperThumbnailSourceProvider
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public const int MaximumDiscoveredProjects = 8192;
    public const long MaximumThumbnailLength = 16L * 1024 * 1024;

    private readonly IWallpaperEngineInstallationLocator _installationLocator;
    private readonly LocalFileWallpaperSourceProvider _localMediaProvider;

    protected WallpaperEngineProjectSourceProviderBase(
        MediaSourceKind sourceKind,
        IWallpaperEngineInstallationLocator installationLocator,
        IMediaStreamInspector? mediaInspector)
    {
        if (sourceKind is not (
                MediaSourceKind.WallpaperEngineLocalProject or
                MediaSourceKind.WallpaperEngineWorkshopProject))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceKind));
        }

        ArgumentNullException.ThrowIfNull(installationLocator);
        SourceKind = sourceKind;
        _installationLocator = installationLocator;
        _localMediaProvider = new LocalFileWallpaperSourceProvider(mediaInspector);
    }

    public MediaSourceKind SourceKind { get; }

    public async ValueTask<IReadOnlyList<WallpaperSourceDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var installation = await _installationLocator
            .LocateAsync(cancellationToken)
            .ConfigureAwait(false);
        var descriptors = new List<WallpaperSourceDescriptor>();
        foreach (var identity in EnumerateProjectIdentities(installation, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var project = await ReadProjectAsync(
                        identity.ProjectRootPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (project.Manifest.ContentKind is
                    WallpaperContentKind.Application or WallpaperContentKind.Unknown)
                {
                    continue;
                }

                _ = ResolveEntryPoint(project, SourceKind);
                descriptors.Add(CreateDescriptor(
                    identity.SourceIdentifier,
                    project.Manifest));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is WallpaperEngineProjectUnavailableException or
                    WallpaperContentNotSupportedException)
            {
                // One malformed, stale, or unsupported project does not hide valid neighbours.
            }
        }

        return descriptors
            .OrderBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(descriptor => descriptor.SourceIdentifier, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask<WallpaperSourceResolution> ResolveAsync(
        MediaReference reference,
        CancellationToken cancellationToken = default)
    {
        var snapshot = ValidateReference(reference);
        var installation = await _installationLocator
            .LocateAsync(cancellationToken)
            .ConfigureAwait(false);
        var projectRoot = ResolveProjectRoot(snapshot, installation);
        await using var project = await ReadProjectAsync(projectRoot, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfUnsupported(snapshot, project.Manifest);

        if (project.Manifest.ContentKind is
            WallpaperContentKind.Image or WallpaperContentKind.Video)
        {
            await using var lease = await AcquireDirectMediaLeaseCoreAsync(
                    snapshot,
                    project,
                    cancellationToken)
                .ConfigureAwait(false);
            return lease.Resolution;
        }

        await using var projectLease = AcquireProjectLeaseCore(snapshot, project);
        return projectLease.Resolution;
    }

    public async ValueTask<IDirectMediaLease> AcquireDirectMediaLeaseAsync(
        MediaReference reference,
        CancellationToken cancellationToken = default)
    {
        var snapshot = ValidateReference(reference);
        var installation = await _installationLocator
            .LocateAsync(cancellationToken)
            .ConfigureAwait(false);
        var projectRoot = ResolveProjectRoot(snapshot, installation);
        var project = await ReadProjectAsync(projectRoot, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ThrowIfUnsupported(snapshot, project.Manifest);
            var result = await AcquireDirectMediaLeaseCoreAsync(
                    snapshot,
                    project,
                    cancellationToken)
                .ConfigureAwait(false);
            project = null;
            return result;
        }
        finally
        {
            if (project is not null)
            {
                await project.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<IWallpaperEngineProjectLease> AcquireProjectLeaseAsync(
        MediaReference reference,
        CancellationToken cancellationToken = default)
    {
        var snapshot = ValidateReference(reference);
        var installation = await _installationLocator
            .LocateAsync(cancellationToken)
            .ConfigureAwait(false);
        var projectRoot = ResolveProjectRoot(snapshot, installation);
        var project = await ReadProjectAsync(projectRoot, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ThrowIfUnsupported(snapshot, project.Manifest);
            var result = AcquireProjectLeaseCore(snapshot, project);
            project = null;
            return result;
        }
        finally
        {
            if (project is not null)
            {
                await project.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<IWallpaperThumbnailLease?> AcquireThumbnailLeaseAsync(
        MediaReference reference,
        CancellationToken cancellationToken = default)
    {
        var snapshot = ValidateReference(reference);
        var installation = await _installationLocator
            .LocateAsync(cancellationToken)
            .ConfigureAwait(false);
        var projectRoot = ResolveProjectRoot(snapshot, installation);
        var project = await ReadProjectAsync(projectRoot, cancellationToken)
            .ConfigureAwait(false);
        FileStream? thumbnailStream = null;
        try
        {
            ThrowIfUnsupported(snapshot, project.Manifest);
            var previewPath = ResolvePreviewPath(project);
            if (previewPath is null)
            {
                return null;
            }

            thumbnailStream = WallpaperEngineLocalPath.OpenPinnedReadOnlyFile(
                previewPath,
                project.ProjectRootPath);
            if (thumbnailStream.Length is <= 0 or > MaximumThumbnailLength)
            {
                throw new WallpaperEngineProjectUnavailableException(
                    SourceKind,
                    WallpaperEngineProjectUnavailableReason.InvalidThumbnail);
            }

            var header = new byte[12];
            var headerLength = await thumbnailStream
                .ReadAsync(header, cancellationToken)
                .ConfigureAwait(false);
            thumbnailStream.Position = 0;
            var contentType = DetectThumbnailContentType(header.AsSpan(0, headerLength));
            var descriptor = CreateDescriptor(snapshot.SourceIdentifier, project.Manifest);
            var canonicalReference = snapshot with
            {
                LastKnownKind = descriptor.ContentKind switch
                {
                    WallpaperContentKind.Image => MediaKind.Image,
                    WallpaperContentKind.Video => MediaKind.Video,
                    _ => MediaKind.None,
                },
                LastKnownContentKind = descriptor.ContentKind,
                LastKnownDisplayName = descriptor.DisplayName,
            };
            var result = new WallpaperEngineThumbnailLease(
                canonicalReference.Snapshot(),
                contentType,
                thumbnailStream,
                project);
            thumbnailStream = null;
            project = null;
            return result;
        }
        catch (WallpaperEngineProjectUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (IsUnsafePathException(exception))
        {
            throw new WallpaperEngineProjectUnavailableException(
                SourceKind,
                WallpaperEngineProjectUnavailableReason.InvalidThumbnail,
                exception);
        }
        finally
        {
            thumbnailStream?.Dispose();
            if (project is not null)
            {
                await project.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    protected abstract IReadOnlyList<ProjectIdentity> EnumerateProjectIdentities(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken);

    protected abstract string ResolveProjectRoot(
        MediaReference reference,
        WallpaperEngineInstallation installation);

    protected sealed record ProjectIdentity(
        string SourceIdentifier,
        string ProjectRootPath);

    protected IReadOnlyList<string> EnumerateBoundedProjectDirectories(
        IEnumerable<string> projectContainers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectContainers);
        var directories = new List<string>(MaximumDiscoveredProjects + 1);
        foreach (var container in projectContainers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var directory in Directory.EnumerateDirectories(
                         container,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                directories.Add(directory);
                if (directories.Count > MaximumDiscoveredProjects)
                {
                    throw new WallpaperEngineProjectUnavailableException(
                        SourceKind,
                        WallpaperEngineProjectUnavailableReason.AmbiguousIdentity);
                }
            }
        }

        directories.Sort(StringComparer.OrdinalIgnoreCase);
        return directories;
    }

    protected static bool IsUnsafePathException(Exception exception) =>
        exception is ArgumentException or InvalidDataException or IOException or
            UnauthorizedAccessException or MediaValidationException;

    private async ValueTask<WallpaperEngineDirectMediaLease>
        AcquireDirectMediaLeaseCoreAsync(
            MediaReference reference,
            PinnedWallpaperEngineProjectManifest project,
            CancellationToken cancellationToken)
    {
        if (project.Manifest.ContentKind is not (
                WallpaperContentKind.Image or WallpaperContentKind.Video))
        {
            throw new WallpaperSourceCapabilityException(
                "This Wallpaper Engine project does not expose direct media.");
        }

        var entry = ResolveEntryPoint(project, SourceKind);
        var localReference = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = entry.DirectMediaPath!,
            LastKnownKind = MediaKind.None,
            LastKnownContentKind = WallpaperContentKind.Unknown,
        };
        IDirectMediaLease? localLease = null;
        try
        {
            localLease = await _localMediaProvider
                .AcquireDirectMediaLeaseAsync(localReference, cancellationToken)
                .ConfigureAwait(false);
            if (!WallpaperEngineLocalPath.IsContainedBy(
                    project.ProjectRootPath,
                    localLease.ResolvedPath))
            {
                throw new WallpaperEngineProjectUnavailableException(
                    SourceKind,
                    WallpaperEngineProjectUnavailableReason.UnsafePath);
            }

            var descriptor = CreateDescriptor(reference.SourceIdentifier, project.Manifest);
            var resolution = new WallpaperSourceResolution(
                reference,
                descriptor,
                localLease.Metadata);
            var result = new WallpaperEngineDirectMediaLease(
                resolution,
                localLease,
                project);
            localLease = null;
            return result;
        }
        catch (WallpaperEngineProjectUnavailableException)
        {
            throw;
        }
        catch (MediaValidationException exception)
        {
            throw new WallpaperEngineProjectUnavailableException(
                SourceKind,
                WallpaperEngineProjectUnavailableReason.InvalidDirectMedia,
                exception);
        }
        finally
        {
            if (localLease is not null)
            {
                await localLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private WallpaperEngineProjectLease AcquireProjectLeaseCore(
        MediaReference reference,
        PinnedWallpaperEngineProjectManifest project)
    {
        if (project.Manifest.ContentKind is not (
                WallpaperContentKind.Scene or WallpaperContentKind.Web))
        {
            throw new WallpaperSourceCapabilityException(
                "This Wallpaper Engine project does not require window delivery.");
        }

        var entry = ResolveEntryPoint(project, SourceKind);
        FileStream? entryStream = null;
        try
        {
            if (entry.EntryPathToPin is not null &&
                !string.Equals(
                    entry.EntryPathToPin,
                    project.ProjectJsonPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                entryStream = WallpaperEngineLocalPath.OpenPinnedReadOnlyFile(
                    entry.EntryPathToPin,
                    project.ProjectRootPath);
            }

            var descriptor = CreateDescriptor(reference.SourceIdentifier, project.Manifest);
            var resolution = new WallpaperSourceResolution(
                reference,
                descriptor,
                directMediaMetadata: null);
            var result = new WallpaperEngineProjectLease(
                resolution,
                entry.LaunchPath!,
                project,
                entryStream);
            entryStream = null;
            return result;
        }
        catch (WallpaperEngineProjectUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (IsUnsafePathException(exception))
        {
            throw new WallpaperEngineProjectUnavailableException(
                SourceKind,
                WallpaperEngineProjectUnavailableReason.UnsafePath,
                exception);
        }
        finally
        {
            entryStream?.Dispose();
        }
    }

    private async ValueTask<PinnedWallpaperEngineProjectManifest> ReadProjectAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            return await WallpaperEngineProjectManifestReader
                .ReadPinnedAsync(projectRoot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException exception)
        {
            throw new WallpaperEngineProjectUnavailableException(
                SourceKind,
                WallpaperEngineProjectUnavailableReason.NotFound,
                exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new WallpaperEngineProjectUnavailableException(
                SourceKind,
                WallpaperEngineProjectUnavailableReason.NotFound,
                exception);
        }
        catch (Exception exception) when (IsUnsafePathException(exception))
        {
            throw new WallpaperEngineProjectUnavailableException(
                SourceKind,
                WallpaperEngineProjectUnavailableReason.InvalidManifest,
                exception);
        }
    }

    private MediaReference ValidateReference(MediaReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var snapshot = reference.Snapshot();
        if (snapshot.SourceKind != SourceKind)
        {
            throw new MediaSourceNotSupportedException(snapshot.SourceKind);
        }

        return snapshot;
    }

    private WallpaperSourceDescriptor CreateDescriptor(
        string sourceIdentifier,
        WallpaperEngineProjectManifest manifest)
    {
        var deliveryKind = manifest.ContentKind switch
        {
            WallpaperContentKind.Image or WallpaperContentKind.Video =>
                WallpaperDeliveryKind.DirectMedia,
            WallpaperContentKind.Scene or WallpaperContentKind.Web =>
                WallpaperDeliveryKind.WallpaperEngineWindow,
            _ => WallpaperDeliveryKind.Unsupported,
        };
        return new WallpaperSourceDescriptor(
            SourceKind,
            sourceIdentifier,
            manifest.DisplayName,
            manifest.ContentKind,
            deliveryKind,
            deliveryKind == WallpaperDeliveryKind.WallpaperEngineWindow
                ? WallpaperDeliveryCapabilities.DynamicFrames
                : WallpaperDeliveryCapabilities.None);
    }

    private static void ThrowIfUnsupported(
        MediaReference reference,
        WallpaperEngineProjectManifest manifest)
    {
        if (manifest.ContentKind is not (
                WallpaperContentKind.Application or WallpaperContentKind.Unknown))
        {
            return;
        }

        throw new WallpaperContentNotSupportedException(
            new WallpaperSourceDescriptor(
                reference.SourceKind,
                reference.SourceIdentifier,
                manifest.DisplayName,
                manifest.ContentKind,
                WallpaperDeliveryKind.Unsupported,
                WallpaperDeliveryCapabilities.None));
    }

    private static ProjectEntryPoint ResolveEntryPoint(
        PinnedWallpaperEngineProjectManifest project,
        MediaSourceKind sourceKind)
    {
        var relativePath = project.Manifest.EntryRelativePath ??
            throw new WallpaperEngineProjectUnavailableException(
                sourceKind,
                WallpaperEngineProjectUnavailableReason.EntryPointMissing);
        string entryPath;
        try
        {
            entryPath = WallpaperEngineLocalPath.CombineContained(
                project.ProjectRootPath,
                relativePath);
        }
        catch (Exception exception) when (IsUnsafePathException(exception))
        {
            throw new WallpaperEngineProjectUnavailableException(
                sourceKind,
                WallpaperEngineProjectUnavailableReason.UnsafePath,
                exception);
        }

        if (!File.Exists(entryPath))
        {
            if (sourceKind == MediaSourceKind.WallpaperEngineWorkshopProject &&
                project.Manifest.ContentKind == WallpaperContentKind.Scene &&
                string.Equals(
                    Path.GetFileName(relativePath),
                    "scene.json",
                    StringComparison.OrdinalIgnoreCase))
            {
                entryPath = FindUniqueScenePackage(project.ProjectRootPath, sourceKind);
            }
            else
            {
                throw new WallpaperEngineProjectUnavailableException(
                    sourceKind,
                    WallpaperEngineProjectUnavailableReason.EntryPointMissing);
            }
        }

        try
        {
            entryPath = WallpaperEngineLocalPath.ValidateExistingRegularFile(
                entryPath,
                project.ProjectRootPath);
        }
        catch (Exception exception) when (IsUnsafePathException(exception))
        {
            throw new WallpaperEngineProjectUnavailableException(
                sourceKind,
                WallpaperEngineProjectUnavailableReason.UnsafePath,
                exception);
        }

        switch (project.Manifest.ContentKind)
        {
            case WallpaperContentKind.Image:
            case WallpaperContentKind.Video:
                return new ProjectEntryPoint(entryPath, null, null);
            case WallpaperContentKind.Web:
                if (!Path.GetExtension(entryPath).Equals(".html", StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetExtension(entryPath).Equals(".htm", StringComparison.OrdinalIgnoreCase))
                {
                    throw new WallpaperEngineProjectUnavailableException(
                        sourceKind,
                        WallpaperEngineProjectUnavailableReason.EntryPointMissing);
                }

                return new ProjectEntryPoint(null, entryPath, entryPath);
            case WallpaperContentKind.Scene:
                var extension = Path.GetExtension(entryPath);
                if (extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase))
                {
                    return new ProjectEntryPoint(null, entryPath, entryPath);
                }

                if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new WallpaperEngineProjectUnavailableException(
                        sourceKind,
                        WallpaperEngineProjectUnavailableReason.EntryPointMissing);
                }

                return new ProjectEntryPoint(
                    null,
                    project.ProjectJsonPath,
                    entryPath);
            default:
                throw new WallpaperEngineProjectUnavailableException(
                    sourceKind,
                    WallpaperEngineProjectUnavailableReason.EntryPointMissing);
        }
    }

    internal static string FindUniqueScenePackage(
        string projectRoot,
        MediaSourceKind sourceKind)
    {
        string[] packages;
        try
        {
            packages = Directory
                .EnumerateFiles(projectRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).Equals(
                    ".pkg",
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException)
        {
            throw new WallpaperEngineProjectUnavailableException(
                sourceKind,
                WallpaperEngineProjectUnavailableReason.EntryPointMissing,
                exception);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new WallpaperEngineProjectUnavailableException(
                sourceKind,
                WallpaperEngineProjectUnavailableReason.UnsafePath,
                exception);
        }

        if (packages.Length == 0)
        {
            throw new WallpaperEngineProjectUnavailableException(
                sourceKind,
                WallpaperEngineProjectUnavailableReason.EntryPointMissing);
        }

        if (packages.Length != 1)
        {
            throw new WallpaperEngineProjectUnavailableException(
                sourceKind,
                WallpaperEngineProjectUnavailableReason.AmbiguousScenePackage);
        }

        return packages[0];
    }

    private static string? ResolvePreviewPath(
        PinnedWallpaperEngineProjectManifest project)
    {
        if (project.Manifest.PreviewRelativePath is not null)
        {
            var declared = WallpaperEngineLocalPath.CombineContained(
                project.ProjectRootPath,
                project.Manifest.PreviewRelativePath);
            return File.Exists(declared) ? declared : null;
        }

        foreach (var fileName in new[]
                 {
                     "preview.jpg",
                     "preview.jpeg",
                     "preview.png",
                     "preview.webp",
                     "preview.gif",
                 })
        {
            var candidate = WallpaperEngineLocalPath.CombineContained(
                project.ProjectRootPath,
                fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string DetectThumbnailContentType(ReadOnlySpan<byte> header)
    {
        if (header.Length >= PngSignature.Length &&
            header[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return "image/png";
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (header.Length >= 12 &&
            header[..4].SequenceEqual("RIFF"u8) &&
            header[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        throw new WallpaperEngineProjectUnavailableException(
            SourceKind,
            WallpaperEngineProjectUnavailableReason.InvalidThumbnail);
    }

    private sealed record ProjectEntryPoint(
        string? DirectMediaPath,
        string? LaunchPath,
        string? EntryPathToPin);

    private sealed class WallpaperEngineDirectMediaLease(
        WallpaperSourceResolution resolution,
        IDirectMediaLease localLease,
        PinnedWallpaperEngineProjectManifest project) : IDirectMediaLease
    {
        private readonly SemaphoreSlim _disposeGate = new(1, 1);
        private IDirectMediaLease? _localLease = localLease;
        private PinnedWallpaperEngineProjectManifest? _project = project;

        public WallpaperSourceResolution Resolution { get; } = resolution;

        public MediaReference Reference => Resolution.CanonicalReference;

        public string ResolvedPath => GetLocalLease().ResolvedPath;

        public LocalFileIdentity FileIdentity => GetLocalLease().FileIdentity;

        public MediaFileMetadata Metadata => GetLocalLease().Metadata;

        public async ValueTask DisposeAsync()
        {
            await _disposeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                List<Exception>? failures = null;
                await DisposeOneAsync(
                        _localLease,
                        () => _localLease = null)
                    .ConfigureAwait(false);
                await DisposeOneAsync(
                        _project,
                        () => _project = null)
                    .ConfigureAwait(false);

                if (_localLease is null && _project is null)
                {
                    GC.SuppressFinalize(this);
                }

                if (failures is [var failure])
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Capture(failure)
                        .Throw();
                }

                if (failures is { Count: > 1 })
                {
                    throw new AggregateException(
                        "One or more Wallpaper Engine direct-media resources could not be released.",
                        failures);
                }

                async ValueTask DisposeOneAsync(
                    IAsyncDisposable? resource,
                    Action clear)
                {
                    if (resource is null)
                    {
                        return;
                    }

                    try
                    {
                        await resource.DisposeAsync().ConfigureAwait(false);
                        clear();
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }
            }
            finally
            {
                _disposeGate.Release();
            }
        }

        public override string ToString() =>
            $"{nameof(WallpaperEngineDirectMediaLease)} {{ Metadata = {Metadata}, " +
            "Paths = <redacted> }}";

        private IDirectMediaLease GetLocalLease() =>
            _localLease ?? throw new ObjectDisposedException(
                nameof(WallpaperEngineDirectMediaLease));
    }

    internal sealed class WallpaperEngineProjectLease(
        WallpaperSourceResolution resolution,
        string launchPath,
        IAsyncDisposable project,
        IDisposable? entryStream) : IWallpaperEngineProjectLease
    {
        private readonly SemaphoreSlim _disposeGate = new(1, 1);
        private IAsyncDisposable? _project =
            project ?? throw new ArgumentNullException(nameof(project));
        private IDisposable? _entryStream = entryStream;

        public WallpaperSourceResolution Resolution { get; } = resolution;

        public string LaunchPath { get; } = launchPath;

        public async ValueTask DisposeAsync()
        {
            await _disposeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                List<Exception>? failures = null;
                var retainedEntryStream = _entryStream;
                if (retainedEntryStream is not null)
                {
                    try
                    {
                        retainedEntryStream.Dispose();
                        _entryStream = null;
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }

                var retainedProject = _project;
                if (retainedProject is not null)
                {
                    try
                    {
                        await retainedProject.DisposeAsync().ConfigureAwait(false);
                        _project = null;
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }

                if (failures is [var failure])
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Capture(failure)
                        .Throw();
                }

                if (failures is { Count: > 1 })
                {
                    throw new AggregateException(
                        "One or more Wallpaper Engine project resources could not be released.",
                        failures);
                }
            }
            finally
            {
                _disposeGate.Release();
            }
        }

        public override string ToString() =>
            $"{nameof(WallpaperEngineProjectLease)} {{ ContentKind = " +
            $"{Resolution.Descriptor.ContentKind}, LaunchPath = <redacted> }}";
    }

    private sealed class WallpaperEngineThumbnailLease(
        MediaReference reference,
        string contentType,
        FileStream contentStream,
        PinnedWallpaperEngineProjectManifest project) : IWallpaperThumbnailLease
    {
        private FileStream? _contentStream = contentStream;
        private PinnedWallpaperEngineProjectManifest? _project = project;

        public MediaReference Reference { get; } = reference;

        public string ContentType { get; } = contentType;

        public long ContentLength => ContentStream.Length;

        public Stream ContentStream =>
            _contentStream ?? throw new ObjectDisposedException(
                nameof(WallpaperEngineThumbnailLease));

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _contentStream, null)?.Dispose();
            var retainedProject = Interlocked.Exchange(ref _project, null);
            if (retainedProject is not null)
            {
                await retainedProject.DisposeAsync().ConfigureAwait(false);
            }
        }

        public override string ToString() =>
            $"{nameof(WallpaperEngineThumbnailLease)} {{ ContentType = {ContentType}, " +
            $"ContentLength = {ContentLength}, Path = <redacted> }}";
    }
}
