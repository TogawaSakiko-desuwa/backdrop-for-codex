using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BackdropForCodex.Core.Tests")]

namespace BackdropForCodex.App.Services.Preferences;

public interface IAppPreferencesStore : IDisposable
{
    Task<AppPreferencesV1> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        AppPreferencesV1 preferences,
        CancellationToken cancellationToken = default);

    Task ResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists UI-only preferences with an atomic same-directory replacement.
/// </summary>
public sealed class AppPreferencesStore : IAppPreferencesStore
{
    private const string DeprecatedWallpaperEngineInstallRootProperty =
        "wallpaperEngineInstallRootPath";

    public const long MaximumDocumentBytes = 64 * 1024;

    public const string SettingsDirectoryName = "CodexWallpaper";

    public const string SettingsFileName = "ui-settings.json";

    private const string ProtectedExistingDocumentMessage =
        "The existing UI preferences document cannot be safely classified. " +
        "Reset it explicitly before saving replacement preferences.";

    private const string ChangedExistingDocumentMessage =
        "UI preferences changed while an update was being prepared.";

    private const string ChangedDuringReadMessage =
        "UI preferences changed while they were being read.";

    private const string PendingRecoveryDocumentMessage =
        "A pending UI preferences recovery document requires an explicit reset.";

    private readonly string _preferencesPath;
    private readonly string _pendingRecoveryPath;
    private readonly string _pendingRollbackPath;
    private readonly string _transactionGuardPath;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly AppPreferencesStoreTestHooks? _testHooks;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposeState;

    public AppPreferencesStore(
        string preferencesPath,
        JsonSerializerOptions? serializerOptions = null)
        : this(preferencesPath, serializerOptions, testHooks: null)
    {
    }

