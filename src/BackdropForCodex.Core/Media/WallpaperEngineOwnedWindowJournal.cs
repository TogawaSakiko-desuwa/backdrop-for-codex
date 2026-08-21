using System.Collections.ObjectModel;
using System.Text.Json;

namespace BackdropForCodex.Core.Media;

internal enum WallpaperEngineOwnedWindowRecoveryState
{
    NothingToRecover = 0,
    Recovered,
    PartiallyRecovered,
    Deferred,
}

internal sealed record WallpaperEngineOwnedWindowRecoveryResult
{
    internal WallpaperEngineOwnedWindowRecoveryResult(
        WallpaperEngineOwnedWindowRecoveryState state,
        int closedCount,
        int remainingCount)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(closedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingCount);
        State = state;
        ClosedCount = closedCount;
        RemainingCount = remainingCount;
    }

    internal WallpaperEngineOwnedWindowRecoveryState State { get; }

    internal int ClosedCount { get; }

    internal int RemainingCount { get; }

    public override string ToString() =>
        $"{nameof(WallpaperEngineOwnedWindowRecoveryResult)} {{ State = {State}, " +
        $"ClosedCount = {ClosedCount}, RemainingCount = {RemainingCount}, Names = <redacted> }}";
}

internal interface IWallpaperEngineOwnedWindowRecovery
{
    ValueTask<WallpaperEngineOwnedWindowRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Persists only the unique names needed to close this application's pop-outs after a crash. It
/// never records paths, PIDs, HWNDs, project identities, or renderer state.
/// </summary>
internal sealed class WindowsWallpaperEngineOwnedWindowJournal
    : IWallpaperEngineOwnedWindowJournal, IDisposable
{
    private const int SchemaVersion = 1;
    private const int MaximumEntryCount = 32;
    private const int MaximumDocumentLength = 64 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _journalPath;
    private bool _disposed;

    internal WindowsWallpaperEngineOwnedWindowJournal()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BackdropForCodex",
            "wallpaper-engine-owned-windows.json"))
    {
    }

    internal WindowsWallpaperEngineOwnedWindowJournal(string journalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        if (!Path.IsPathFullyQualified(journalPath))
        {
            throw new ArgumentException(
                "The owned-window journal path must be fully qualified.",
                nameof(journalPath));
        }

        _journalPath = Path.GetFullPath(journalPath);
    }

    public async ValueTask RecordAsync(
        WallpaperEngineOwnedWindowName windowName,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (entries.Contains(windowName))
            {
                return;
            }

            if (entries.Count >= MaximumEntryCount)
            {
                throw InvalidJournal();
            }

            entries.Add(windowName);
            await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ClearAsync(
        WallpaperEngineOwnedWindowName windowName,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!entries.Remove(windowName))
            {
                return;
            }

            await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<IReadOnlyList<WallpaperEngineOwnedWindowName>> ReadPendingAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new ReadOnlyCollection<WallpaperEngineOwnedWindowName>(
                await ReadCoreAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public override string ToString() =>
        $"{nameof(WindowsWallpaperEngineOwnedWindowJournal)} {{ Path = <redacted>, " +
        "Entries = <redacted> }}";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private async ValueTask<List<WallpaperEngineOwnedWindowName>> ReadCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
        {
            return [];
        }

        byte[] bytes;
        try
        {
            await using var stream = new FileStream(
                _journalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is < 2 or > MaximumDocumentLength)
            {
                throw InvalidJournal();
            }

            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (WallpaperEnginePlatformUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                NotSupportedException or System.Security.SecurityException)
        {
            throw InvalidJournal();
        }

        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidJournal();
            }

            var propertyNames = root
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray();
            if (propertyNames.Length != 2 ||
                !propertyNames.Contains("schemaVersion", StringComparer.Ordinal) ||
                !propertyNames.Contains("ownedWindowNames", StringComparer.Ordinal) ||
                !root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != SchemaVersion ||
                !root.TryGetProperty("ownedWindowNames", out var names) ||
                names.ValueKind != JsonValueKind.Array ||
                names.GetArrayLength() > MaximumEntryCount)
            {
                throw InvalidJournal();
            }

            var result = new List<WallpaperEngineOwnedWindowName>(names.GetArrayLength());
            foreach (var element in names.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String ||
                    element.GetString() is not { } value)
                {
                    throw InvalidJournal();
                }

                var name = new WallpaperEngineOwnedWindowName(value);
                if (result.Contains(name))
                {
                    throw InvalidJournal();
                }

                result.Add(name);
            }

            return result;
        }
        catch (WallpaperEnginePlatformUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw InvalidJournal();
        }
    }

    private async ValueTask WriteCoreAsync(
        IReadOnlyList<WallpaperEngineOwnedWindowName> entries,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_journalPath) ??
            throw InvalidJournal();
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_journalPath)}.{Guid.CreateVersion7():N}.tmp");
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
                await using var writer = new Utf8JsonWriter(
                    stream,
                    new JsonWriterOptions { Indented = false });
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", SchemaVersion);
                writer.WriteStartArray("ownedWindowNames");
                foreach (var entry in entries)
                {
                    writer.WriteStringValue(entry.Value);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _journalPath, overwrite: true);
        }
        catch (WallpaperEnginePlatformUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                NotSupportedException or System.Security.SecurityException)
        {
            throw InvalidJournal();
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    NotSupportedException or System.Security.SecurityException)
            {
                // The stable file was already published or left untouched; a uniquely named
                // temporary file cannot authorize or recover any window.
            }
        }
    }

    private static WallpaperEnginePlatformUnavailableException InvalidJournal() =>
        new(WallpaperEnginePlatformUnavailableReason.RecoveryJournalInvalid);
}

