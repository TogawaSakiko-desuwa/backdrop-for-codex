using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackdropForCodex.Core.Settings;

public interface ISettingsStore : IDisposable
{
    Task<SettingsV1> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SettingsV1 settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists settings as JSON and publishes updates with a same-directory atomic replacement.
/// </summary>
public sealed class SettingsStore : ISettingsStore, IDisposable
{
    public const long MaximumDocumentBytes = 1024 * 1024;

    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposeState;

    public SettingsStore(string settingsPath, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
        _serializerOptions = CreateSerializerOptions(serializerOptions);
    }

    public async Task<SettingsV1> LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return SettingsV1.CreateDefault();
            }

            try
            {
                await using var stream = new FileStream(
                    _settingsPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaximumDocumentBytes)
                {
                    throw new SettingsStoreException("The settings document exceeds the size limit.");
                }

                var settings = await JsonSerializer.DeserializeAsync<SettingsV1>(
                    stream,
                    _serializerOptions,
                    cancellationToken).ConfigureAwait(false);

                if (settings is null)
                {
                    throw new SettingsStoreException("The settings document is empty.");
                }

                try
                {
                    return settings.Snapshot();
                }
                catch (SettingsValidationException exception)
                {
                    throw new SettingsStoreException("The settings document failed validation.", exception);
                }
            }
            catch (SettingsStoreException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new SettingsStoreException("The settings document is not valid JSON.", exception);
            }
            catch (IOException exception)
            {
                throw new SettingsStoreException("The settings document could not be read.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new SettingsStoreException("The settings document could not be read.", exception);
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

    public async Task SaveAsync(SettingsV1 settings, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.Snapshot();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directoryPath = Path.GetDirectoryName(_settingsPath)
                ?? throw new SettingsStoreException("The settings location has no parent directory.");

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
            catch (SettingsStoreException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new SettingsStoreException("Settings could not be serialized.", exception);
            }
            catch (IOException exception)
            {
                throw new SettingsStoreException("Settings could not be saved.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new SettingsStoreException("Settings could not be saved.", exception);
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

    private static JsonSerializerOptions CreateSerializerOptions(JsonSerializerOptions? serializerOptions)
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
        if (File.Exists(_settingsPath))
        {
            File.Replace(temporaryPath, _settingsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }

        File.Move(temporaryPath, _settingsPath);
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
}

public sealed class SettingsStoreException : IOException
{
    public SettingsStoreException(string message)
        : base(message)
    {
    }

    public SettingsStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
