using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    public const long MaximumDocumentBytes = 64 * 1024;

    public const string SettingsDirectoryName = "CodexWallpaper";

    public const string SettingsFileName = "ui-settings.json";

    private readonly string _preferencesPath;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposeState;

    public AppPreferencesStore(
        string preferencesPath,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferencesPath);
        _preferencesPath = Path.GetFullPath(preferencesPath);
        _serializerOptions = CreateSerializerOptions(serializerOptions);
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
            if (!File.Exists(_preferencesPath))
            {
                return AppPreferencesV1.CreateDefault();
            }

            try
            {
                await using var stream = new FileStream(
                    _preferencesPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaximumDocumentBytes)
                {
                    throw new AppPreferencesStoreException(
                        AppPreferencesStoreOperation.Read,
                        "The UI preferences document exceeds the size limit.");
                }

                var preferences = await JsonSerializer.DeserializeAsync<AppPreferencesV1>(
                    stream,
                    _serializerOptions,
                    cancellationToken).ConfigureAwait(false);
                if (preferences is null)
                {
                    throw new AppPreferencesStoreException(
                        AppPreferencesStoreOperation.Read,
                        "The UI preferences document is empty.");
                }

                try
                {
                    return preferences.Snapshot();
                }
                catch (AppPreferencesValidationException exception)
                {
                    throw new AppPreferencesStoreException(
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
                throw new AppPreferencesStoreException(
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
            var directoryPath = Path.GetDirectoryName(_preferencesPath)
                ?? throw new AppPreferencesStoreException(
                    AppPreferencesStoreOperation.Write,
                    "The UI preferences location is unavailable.");
            string? temporaryPath = null;

            try
            {
                Directory.CreateDirectory(directoryPath);
                temporaryPath = Path.Combine(
                    directoryPath,
                    $".{Path.GetFileName(_preferencesPath)}.{Guid.NewGuid():N}.tmp");

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
                PublishTemporaryFile(temporaryPath);
                temporaryPath = null;
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
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
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

    private void PublishTemporaryFile(string temporaryPath)
    {
        if (File.Exists(_preferencesPath))
        {
            File.Replace(
                temporaryPath,
                _preferencesPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            return;
        }

        File.Move(temporaryPath, _preferencesPath);
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
            // Best-effort cleanup of a private, unpublished temporary file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a private, unpublished temporary file.
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
}

public enum AppPreferencesStoreOperation
{
    Read = 0,
    Write,
    Reset,
}

public sealed class AppPreferencesStoreException : IOException
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
