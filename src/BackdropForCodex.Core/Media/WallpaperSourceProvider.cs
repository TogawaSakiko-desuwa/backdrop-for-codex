using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BackdropForCodex.Core.Media;

public sealed record LocalFileIdentity(uint VolumeSerialNumber, ulong FileIndex);

public interface IMediaLease : IAsyncDisposable
{
    MediaReference Reference { get; }

    string ResolvedPath { get; }

    LocalFileIdentity FileIdentity { get; }

    MediaFileMetadata Metadata { get; }
}

public sealed record MediaSourceValidation(
    MediaReference Reference,
    MediaFileMetadata Metadata);

public interface IWallpaperSourceProvider
{
    MediaSourceKind SourceKind { get; }

    /// <summary>
    /// Discovers already-known sources without scanning arbitrary user directories. Local-file
    /// selection is user-directed, so its provider returns an empty collection.
    /// </summary>
    ValueTask<IReadOnlyList<MediaReference>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<MediaReference>>(
            Array.Empty<MediaReference>());
    }

    /// <summary>
    /// Resolves and normalizes a durable source reference. This is metadata preparation only and is
    /// never an authorization substitute for acquiring a lease.
    /// </summary>
    ValueTask<MediaReference> ResolveAsync(
        MediaReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (reference.SourceKind != SourceKind)
        {
            throw new MediaSourceNotSupportedException(reference.SourceKind);
        }

        return ValueTask.FromResult(reference.Snapshot());
    }

    /// <summary>
    /// Performs an advisory validation and releases its handle. Runtime use must still acquire a
    /// fresh lease, which repeats validation through the pinned handle.
    /// </summary>
    async ValueTask<MediaSourceValidation> ValidateAsync(
        MediaReference reference,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(reference, cancellationToken)
            .ConfigureAwait(false);
        return new MediaSourceValidation(lease.Reference, lease.Metadata);
    }

    ValueTask<IMediaLease> AcquireLeaseAsync(
        MediaReference reference,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Opens one read-only local file handle, resolves its final target, validates that target through
/// the same handle, and keeps it pinned until the returned lease is disposed.
/// </summary>
public sealed class LocalFileWallpaperSourceProvider : IWallpaperSourceProvider
{
    private readonly IMediaStreamInspector _inspector;

    public LocalFileWallpaperSourceProvider(IMediaStreamInspector? inspector = null)
    {
        _inspector = inspector ?? new MediaFileInspector();
    }

    public MediaSourceKind SourceKind => MediaSourceKind.LocalFile;

    public ValueTask<IReadOnlyList<MediaReference>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<MediaReference>>(
            Array.Empty<MediaReference>());
    }

    public ValueTask<MediaReference> ResolveAsync(
        MediaReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (reference.SourceKind != SourceKind)
        {
            throw new MediaSourceNotSupportedException(reference.SourceKind);
        }

        return ValueTask.FromResult(reference.Snapshot());
    }

    public async ValueTask<MediaSourceValidation> ValidateAsync(
        MediaReference reference,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLeaseAsync(reference, cancellationToken)
            .ConfigureAwait(false);
        return new MediaSourceValidation(lease.Reference, lease.Metadata);
    }

    public async ValueTask<IMediaLease> AcquireLeaseAsync(
        MediaReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var snapshot = reference.Snapshot();
        if (snapshot.SourceKind != SourceKind)
        {
            throw new MediaSourceNotSupportedException(snapshot.SourceKind);
        }

        cancellationToken.ThrowIfCancellationRequested();
        FileStream? stream = null;
        try
        {
            stream = OpenReadLease(snapshot.SourceIdentifier);
            var resolvedPath = WindowsLocalFileIdentity.ResolveFinalPath(stream.SafeFileHandle);
            var identity = WindowsLocalFileIdentity.Read(stream.SafeFileHandle, resolvedPath);
            var metadata = await _inspector
                .InspectAsync(stream, resolvedPath, cancellationToken)
                .ConfigureAwait(false);

            var lease = new LocalFileMediaLease(
                snapshot with { LastKnownKind = metadata.Kind },
                resolvedPath,
                identity,
                metadata,
                stream);
            stream = null;
            return lease;
        }
        catch (MediaValidationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException exception)
        {
            throw new MediaValidationException("The media file was not found.", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new MediaValidationException("The media file was not found.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new MediaValidationException("The media file could not be opened.", exception);
        }
        catch (IOException exception)
        {
            throw new MediaValidationException("The media file could not be read.", exception);
        }
        catch (Win32Exception exception)
        {
            throw new MediaValidationException("The media file identity could not be verified.", exception);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static FileStream OpenReadLease(string path)
    {
        if (IsNetworkOrUnsupportedDevicePath(path))
        {
            throw new MediaValidationException("Network media paths are not supported.");
        }

        // Reject mapped network drives before opening the path. The final handle is checked again
        // below because a local-looking path can still traverse a reparse point.
        WindowsLocalFileIdentity.EnsureInputPathTargetsLocalVolume(path);

        // FileShare.Read keeps the selected object readable by Chromium while preventing ordinary
        // writes, replacement, rename, and deletion for the complete injection lifetime.
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static bool IsNetworkOrUnsupportedDevicePath(string path)
    {
        const string extendedDosPrefix = @"\\?\";
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        // Extended-length DOS paths are local candidates; the opened handle and its resolved
        // volume are still verified below. UNC, device, and volume-GUID inputs are rejected.
        return !path.StartsWith(extendedDosPrefix, StringComparison.Ordinal) ||
            path.Length < extendedDosPrefix.Length + 3 ||
            !char.IsAsciiLetter(path[extendedDosPrefix.Length]) ||
            path[extendedDosPrefix.Length + 1] != ':' ||
            path[extendedDosPrefix.Length + 2] != '\\';
    }

    private sealed class LocalFileMediaLease(
        MediaReference reference,
        string resolvedPath,
        LocalFileIdentity fileIdentity,
        MediaFileMetadata metadata,
        FileStream stream) : IMediaLease
    {
        private FileStream? _stream = stream;

        public MediaReference Reference { get; } = reference;

        public string ResolvedPath { get; } = resolvedPath;

        public LocalFileIdentity FileIdentity { get; } = fileIdentity;

        public MediaFileMetadata Metadata { get; } = metadata;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
            return ValueTask.CompletedTask;
        }

        public override string ToString() =>
            $"{nameof(LocalFileMediaLease)} {{ Metadata = {Metadata}, FileIdentity = {FileIdentity}, ResolvedPath = <redacted> }}";
    }
}

internal static class WindowsLocalFileIdentity
{
    private const uint FileTypeDisk = 0x0001;
    private const uint FileAttributeDirectory = 0x0010;

    public static void EnsureInputPathTargetsLocalVolume(string path) =>
        EnsureLocalPath(NormalizeDosPath(path));

    public static string ResolveFinalPath(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Local media leases require Windows.");
        }

        var capacity = 512;
        while (true)
        {
            var builder = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(
                handle,
                builder,
                checked((uint)builder.Capacity),
                flags: 0);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (length < builder.Capacity)
            {
                var resolvedPath = NormalizeDosPath(builder.ToString());
                EnsureLocalPath(resolvedPath);
                return resolvedPath;
            }

            capacity = checked((int)length + 1);
        }
    }

    public static LocalFileIdentity Read(SafeFileHandle handle, string resolvedPath)
    {
        if (GetFileType(handle) != FileTypeDisk)
        {
            throw new MediaValidationException("The media source is not a regular disk file.");
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if ((information.FileAttributes & FileAttributeDirectory) != 0)
        {
            throw new MediaValidationException("The media source is not a regular file.");
        }

        EnsureLocalPath(resolvedPath);
        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return new LocalFileIdentity(information.VolumeSerialNumber, fileIndex);
    }

    private static string NormalizeDosPath(string path)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedDosPrefix = @"\\?\";

        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[extendedUncPrefix.Length..];
        }

        if (path.StartsWith(extendedDosPrefix, StringComparison.Ordinal))
        {
            return path[extendedDosPrefix.Length..];
        }

        return path;
    }

    private static void EnsureLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new MediaValidationException("Network media paths are not supported.");
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new MediaValidationException("The media file did not resolve to a local volume.");
        }

        DriveType driveType;
        try
        {
            driveType = new DriveInfo(root).DriveType;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new MediaValidationException(
                "The media file did not resolve to a stable local volume.",
                exception);
        }

        if (driveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown)
        {
            throw new MediaValidationException(
                "The media file did not resolve to a stable local volume.");
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1838:Avoid StringBuilder parameters for P/Invokes",
        Justification = "GetFinalPathNameByHandle writes into a caller-owned bounded character buffer.")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

public sealed class MediaSourceNotSupportedException : NotSupportedException
{
    public MediaSourceNotSupportedException(MediaSourceKind sourceKind)
        : base($"No wallpaper source provider is available for {sourceKind}.")
    {
        SourceKind = sourceKind;
    }

    public MediaSourceKind SourceKind { get; }
}
