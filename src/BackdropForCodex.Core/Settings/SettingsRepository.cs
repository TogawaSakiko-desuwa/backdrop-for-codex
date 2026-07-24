using System.Text.Json;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.Core.Settings;

/// <summary>
/// Owns the Settings V2 document, strict V1 migration, and same-directory atomic publication.
/// </summary>
public sealed class SettingsRepository : ISettingsRepository
{
    public const long MaximumDocumentBytes = 1024 * 1024;

    public const string Version1BackupFileName = "settings.v1.backup.json";

    private readonly string _settingsPath;
    private readonly string _version1BackupPath;
    private readonly SettingsDocumentCodec _documentCodec;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposeState;

    public SettingsRepository(
        string settingsPath,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);

        var directoryPath = Path.GetDirectoryName(_settingsPath)
            ?? throw new ArgumentException(
                "The settings location must have a parent directory.",
                nameof(settingsPath));
        _version1BackupPath = Path.Combine(directoryPath, Version1BackupFileName);

        if (string.Equals(
                _settingsPath,
                _version1BackupPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The settings document cannot use the reserved V1 backup name.",
                nameof(settingsPath));
        }

        _documentCodec = new SettingsDocumentCodec(serializerOptions);
    }

    public bool HasVersion1Backup
    {
        get
        {
            ThrowIfDisposed();
            return File.Exists(_version1BackupPath);
        }
    }

    public async Task<SettingsLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SettingsV2> SaveAsync(
        SettingsV2 settings,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        var canonicalSettings = settings.Snapshot();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var expectedDocument = await EnsureExistingDocumentCanBeReplacedAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            await PublishAsync(
                    canonicalSettings,
                    cancellationToken,
                    expectedDocument)
                .ConfigureAwait(false);
            return canonicalSettings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SettingsLoadResult> RestoreVersion1BackupAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            byte[] backupBytes;
            try
            {
                backupBytes = await ReadDocumentAsync(
                        _version1BackupPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return Recovery(SettingsRecoveryReason.Version1BackupInvalid);
            }

            SettingsV1 version1;
            try
            {
                var schemaVersion = SettingsDocumentCodec.ReadSchemaVersion(backupBytes);
                if (schemaVersion != SettingsV1.CurrentSchemaVersion)
                {
                    return Recovery(SettingsRecoveryReason.Version1BackupInvalid);
                }

                version1 = _documentCodec.DeserializeVersion1(backupBytes);
            }
            catch (Exception exception) when (
                exception is JsonException or SettingsValidationException)
            {
                return Recovery(SettingsRecoveryReason.Version1BackupInvalid);
            }

            try
            {
                var migrated = SettingsV1Migrator.Migrate(version1);
                await PublishAsync(migrated, cancellationToken).ConfigureAwait(false);
                return new SettingsLoadResult.Ready(
                    migrated,
                    MigratedFromVersion1: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                JsonException or
                SettingsValidationException or
                MediaReferenceValidationException or
                ArgumentException or
                NotSupportedException)
            {
                return Recovery(SettingsRecoveryReason.MigrationFailed);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SettingsV2> ResetAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                DeleteFileIfPresent(_settingsPath, clearReadOnly: true);
                DeleteFileIfPresent(_version1BackupPath, clearReadOnly: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new SettingsRepositoryException(
                    "The settings repository could not be reset.",
                    exception);
            }

            return SettingsV2.CreateDefault().Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _ = Interlocked.Exchange(ref _disposeState, 1);
        // The semaphore intentionally remains undisposed. An operation can pass the public
        // disposed check immediately before Dispose and already be waiting on the gate; retaining
        // the private semaphore lets that admitted caller observe the second check and unwind
        // without masking a completed write or becoming permanently stranded.
        GC.SuppressFinalize(this);
    }

    private async Task<SettingsLoadResult> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return new SettingsLoadResult.Ready(
                SettingsV2.CreateDefault().Snapshot(),
                MigratedFromVersion1: false);
        }

        byte[] documentBytes;
        try
        {
            documentBytes = await ReadDocumentAsync(_settingsPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SettingsDocumentTooLargeException)
        {
            return Recovery(SettingsRecoveryReason.DocumentTooLarge);
        }
        catch (FileNotFoundException)
        {
            return new SettingsLoadResult.Ready(
                SettingsV2.CreateDefault().Snapshot(),
                MigratedFromVersion1: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Recovery(SettingsRecoveryReason.DocumentCouldNotBeRead);
        }

        int schemaVersion;
        try
        {
            schemaVersion = SettingsDocumentCodec.ReadSchemaVersion(documentBytes);
        }
        catch (JsonException)
        {
            return Recovery(SettingsRecoveryReason.InvalidDocument);
        }

        if (schemaVersion > SettingsV2.CurrentSchemaVersion)
        {
            return new SettingsLoadResult.FutureReadOnly(
                schemaVersion,
                HasVersion1Backup);
        }

        if (schemaVersion == SettingsV2.CurrentSchemaVersion)
        {
            try
            {
                var settings = _documentCodec.DeserializeVersion2(documentBytes);
                return new SettingsLoadResult.Ready(
                    settings,
                    MigratedFromVersion1: false);
            }
            catch (Exception exception) when (
                exception is JsonException or
                SettingsValidationException or
                MediaReferenceValidationException)
            {
                return Recovery(SettingsRecoveryReason.InvalidDocument);
            }
        }

        if (schemaVersion != SettingsV1.CurrentSchemaVersion)
        {
            return Recovery(SettingsRecoveryReason.InvalidDocument);
        }

        SettingsV1 version1;
        try
        {
            version1 = _documentCodec.DeserializeVersion1(documentBytes);
        }
        catch (Exception exception) when (
            exception is JsonException or SettingsValidationException)
        {
            return Recovery(SettingsRecoveryReason.InvalidDocument);
        }

        try
        {
            await EnsureVersion1BackupAsync(documentBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Version1BackupConflictException)
        {
            return Recovery(SettingsRecoveryReason.Version1BackupConflict);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Recovery(SettingsRecoveryReason.MigrationFailed);
        }

        try
        {
            var migrated = SettingsV1Migrator.Migrate(version1);
            await PublishAsync(
                    migrated,
                    cancellationToken,
                    ExpectedDocumentState.Present(documentBytes))
                .ConfigureAwait(false);
            return new SettingsLoadResult.Ready(
                migrated,
                MigratedFromVersion1: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            SettingsValidationException or
            MediaReferenceValidationException or
            ArgumentException or
            NotSupportedException)
        {
            return Recovery(SettingsRecoveryReason.MigrationFailed);
        }
    }

    private SettingsLoadResult.RecoveryRequired Recovery(SettingsRecoveryReason reason) =>
        new(reason, File.Exists(_version1BackupPath));

    private async Task<ExpectedDocumentState> EnsureExistingDocumentCanBeReplacedAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return ExpectedDocumentState.Missing;
        }

        byte[] documentBytes;
        try
        {
            documentBytes = await ReadDocumentAsync(_settingsPath, cancellationToken)
                .ConfigureAwait(false);
            var schemaVersion = SettingsDocumentCodec.ReadSchemaVersion(documentBytes);
            if (schemaVersion != SettingsV2.CurrentSchemaVersion)
            {
                throw new SettingsRepositoryException(
                    "Save refused because the current document is not a writable V2 document.");
            }

            _ = _documentCodec.DeserializeVersion2(documentBytes);
            return ExpectedDocumentState.Present(documentBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SettingsRepositoryException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            SettingsValidationException or
            MediaReferenceValidationException)
        {
            throw new SettingsRepositoryException(
                "Save refused because the current document requires recovery.",
                exception);
        }
    }

    private async Task EnsureVersion1BackupAsync(
        byte[] originalBytes,
        CancellationToken cancellationToken)
    {
        if (File.Exists(_version1BackupPath))
        {
            await VerifyExistingVersion1BackupAsync(originalBytes, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var directoryPath = Path.GetDirectoryName(_version1BackupPath)
            ?? throw new SettingsRepositoryException(
                "The V1 backup location has no parent directory.");
        Directory.CreateDirectory(directoryPath);
        var temporaryPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(_version1BackupPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(originalBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            var verificationBytes = await ReadDocumentAsync(
                    temporaryPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!originalBytes.AsSpan().SequenceEqual(verificationBytes))
            {
                throw new IOException("The V1 backup could not be verified.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, _version1BackupPath);
            }
            catch (IOException) when (File.Exists(_version1BackupPath))
            {
                await VerifyExistingVersion1BackupAsync(originalBytes, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            SetReadOnly(_version1BackupPath);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private async Task VerifyExistingVersion1BackupAsync(
        byte[] originalBytes,
        CancellationToken cancellationToken)
    {
        byte[] backupBytes;
        try
        {
            backupBytes = await ReadDocumentAsync(
                    _version1BackupPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SettingsDocumentTooLargeException exception)
        {
            throw new Version1BackupConflictException(exception);
        }

        if (!originalBytes.AsSpan().SequenceEqual(backupBytes))
        {
            throw new Version1BackupConflictException();
        }

        SetReadOnly(_version1BackupPath);
    }

    private async Task PublishAsync(
        SettingsV2 settings,
        CancellationToken cancellationToken,
        ExpectedDocumentState? expectedDocument = null)
    {
        var documentBytes = _documentCodec.SerializeVersion2(settings);
        var directoryPath = Path.GetDirectoryName(_settingsPath)
            ?? throw new SettingsRepositoryException(
                "The settings location has no parent directory.");

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(directoryPath);
            temporaryPath = Path.Combine(
                directoryPath,
                $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(documentBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (expectedDocument is not null)
            {
                await EnsureExpectedDocumentUnchangedAsync(
                        expectedDocument,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            PublishTemporaryFile(temporaryPath);
            temporaryPath = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SettingsRepositoryException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new SettingsRepositoryException(
                "Settings could not be saved.",
                exception);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static async Task<byte[]> ReadDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length > MaximumDocumentBytes)
        {
            throw new SettingsDocumentTooLargeException();
        }

        using var memory = new MemoryStream(
            capacity: checked((int)Math.Min(stream.Length, MaximumDocumentBytes)));
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        if (memory.Length > MaximumDocumentBytes)
        {
            throw new SettingsDocumentTooLargeException();
        }

        return memory.ToArray();
    }

    private async Task EnsureExpectedDocumentUnchangedAsync(
        ExpectedDocumentState expectedDocument,
        CancellationToken cancellationToken)
    {
        if (!expectedDocument.Exists)
        {
            if (File.Exists(_settingsPath))
            {
                throw new SettingsRepositoryException(
                    "Settings changed while an update was being prepared.");
            }

            return;
        }

        byte[] currentBytes;
        try
        {
            currentBytes = await ReadDocumentAsync(_settingsPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new SettingsRepositoryException(
                "Settings changed while an update was being prepared.",
                exception);
        }

        if (!expectedDocument.Bytes.AsSpan().SequenceEqual(currentBytes))
        {
            throw new SettingsRepositoryException(
                "Settings changed while an update was being prepared.");
        }
    }

    private void PublishTemporaryFile(string temporaryPath)
    {
        if (File.Exists(_settingsPath))
        {
            File.Replace(
                temporaryPath,
                _settingsPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            return;
        }

        File.Move(temporaryPath, _settingsPath);
    }

    private static void SetReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) == 0)
        {
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0)
        {
            throw new IOException("The V1 backup could not be marked read-only.");
        }
    }

    private static void DeleteFileIfPresent(string path, bool clearReadOnly)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var originalAttributes = File.GetAttributes(path);
        try
        {
            if (clearReadOnly &&
                (originalAttributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(
                    path,
                    originalAttributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.SetAttributes(path, originalAttributes);
                }
                catch (Exception attributeException) when (
                    attributeException is IOException or UnauthorizedAccessException)
                {
                    // Preserve the deletion failure as the primary error. The caller reports
                    // reset failure, and the next load still treats the surviving backup as
                    // recovery material rather than publishing new settings over it.
                }
            }

            throw;
        }
    }

    private static void TryDeleteTemporaryFile(string? temporaryPath)
    {
        if (temporaryPath is null)
        {
            return;
        }

        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
            // Best effort cleanup of a private, unpublished temporary file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup of a private, unpublished temporary file.
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    private sealed class SettingsDocumentTooLargeException : IOException
    {
    }

    private sealed class Version1BackupConflictException : IOException
    {
        internal Version1BackupConflictException()
        {
        }

        internal Version1BackupConflictException(Exception innerException)
            : base("The existing V1 backup does not match the source document.", innerException)
        {
        }
    }

    private sealed record ExpectedDocumentState(bool Exists, byte[] Bytes)
    {
        internal static ExpectedDocumentState Missing { get; } =
            new(Exists: false, Array.Empty<byte>());

        internal static ExpectedDocumentState Present(byte[] bytes) =>
            new(Exists: true, bytes.ToArray());
    }
}
