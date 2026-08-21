using System.Text.Json;

namespace BackdropForCodex.Core.Media;

internal sealed record WallpaperEngineProjectManifest(
    string DisplayName,
    WallpaperContentKind ContentKind,
    string? EntryRelativePath,
    string? PreviewRelativePath);

internal sealed class PinnedWallpaperEngineProjectManifest : IAsyncDisposable
{
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private Stream? _manifestStream;

    public PinnedWallpaperEngineProjectManifest(
        string projectRootPath,
        string projectJsonPath,
        WallpaperEngineProjectManifest manifest,
        Stream manifestStream)
    {
        ProjectRootPath = projectRootPath;
        ProjectJsonPath = projectJsonPath;
        Manifest = manifest;
        _manifestStream = manifestStream;
    }

    public string ProjectRootPath { get; }

    public string ProjectJsonPath { get; }

    public WallpaperEngineProjectManifest Manifest { get; }

    public Stream ManifestStream =>
        _manifestStream ?? throw new ObjectDisposedException(
            nameof(PinnedWallpaperEngineProjectManifest));

    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var retainedStream = _manifestStream;
            if (retainedStream is null)
            {
                return;
            }

            retainedStream.Dispose();
            _manifestStream = null;
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    public override string ToString() =>
        $"{nameof(PinnedWallpaperEngineProjectManifest)} {{ ContentKind = " +
        $"{Manifest.ContentKind}, Paths = <redacted> }}";
}

internal static class WallpaperEngineProjectManifestReader
{
    public const int MaximumDocumentLength = 256 * 1024;
    public const int MaximumDepth = 16;
    public const int MaximumEntryPathLength = 4096;

    public static async ValueTask<PinnedWallpaperEngineProjectManifest> ReadPinnedAsync(
        string projectRootPath,
        CancellationToken cancellationToken)
    {
        var validatedRoot = WallpaperEngineLocalPath.ValidateExistingDirectory(projectRootPath);
        var projectJsonPath = WallpaperEngineLocalPath.CombineContained(
            validatedRoot,
            "project.json");
        FileStream? stream = null;
        try
        {
            stream = WallpaperEngineLocalPath.OpenPinnedReadOnlyFile(
                projectJsonPath,
                validatedRoot);
            if (stream.Length is <= 0 or > MaximumDocumentLength)
            {
                throw new InvalidDataException(
                    "The Wallpaper Engine project manifest has an invalid size.");
            }

            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            var manifest = Parse(bytes, validatedRoot);
            var resolvedProjectJsonPath = WindowsLocalFileIdentity.ResolveFinalPath(
                stream.SafeFileHandle);
            if (!WallpaperEngineLocalPath.IsContainedBy(
                    validatedRoot,
                    resolvedProjectJsonPath))
            {
                throw new InvalidDataException(
                    "The Wallpaper Engine project manifest escaped its project root.");
            }

            var result = new PinnedWallpaperEngineProjectManifest(
                validatedRoot,
                resolvedProjectJsonPath,
                manifest,
                stream);
            stream = null;
            return result;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static WallpaperEngineProjectManifest Parse(
        ReadOnlyMemory<byte> bytes,
        string projectRootPath)
    {
        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumDepth,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The Wallpaper Engine project manifest root must be an object.");
            }

            string? type = null;
            string? title = null;
            string? entry = null;
            string? preview = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var recognizedName = property.Name switch
                {
                    var name when name.Equals("type", StringComparison.OrdinalIgnoreCase) => "type",
                    var name when name.Equals("title", StringComparison.OrdinalIgnoreCase) => "title",
                    var name when name.Equals("file", StringComparison.OrdinalIgnoreCase) => "file",
                    var name when name.Equals("preview", StringComparison.OrdinalIgnoreCase) => "preview",
                    _ => null,
                };
                if (recognizedName is null)
                {
                    // Wallpaper Engine adds fields over time. Unknown fields are intentionally
                    // ignored within the document size and depth limits.
                    continue;
                }

                if (!seen.Add(recognizedName))
                {
                    throw new InvalidDataException(
                        "The Wallpaper Engine project manifest has duplicate identity fields.");
                }

                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => throw new InvalidDataException(
                        "A Wallpaper Engine project identity field has the wrong type."),
                };
                switch (recognizedName)
                {
                    case "type":
                        type = value;
                        break;
                    case "title":
                        title = value;
                        break;
                    case "file":
                        entry = value;
                        break;
                    case "preview":
                        preview = value;
                        break;
                }
            }

            var contentKind = ParseContentKind(type);
            var displayName = NormalizeDisplayName(title, projectRootPath);
            entry = NormalizeRelativePath(entry, "entry", required: contentKind is not (
                WallpaperContentKind.Application or WallpaperContentKind.Unknown));
            preview = NormalizeRelativePath(preview, "preview", required: false);
            return new WallpaperEngineProjectManifest(
                displayName,
                contentKind,
                entry,
                preview);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The Wallpaper Engine project manifest is not valid bounded JSON.",
                exception);
        }
    }

    private static WallpaperContentKind ParseContentKind(string? type)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Length > 32)
        {
            return WallpaperContentKind.Unknown;
        }

        return type.Trim().ToLowerInvariant() switch
        {
            "image" => WallpaperContentKind.Image,
            "video" => WallpaperContentKind.Video,
            "scene" => WallpaperContentKind.Scene,
            "web" => WallpaperContentKind.Web,
            "application" => WallpaperContentKind.Application,
            _ => WallpaperContentKind.Unknown,
        };
    }

    private static string NormalizeDisplayName(string? title, string projectRootPath)
    {
        if (title?.Length > WallpaperSourceDescriptor.MaximumDisplayNameLength)
        {
            throw new InvalidDataException(
                "The Wallpaper Engine project title is too long.");
        }

        var displayName = title is null
            ? null
            : WallpaperDisplayNameSanitizer.Sanitize(title);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = WallpaperDisplayNameSanitizer.Sanitize(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(projectRootPath)));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "Wallpaper Engine project";
        }

        if (displayName.Length > WallpaperSourceDescriptor.MaximumDisplayNameLength)
        {
            throw new InvalidDataException(
                "The Wallpaper Engine project title is too long.");
        }

        return displayName;
    }

    private static string? NormalizeRelativePath(
        string? path,
        string fieldName,
        bool required)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (required)
            {
                throw new InvalidDataException(
                    $"The Wallpaper Engine project {fieldName} is missing.");
            }

            return null;
        }

        path = path.Trim();
        if (path.Length > MaximumEntryPathLength || Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException(
                $"The Wallpaper Engine project {fieldName} path is invalid.");
        }

        return path;
    }
}