    internal AppPreferencesStore(
        string preferencesPath,
        JsonSerializerOptions? serializerOptions,
        AppPreferencesStoreTestHooks? testHooks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferencesPath);
        _preferencesPath = Path.GetFullPath(preferencesPath);
        var directoryPath = Path.GetDirectoryName(_preferencesPath)
            ?? throw new ArgumentException(
                "The UI preferences location must have a parent directory.",
                nameof(preferencesPath));
        _pendingRecoveryPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(_preferencesPath)}.pending-recovery");
        _pendingRollbackPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(_preferencesPath)}.pending-rollback");
        _transactionGuardPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(_preferencesPath)}.transaction-in-progress");
        _serializerOptions = CreateSerializerOptions(serializerOptions);
        _testHooks = testHooks;
    }

    public static AppPreferencesStore CreateForCurrentUser()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new AppPreferencesStoreException(
                AppPreferencesStoreOperation.Read,
                "The UI preferences location is unavailable.");
        }

        return new AppPreferencesStore(
            Path.Combine(localAppData, SettingsDirectoryName, SettingsFileName));
    }

    public async Task<AppPreferencesV1> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNoPendingRecovery(AppPreferencesStoreOperation.Read);
            _testHooks?.AfterInitialRecoveryCheck?.Invoke();
            try
            {
                ExpectedDocumentState documentState;
                try
                {
                    documentState = await ReadExistingDocumentStateAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (PreferencesDocumentTooLargeException exception)
                {
                    throw new ProtectedPreferencesDocumentException(
                        AppPreferencesStoreOperation.Read,
                        "The UI preferences document exceeds the size limit.",
                        exception);
                }

                if (!documentState.Exists)
                {
                    await EnsureLoadedDocumentStillCurrentAsync(
                            documentState,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return AppPreferencesV1.CreateDefault();
                }

                AppPreferencesV1? preferences;
                bool removedDeprecatedInstallRoot;
                using (var stream = new MemoryStream(documentState.Bytes, writable: false))
                {
                    (preferences, removedDeprecatedInstallRoot) =
                        await DeserializePreferencesAsync(stream, cancellationToken)
                            .ConfigureAwait(false);
                }

                if (preferences is null)
                {
                    throw new ProtectedPreferencesDocumentException(
                        AppPreferencesStoreOperation.Read,
                        "The UI preferences document is empty.");
                }

                try
                {
                    var snapshot = preferences.Snapshot();
                    if (removedDeprecatedInstallRoot)
                    {
                        documentState = await WriteSnapshotCoreAsync(
                                snapshot,
                                documentState,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await EnsureLoadedDocumentStillCurrentAsync(
                            documentState,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return snapshot;
                }
                catch (AppPreferencesValidationException exception)
                {
                    throw new ProtectedPreferencesDocumentException(
                        AppPreferencesStoreOperation.Read,
                        "The UI preferences document failed validation.",
                        exception);
                }
            }
            catch (AppPreferencesStoreException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new ProtectedPreferencesDocumentException(
                    AppPreferencesStoreOperation.Read,
                    "The UI preferences document is not valid JSON.",
                    exception);
            }
            catch (IOException exception)
            {
                throw new AppPreferencesStoreException(
                    AppPreferencesStoreOperation.Read,
                    "The UI preferences document could not be read.",
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new AppPreferencesStoreException(
                    AppPreferencesStoreOperation.Read,
                    "The UI preferences document could not be read.",
                    exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        AppPreferencesV1 preferences,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(preferences);
        var snapshot = preferences.Snapshot();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                EnsureNoPendingRecovery(AppPreferencesStoreOperation.Write);
                var expectedDocument = await EnsureExistingDocumentCanBeReplacedAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
                await WriteSnapshotCoreAsync(snapshot, expectedDocument, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AppPreferencesStoreException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new AppPreferencesStoreException(
                    AppPreferencesStoreOperation.Write,
                    "UI preferences could not be serialized.",
                    exception);
            }
            catch (IOException exception)
            {
                throw new AppPreferencesStoreException(
                    AppPreferencesStoreOperation.Write,
                    "UI preferences could not be saved.",
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new AppPreferencesStoreException(
                    AppPreferencesStoreOperation.Write,
                    "UI preferences could not be saved.",
                    exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(_preferencesPath);
                File.Delete(_pendingRecoveryPath);
                File.Delete(_pendingRollbackPath);
                File.Delete(_transactionGuardPath);
            }
            catch (IOException exception)
            {
                throw new AppPreferencesStoreException(
                    AppPreferencesStoreOperation.Reset,
                    "UI preferences could not be reset.",
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new AppPreferencesStoreException(
                    AppPreferencesStoreOperation.Reset,
                    "UI preferences could not be reset.",
                    exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _gate.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static JsonSerializerOptions CreateSerializerOptions(
        JsonSerializerOptions? serializerOptions)
    {
        var options = serializerOptions is null
            ? new JsonSerializerOptions(JsonSerializerDefaults.Web)
            : new JsonSerializerOptions(serializerOptions);

        options.WriteIndented = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private async ValueTask<(AppPreferencesV1? Preferences, bool RemovedDeprecatedInstallRoot)>
        DeserializePreferencesAsync(
            Stream stream,
            CancellationToken cancellationToken,
            AppPreferencesStoreOperation operation = AppPreferencesStoreOperation.Read)
    {
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        ThrowIfFutureSchemaVersion(
            document.RootElement,
            operation);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (
                document.RootElement.Deserialize<AppPreferencesV1>(_serializerOptions),
                RemovedDeprecatedInstallRoot: false);
        }

        var removedDeprecatedInstallRoot = document.RootElement
            .EnumerateObject()
            .Any(IsDeprecatedWallpaperEngineInstallRootProperty);
        if (!removedDeprecatedInstallRoot)
        {
            return (
                document.RootElement.Deserialize<AppPreferencesV1>(_serializerOptions),
                RemovedDeprecatedInstallRoot: false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var sanitized = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(sanitized))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!IsDeprecatedWallpaperEngineInstallRootProperty(property))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        sanitized.Position = 0;
        var preferences = await JsonSerializer.DeserializeAsync<AppPreferencesV1>(
            sanitized,
            _serializerOptions,
            cancellationToken).ConfigureAwait(false);
        return (preferences, RemovedDeprecatedInstallRoot: true);
    }

    private static void ThrowIfFutureSchemaVersion(
        JsonElement root,
        AppPreferencesStoreOperation operation)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(
                    property.Name,
                    nameof(AppPreferencesV1.SchemaVersion),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Number)
            {
                return;
            }

            if (!property.Value.TryGetInt32(out var schemaVersion))
            {
                var rawSchemaVersion = property.Value.GetRawText();
                if (rawSchemaVersion.All(char.IsAsciiDigit))
                {
                    throw new FuturePreferencesVersionException(
                        operation,
                        rawSchemaVersion);
                }

                return;
            }

            if (schemaVersion > AppPreferencesV1.CurrentSchemaVersion)
            {
                throw new FuturePreferencesVersionException(operation, schemaVersion);
            }

            return;
        }
    }

    private static bool IsDeprecatedWallpaperEngineInstallRootProperty(
        JsonProperty property) =>
        string.Equals(
            property.Name,
            DeprecatedWallpaperEngineInstallRootProperty,
            StringComparison.OrdinalIgnoreCase);

    private async Task<ExpectedDocumentState> EnsureExistingDocumentCanBeReplacedAsync(
        CancellationToken cancellationToken)
    {
        ExpectedDocumentState documentState;
        try
        {
            documentState = await ReadExistingDocumentStateAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PreferencesDocumentTooLargeException exception)
        {
            throw new ProtectedPreferencesDocumentException(
                AppPreferencesStoreOperation.Write,
                ProtectedExistingDocumentMessage,
                exception);
        }

        if (!documentState.Exists)
        {
            return documentState;
        }

        await EnsureWritableDocumentAsync(documentState.Bytes, cancellationToken)
            .ConfigureAwait(false);
        return documentState;
    }

    private async Task EnsureWritableDocumentAsync(
        byte[] documentBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new MemoryStream(documentBytes, writable: false);
            var (existing, _) = await DeserializePreferencesAsync(
                    stream,
                    cancellationToken,
                    AppPreferencesStoreOperation.Write)
                .ConfigureAwait(false);
            if (existing is null)
            {
                throw new ProtectedPreferencesDocumentException(
                    AppPreferencesStoreOperation.Write,
                    ProtectedExistingDocumentMessage);
            }

            try
            {
                existing.Validate();
            }
            catch (AppPreferencesValidationException exception)
            {
                throw new ProtectedPreferencesDocumentException(
                    AppPreferencesStoreOperation.Write,
                    ProtectedExistingDocumentMessage,
                    exception);
            }
        }
        catch (JsonException exception)
        {
            throw new ProtectedPreferencesDocumentException(
                AppPreferencesStoreOperation.Write,
                ProtectedExistingDocumentMessage,
                exception);
        }
    }

    private async Task EnsureExpectedDocumentUnchangedAsync(
        ExpectedDocumentState expectedDocument,
        CancellationToken cancellationToken)
    {
        ExpectedDocumentState currentDocument;
        try
        {
            currentDocument = await ReadExistingDocumentStateAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PreferencesDocumentTooLargeException exception)
        {
            throw new ProtectedPreferencesDocumentException(
                AppPreferencesStoreOperation.Write,
                ProtectedExistingDocumentMessage,
                exception);
        }

        if (expectedDocument.Matches(currentDocument))
        {
            return;
        }

        if (currentDocument.Exists)
        {
            await EnsureWritableDocumentAsync(currentDocument.Bytes, cancellationToken)
                .ConfigureAwait(false);
        }

        throw CreateChangedDocumentException();
    }

    private async Task EnsureLoadedDocumentStillCurrentAsync(
        ExpectedDocumentState loadedDocument,
        CancellationToken cancellationToken)
    {
        EnsureNoPendingRecovery(AppPreferencesStoreOperation.Read);

        ExpectedDocumentState currentDocument;
        try
        {
            currentDocument = await ReadExistingDocumentStateAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PreferencesDocumentTooLargeException exception)
        {
            throw new ProtectedPreferencesDocumentException(
                AppPreferencesStoreOperation.Read,
                "The UI preferences document exceeds the size limit.",
                exception);
        }

        if (!loadedDocument.Matches(currentDocument))
        {
            throw new AppPreferencesStoreException(
                AppPreferencesStoreOperation.Read,
                ChangedDuringReadMessage);
        }

        EnsureNoPendingRecovery(AppPreferencesStoreOperation.Read);
    }

    private async Task<ExpectedDocumentState> ReadExistingDocumentStateAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var documentBytes = await ReadDocumentBytesAsync(
                    _preferencesPath,
                    cancellationToken)
                .ConfigureAwait(false);
            return ExpectedDocumentState.Present(documentBytes);
        }
        catch (FileNotFoundException)
        {
            return ExpectedDocumentState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return ExpectedDocumentState.Missing;
        }
    }

    private static async Task<byte[]> ReadDocumentBytesAsync(
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
            throw new PreferencesDocumentTooLargeException();
        }

        using var memory = new MemoryStream(
            capacity: checked((int)Math.Min(stream.Length, MaximumDocumentBytes)));
        var buffer = new byte[4096];
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return memory.ToArray();
            }

            if (memory.Length + bytesRead > MaximumDocumentBytes)
            {
                throw new PreferencesDocumentTooLargeException();
            }

            memory.Write(buffer, 0, bytesRead);
        }
    }

    private async Task<ExpectedDocumentState> WriteSnapshotCoreAsync(
        AppPreferencesV1 snapshot,
        ExpectedDocumentState expectedDocument,
        CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(_preferencesPath)
            ?? throw new IOException("The UI preferences location is unavailable.");
        string? temporaryPath = null;
        string? recoveryPath = null;
        string? guardPath = null;
        var ownsRecovery = false;
        var ownsGuard = false;
        var preserveRecovery = false;
        var preserveGuard = false;
        try
        {
            Directory.CreateDirectory(directoryPath);
            temporaryPath = CreatePrivatePath(directoryPath, "tmp");

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    snapshot,
                    _serializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _testHooks?.AfterTemporaryFilePrepared?.Invoke();
            byte[] candidateBytes;
            try
            {
                candidateBytes = await ReadDocumentBytesAsync(temporaryPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PreferencesDocumentTooLargeException exception)
            {
                throw new AppPreferencesStoreException(
                    AppPreferencesStoreOperation.Write,
                    "The serialized UI preferences document exceeds the size limit.",
                    exception);
            }

            await EnsureExpectedDocumentUnchangedAsync(expectedDocument, cancellationToken)
                .ConfigureAwait(false);
            _testHooks?.BeforePublish?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            recoveryPath = _pendingRecoveryPath;
            guardPath = _transactionGuardPath;
            CreateTransactionGuard();
            ownsGuard = true;
            CreatePendingRecoveryMarker();
            ownsRecovery = true;
            if (!expectedDocument.Exists)
            {
                await PublishMissingExpectedDocumentAsync(temporaryPath)
                    .ConfigureAwait(false);
                temporaryPath = null;
                preserveRecovery = true;
                preserveGuard = true;
                File.Delete(recoveryPath);
                ownsRecovery = false;
                recoveryPath = null;
                preserveRecovery = false;
                File.Delete(guardPath);
                ownsGuard = false;
                guardPath = null;
                preserveGuard = false;
                return ExpectedDocumentState.Present(candidateBytes);
            }

            preserveRecovery = true;
            preserveGuard = true;
            try
            {
                ReplaceFile(
                    temporaryPath,
                    _preferencesPath,
                    recoveryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                if (await CanProveInitialReplaceDidNotTransitionAsync(
                        expectedDocument,
                        candidateBytes,
                        temporaryPath,
                        recoveryPath)
                    .ConfigureAwait(false))
                {
                    preserveRecovery = false;
                    preserveGuard = false;
                }

                throw;
            }

            temporaryPath = null;
            _testHooks?.AfterReplace?.Invoke();

            await CompleteExistingDocumentPublicationAsync(
                    expectedDocument,
                    candidateBytes,
                    recoveryPath)
                .ConfigureAwait(false);
            ownsRecovery = false;
            ownsGuard = false;
            preserveRecovery = false;
            preserveGuard = false;
            return ExpectedDocumentState.Present(candidateBytes);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
            if (ownsRecovery && !preserveRecovery)
            {
                TryDeleteTemporaryFile(recoveryPath);
            }

            if (ownsGuard && !preserveGuard)
            {
                TryDeleteTemporaryFile(guardPath);
            }
        }
    }

    private async Task PublishMissingExpectedDocumentAsync(string temporaryPath)
    {
        try
        {
            File.Move(temporaryPath, _preferencesPath);
        }
        catch (IOException exception)
        {
            await ThrowClassifiedPublishConflictAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ThrowClassifiedPublishConflictAsync(IOException publishException)
    {
        ExpectedDocumentState competingDocument;
        try
        {
            competingDocument = await ReadExistingDocumentStateAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (PreferencesDocumentTooLargeException exception)
        {
            throw new ProtectedPreferencesDocumentException(
                AppPreferencesStoreOperation.Write,
                ProtectedExistingDocumentMessage,
                exception);
        }

        if (!competingDocument.Exists)
        {
            throw publishException;
        }

        await EnsureWritableDocumentAsync(competingDocument.Bytes, CancellationToken.None)
            .ConfigureAwait(false);
        throw CreateChangedDocumentException(publishException);
    }

    private async Task<bool> CanProveInitialReplaceDidNotTransitionAsync(
        ExpectedDocumentState expectedDocument,
        byte[] candidateBytes,
        string temporaryPath,
        string recoveryPath)
    {
        try
        {
            var currentDocument = await ReadExistingDocumentStateAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (!expectedDocument.Matches(currentDocument))
            {
                return false;
            }

            var recoveryBytes = await ReadDocumentBytesAsync(
                    recoveryPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (recoveryBytes.Length != 0)
            {
                return false;
            }

            var currentCandidateBytes = await ReadDocumentBytesAsync(
                    temporaryPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return candidateBytes.AsSpan().SequenceEqual(currentCandidateBytes);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task CompleteExistingDocumentPublicationAsync(
        ExpectedDocumentState expectedDocument,
        byte[] candidateBytes,
        string recoveryPath)
    {
        byte[]? displacedDocumentBytes = null;
        var displacedDocumentIsOversized = false;
        try
        {
            displacedDocumentBytes = await ReadDocumentBytesAsync(
                    recoveryPath,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (PreferencesDocumentTooLargeException)
        {
            displacedDocumentIsOversized = true;
        }

        var displacedDocumentMatchesExpected =
            !displacedDocumentIsOversized &&
            expectedDocument.Bytes.AsSpan().SequenceEqual(displacedDocumentBytes);

        ExpectedDocumentState publishedDocument;
        try
        {
            publishedDocument = await ReadExistingDocumentStateAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (PreferencesDocumentTooLargeException exception)
        {
            throw CreateChangedDocumentException(exception);
        }

        if (displacedDocumentMatchesExpected)
        {
            if (!publishedDocument.Exists ||
                !candidateBytes.AsSpan().SequenceEqual(publishedDocument.Bytes))
            {
                throw CreateChangedDocumentException();
            }

            File.Delete(recoveryPath);
            File.Delete(_transactionGuardPath);
            return;
        }

        if (!publishedDocument.Exists ||
            !candidateBytes.AsSpan().SequenceEqual(publishedDocument.Bytes))
        {
            throw CreateChangedDocumentException();
        }

        var rollbackPath = _pendingRollbackPath;
        var ownsRollback = false;
        var preserveRollback = false;
        try
        {
            CreateDurableMarker(rollbackPath);
            ownsRollback = true;
            _testHooks?.BeforeRollback?.Invoke();
            preserveRollback = true;
            ReplaceFile(
                recoveryPath,
                _preferencesPath,
                rollbackPath);
            _testHooks?.AfterRollback?.Invoke();

            byte[] rolledAsideBytes;
            try
            {
                rolledAsideBytes = await ReadDocumentBytesAsync(
                        rollbackPath,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                if (TryPromoteRollbackToPendingRecovery(rollbackPath))
                {
                    ownsRollback = false;
                    preserveRollback = false;
                }

                throw CreateChangedDocumentException(exception);
            }

            if (!candidateBytes.AsSpan().SequenceEqual(rolledAsideBytes))
            {
                if (TryPromoteRollbackToPendingRecovery(rollbackPath))
                {
                    ownsRollback = false;
                    preserveRollback = false;
                }

                throw CreateChangedDocumentException();
            }

            File.Delete(rollbackPath);
            ownsRollback = false;
            preserveRollback = false;
        }
        finally
        {
            if (ownsRollback && !preserveRollback)
            {
                TryDeleteTemporaryFile(rollbackPath);
            }
        }

        File.Delete(_transactionGuardPath);

        if (displacedDocumentIsOversized)
        {
            throw new ProtectedPreferencesDocumentException(
                AppPreferencesStoreOperation.Write,
                ProtectedExistingDocumentMessage);
        }

        await EnsureWritableDocumentAsync(
                displacedDocumentBytes!,
                CancellationToken.None)
            .ConfigureAwait(false);
        throw CreateChangedDocumentException();
    }

    private void EnsureNoPendingRecovery(AppPreferencesStoreOperation operation)
    {
        EnsureRecoveryPathIsMissing(_transactionGuardPath, operation);
        EnsureRecoveryPathIsMissing(_pendingRecoveryPath, operation);
        EnsureRecoveryPathIsMissing(_pendingRollbackPath, operation);
    }

    private static void EnsureRecoveryPathIsMissing(
        string path,
        AppPreferencesStoreOperation operation)
    {
        try
        {
            _ = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (IOException exception)
        {
            throw new ProtectedPreferencesDocumentException(
                operation,
                PendingRecoveryDocumentMessage,
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ProtectedPreferencesDocumentException(
                operation,
                PendingRecoveryDocumentMessage,
                exception);
        }

        throw new ProtectedPreferencesDocumentException(
            operation,
            PendingRecoveryDocumentMessage);
    }

    private void CreateTransactionGuard() =>
        CreateDurableMarker(_transactionGuardPath);

    private void CreatePendingRecoveryMarker() =>
        CreateDurableMarker(_pendingRecoveryPath);

    private static void CreateDurableMarker(string path)
    {
        using var marker = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
        marker.Flush(flushToDisk: true);
    }

    private void ReplaceFile(string sourcePath, string destinationPath, string backupPath)
    {
        if (_testHooks?.ReplaceFile is { } replaceFile)
        {
            replaceFile(sourcePath, destinationPath, backupPath);
            return;
        }

        File.Replace(
            sourcePath,
            destinationPath,
            backupPath,
            ignoreMetadataErrors: true);
    }

    private bool TryPromoteRollbackToPendingRecovery(string rollbackPath)
    {
        try
        {
            File.Move(rollbackPath, _pendingRecoveryPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string CreatePrivatePath(string directoryPath, string suffix) =>
        Path.Combine(
            directoryPath,
            $".{Path.GetFileName(_preferencesPath)}.{Guid.NewGuid():N}.{suffix}");

    private static AppPreferencesStoreException CreateChangedDocumentException(
        Exception? innerException = null) =>
        innerException is null
            ? new AppPreferencesStoreException(
                AppPreferencesStoreOperation.Write,
                ChangedExistingDocumentMessage)
            : new AppPreferencesStoreException(
                AppPreferencesStoreOperation.Write,
                ChangedExistingDocumentMessage,
                innerException);

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
            // Best-effort cleanup of a private, unpublished temporary file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a private, unpublished temporary file.
        }
    }

    private sealed record ExpectedDocumentState(bool Exists, byte[] Bytes)
    {
        internal static ExpectedDocumentState Missing { get; } =
            new(Exists: false, Array.Empty<byte>());

        internal static ExpectedDocumentState Present(byte[] bytes) =>
            new(Exists: true, bytes.ToArray());

        internal bool Matches(ExpectedDocumentState other) =>
            Exists == other.Exists &&
            (!Exists || Bytes.AsSpan().SequenceEqual(other.Bytes));
    }

    private sealed class PreferencesDocumentTooLargeException : IOException
    {
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
}

internal sealed record AppPreferencesStoreTestHooks(
    Action? AfterInitialRecoveryCheck = null,
    Action? AfterTemporaryFilePrepared = null,
    Action? BeforePublish = null,
    Action? AfterReplace = null,
    Action? BeforeRollback = null,
    Action? AfterRollback = null,
    Action<string, string, string>? ReplaceFile = null);

public enum AppPreferencesStoreOperation
{
    Read = 0,
    Write,
    Reset,
}

public class AppPreferencesStoreException : IOException
{
    public AppPreferencesStoreException(
        AppPreferencesStoreOperation operation,
        string message)
        : base(message)
    {
        Operation = operation;
    }

    public AppPreferencesStoreException(
        AppPreferencesStoreOperation operation,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Operation = operation;
    }

    public AppPreferencesStoreOperation Operation { get; }
}

public abstract class ProtectedPreferencesException : AppPreferencesStoreException
{
    protected ProtectedPreferencesException(
        AppPreferencesStoreOperation operation,
        string message)
        : base(operation, message)
    {
    }

    protected ProtectedPreferencesException(
        AppPreferencesStoreOperation operation,
        string message,
        Exception innerException)
        : base(operation, message, innerException)
    {
    }
}

public sealed class ProtectedPreferencesMutationException : ProtectedPreferencesException
{
    public ProtectedPreferencesMutationException()
        : base(
            AppPreferencesStoreOperation.Write,
            "UI preferences are protected and read-only. " +
            "Reset the app explicitly before replacing them.")
    {
    }
}

public class ProtectedPreferencesDocumentException : ProtectedPreferencesException
{
    public ProtectedPreferencesDocumentException(
        AppPreferencesStoreOperation operation,
        string message)
        : base(operation, message)
    {
    }

    public ProtectedPreferencesDocumentException(
        AppPreferencesStoreOperation operation,
        string message,
        Exception innerException)
        : base(operation, message, innerException)
    {
    }
}

public sealed class FuturePreferencesVersionException : ProtectedPreferencesDocumentException
{
    private const int MaximumSchemaVersionDisplayDigits = 64;

    public FuturePreferencesVersionException(
        AppPreferencesStoreOperation operation,
        int schemaVersion)
        : base(
            operation,
            $"UI preferences schema version {schemaVersion} is newer than the supported " +
            $"version {AppPreferencesV1.CurrentSchemaVersion}.")
    {
        SchemaVersion = schemaVersion;
        SchemaVersionDisplay = schemaVersion.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public FuturePreferencesVersionException(
        AppPreferencesStoreOperation operation,
        string schemaVersionDisplay)
        : base(
            operation,
            $"UI preferences schema version {FormatSchemaVersionDisplay(schemaVersionDisplay)} " +
            "is unsupported by " +
            $"version {AppPreferencesV1.CurrentSchemaVersion}.")
    {
        SchemaVersionDisplay = FormatSchemaVersionDisplay(schemaVersionDisplay);
    }

    public int? SchemaVersion { get; }

    public string SchemaVersionDisplay { get; }

    private static string FormatSchemaVersionDisplay(string schemaVersionDisplay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersionDisplay);
        if (!schemaVersionDisplay.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                "The schema version display must contain only ASCII digits.",
                nameof(schemaVersionDisplay));
        }

        return schemaVersionDisplay.Length <= MaximumSchemaVersionDisplayDigits
            ? schemaVersionDisplay
            : schemaVersionDisplay[..MaximumSchemaVersionDisplayDigits] + "...";
    }
}