internal sealed class WallpaperEngineOwnedWindowRecovery : IWallpaperEngineOwnedWindowRecovery
{
    private readonly IWallpaperEngineInstallationLocator _installationLocator;
    private readonly IWallpaperEngineControlClient _controlClient;
    private readonly IWallpaperEngineOwnedWindowVerifier _windowVerifier;
    private readonly WindowsWallpaperEngineOwnedWindowJournal _journal;

    internal WallpaperEngineOwnedWindowRecovery(
        IWallpaperEngineInstallationLocator installationLocator,
        IWallpaperEngineControlClient controlClient,
        IWallpaperEngineOwnedWindowVerifier windowVerifier,
        WindowsWallpaperEngineOwnedWindowJournal journal)
    {
        _installationLocator = installationLocator ??
            throw new ArgumentNullException(nameof(installationLocator));
        _controlClient = controlClient ??
            throw new ArgumentNullException(nameof(controlClient));
        _windowVerifier = windowVerifier ??
            throw new ArgumentNullException(nameof(windowVerifier));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public async ValueTask<WallpaperEngineOwnedWindowRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken)
    {
        var pending = await _journal
            .ReadPendingAsync(cancellationToken)
            .ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return new WallpaperEngineOwnedWindowRecoveryResult(
                WallpaperEngineOwnedWindowRecoveryState.NothingToRecover,
                closedCount: 0,
                remainingCount: 0);
        }

        WallpaperEngineInstallation installation;
        try
        {
            installation = await _installationLocator
                .LocateAsync(cancellationToken)
                .ConfigureAwait(false);
            await _controlClient
                .EnsureRunningAsync(installation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is WallpaperEngineUnavailableException or
                WallpaperEnginePlatformUnavailableException)
        {
            return new WallpaperEngineOwnedWindowRecoveryResult(
                WallpaperEngineOwnedWindowRecoveryState.Deferred,
                closedCount: 0,
                remainingCount: pending.Count);
        }

        var closedCount = 0;
        foreach (var windowName in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var baseline = await _windowVerifier
                    .CaptureBaselineAsync(installation, cancellationToken)
                    .ConfigureAwait(false);
                await _controlClient
                    .CloseWindowAsync(installation, windowName, cancellationToken)
                    .ConfigureAwait(false);
                await _windowVerifier
                    .ConfirmOwnedWindowAbsentAsync(
                        installation,
                        windowName,
                        baseline,
                        cancellationToken)
                    .ConfigureAwait(false);
                await _journal
                    .ClearAsync(windowName, cancellationToken)
                    .ConfigureAwait(false);
                closedCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException or
                    UnauthorizedAccessException or
                    WallpaperEnginePlatformUnavailableException)
            {
                // Keep this exact name journaled for a later bounded recovery attempt.
            }
        }

        var remainingCount = pending.Count - closedCount;
        return new WallpaperEngineOwnedWindowRecoveryResult(
            remainingCount == 0
                ? WallpaperEngineOwnedWindowRecoveryState.Recovered
                : WallpaperEngineOwnedWindowRecoveryState.PartiallyRecovered,
            closedCount,
            remainingCount);
    }
}
