using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;

namespace BackdropForCodex.Core.Media;

internal enum WallpaperEnginePlatformUnavailableReason
{
    StartupNotProven = 0,
    CommandTimedOut,
    CommandOutputInvalid,
    CommandRejected,
    ProcessBoundaryUnavailable,
    WindowOwnershipNotProven,
    InitialAudioSilenceNotProven,
    AudioIsolationNotProven,
    RecoveryJournalInvalid,
    WindowPlacementNotProven,
}

internal sealed class WallpaperEnginePlatformUnavailableException : InvalidOperationException
{
    internal WallpaperEnginePlatformUnavailableException(
        WallpaperEnginePlatformUnavailableReason reason,
        Exception? innerException = null)
        : base(CreateMessage(reason))
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        Reason = reason;
        if (innerException is not null)
        {
            DiagnosticExceptionType = innerException.GetType().Name;
            DiagnosticHResult = innerException.HResult;
        }
    }

    internal WallpaperEnginePlatformUnavailableReason Reason { get; }

    /// <summary>
    /// The path-free CLR type name of the platform-boundary failure. The original exception,
    /// message and stack are deliberately not retained because native failures can contain user
    /// paths, process details or window identifiers.
    /// </summary>
    internal string? DiagnosticExceptionType { get; }

    /// <summary>
    /// The path-free HRESULT associated with the platform-boundary failure.
    /// </summary>
    internal int? DiagnosticHResult { get; }

    private static string CreateMessage(WallpaperEnginePlatformUnavailableReason reason) =>
        reason switch
        {
            WallpaperEnginePlatformUnavailableReason.StartupNotProven =>
                "Wallpaper Engine must already be running before a window-level command is sent.",
            WallpaperEnginePlatformUnavailableReason.CommandTimedOut =>
                "The Wallpaper Engine window-level command timed out.",
            WallpaperEnginePlatformUnavailableReason.CommandOutputInvalid =>
                "The Wallpaper Engine window-level command returned invalid output.",
            WallpaperEnginePlatformUnavailableReason.CommandRejected =>
                "Wallpaper Engine rejected the window-level command.",
            WallpaperEnginePlatformUnavailableReason.ProcessBoundaryUnavailable =>
                "The Wallpaper Engine process boundary could not be validated.",
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven =>
                "The Wallpaper Engine pop-out window ownership could not be proven.",
            WallpaperEnginePlatformUnavailableReason.InitialAudioSilenceNotProven =>
                "The Wallpaper Engine pop-out could not be proven silent before opening.",
            WallpaperEnginePlatformUnavailableReason.WindowPlacementNotProven =>
                "The Wallpaper Engine pop-out non-interfering placement could not be proven.",
            WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven =>
                "The Wallpaper Engine pop-out audio session could not be isolated.",
            WallpaperEnginePlatformUnavailableReason.RecoveryJournalInvalid =>
                "The Wallpaper Engine owned-window recovery journal is invalid.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
}

internal enum WallpaperEngineProcessFailure
{
    FailedToStart = 0,
    TimedOut,
    OutputLimitExceeded,
    ProcessInspectionFailed,
}

internal sealed class WallpaperEngineProcessException : InvalidOperationException
{
    internal WallpaperEngineProcessException(WallpaperEngineProcessFailure failure)
        : base("The Wallpaper Engine command process failed at a reviewed boundary.")
    {
        if (!Enum.IsDefined(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        Failure = failure;
    }

    internal WallpaperEngineProcessFailure Failure { get; }
}

internal sealed class WallpaperEngineProcessCommand
{
    private static readonly HashSet<string> ReviewedOperations =
        new(StringComparer.Ordinal)
        {
            "openWallpaper",
            "getWallpaper",
            "closeWallpaper",
        };

    internal WallpaperEngineProcessCommand(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var copy = arguments.ToArray();
        if (copy.Length < 2 ||
            !string.Equals(copy[0], "-control", StringComparison.Ordinal) ||
            !ReviewedOperations.Contains(copy[1]) ||
            copy.Any(argument => argument is null))
        {
            throw new ArgumentException(
                "Only reviewed Wallpaper Engine window-level commands are allowed.",
                nameof(arguments));
        }

        Arguments = new ReadOnlyCollection<string>(copy);
    }

    internal IReadOnlyList<string> Arguments { get; }

    public override string ToString() =>
        $"{nameof(WallpaperEngineProcessCommand)} {{ Arguments = <redacted> }}";
}

internal sealed record WallpaperEngineProcessResult
{
    internal WallpaperEngineProcessResult(int exitCode, string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ExitCode = exitCode;
        StandardOutput = standardOutput;
    }

    internal int ExitCode { get; }

    internal string StandardOutput { get; }

    public override string ToString() =>
        $"{nameof(WallpaperEngineProcessResult)} {{ ExitCode = {ExitCode}, " +
        "StandardOutput = <redacted> }}";
}

internal interface IWallpaperEngineProcessBoundary
{
    ValueTask<bool> IsRunningAsync(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken);

    ValueTask<WallpaperEngineProcessResult> ExecuteAsync(
        WallpaperEngineInstallation installation,
        WallpaperEngineProcessCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal interface IWallpaperEngineCommandProcessFactory
{
    IWallpaperEngineCommandProcess Create(ProcessStartInfo startInfo);
}

internal interface IWallpaperEngineCommandProcess : IDisposable
{
    TextReader StandardOutput { get; }

    TextReader StandardError { get; }

    int ExitCode { get; }

    bool HasExited { get; }

    bool Start();

    ValueTask WaitForExitAsync(CancellationToken cancellationToken);

    void KillExactProcess();
}

/// <summary>
/// Sends only reviewed, uniquely addressed pop-out commands to an already-running Wallpaper
/// Engine instance. The official CLI does not define a non-disruptive startup command, so a
/// missing instance is a typed unavailable result rather than an implicit process launch.
/// </summary>
internal sealed class WallpaperEngineControlClient : IWallpaperEngineControlClient
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(8);
    private readonly IWallpaperEngineProcessBoundary _processBoundary;
    private readonly TimeSpan _commandTimeout;

    internal WallpaperEngineControlClient()
        : this(new WindowsWallpaperEngineProcessBoundary(), DefaultCommandTimeout)
    {
    }

    internal WallpaperEngineControlClient(
        IWallpaperEngineProcessBoundary processBoundary,
        TimeSpan commandTimeout)
    {
        _processBoundary = processBoundary ??
            throw new ArgumentNullException(nameof(processBoundary));
        if (commandTimeout <= TimeSpan.Zero || commandTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        }

        _commandTimeout = commandTimeout;
    }

    public async ValueTask EnsureRunningAsync(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        bool isRunning;
        try
        {
            isRunning = await _processBoundary
                .IsRunningAsync(installation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WallpaperEngineProcessException)
        {
            throw new WallpaperEnginePlatformUnavailableException(
                WallpaperEnginePlatformUnavailableReason.ProcessBoundaryUnavailable);
        }

        if (!isRunning)
        {
            throw new WallpaperEnginePlatformUnavailableException(
                WallpaperEnginePlatformUnavailableReason.StartupNotProven);
        }
    }

    public async ValueTask OpenWindowAsync(
        WallpaperEngineInstallation installation,
        string launchPath,
        WallpaperEngineOwnedWindowName windowName,
        WallpaperEngineWindowOptions options,
        WallpaperEngineWindowPlacement placement,
        CancellationToken cancellationToken)
    {
        var command = new WallpaperEngineProcessCommand(
            WallpaperEngineControlCommandBuilder.BuildOpenWindow(
                launchPath,
                windowName,
                options,
                placement.X,
                placement.Y));
        _ = await ExecuteReviewedAsync(
                installation,
                command,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<string?> QueryWindowWallpaperAsync(
        WallpaperEngineInstallation installation,
        WallpaperEngineOwnedWindowName windowName,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteReviewedAsync(
                installation,
                new WallpaperEngineProcessCommand(
                    WallpaperEngineControlCommandBuilder.BuildQueryWindow(windowName)),
                cancellationToken)
            .ConfigureAwait(false);
        var reportedPath = result.StandardOutput.Trim();
        return reportedPath.Length == 0 ? null : reportedPath;
    }

    public async ValueTask CloseWindowAsync(
        WallpaperEngineInstallation installation,
        WallpaperEngineOwnedWindowName windowName,
        CancellationToken cancellationToken)
    {
        _ = await ExecuteReviewedAsync(
                installation,
                new WallpaperEngineProcessCommand(
                    WallpaperEngineControlCommandBuilder.BuildCloseWindow(windowName)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<WallpaperEngineProcessResult> ExecuteReviewedAsync(
        WallpaperEngineInstallation installation,
        WallpaperEngineProcessCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!await _processBoundary
                    .IsRunningAsync(installation, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new WallpaperEnginePlatformUnavailableException(
                    WallpaperEnginePlatformUnavailableReason.StartupNotProven);
            }
        }
        catch (WallpaperEngineProcessException)
        {
            throw new WallpaperEnginePlatformUnavailableException(
                WallpaperEnginePlatformUnavailableReason.ProcessBoundaryUnavailable);
        }

        WallpaperEngineProcessResult result;
        try
        {
            result = await _processBoundary
                .ExecuteAsync(
                    installation,
                    command,
                    _commandTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WallpaperEngineProcessException exception)
        {
            var reason = exception.Failure switch
            {
                WallpaperEngineProcessFailure.TimedOut =>
                    WallpaperEnginePlatformUnavailableReason.CommandTimedOut,
                WallpaperEngineProcessFailure.OutputLimitExceeded =>
                    WallpaperEnginePlatformUnavailableReason.CommandOutputInvalid,
                WallpaperEngineProcessFailure.FailedToStart or
                    WallpaperEngineProcessFailure.ProcessInspectionFailed =>
                    WallpaperEnginePlatformUnavailableReason.ProcessBoundaryUnavailable,
                _ => throw new InvalidOperationException(
                    "The process boundary returned an unsupported failure category."),
            };
            throw new WallpaperEnginePlatformUnavailableException(reason);
        }

        if (result.ExitCode != 0)
        {
            throw new WallpaperEnginePlatformUnavailableException(
                WallpaperEnginePlatformUnavailableReason.CommandRejected);
        }

        return result;
    }
}

internal sealed class WindowsWallpaperEngineProcessBoundary : IWallpaperEngineProcessBoundary
{
    private const int MaximumCapturedCharacterCount = 16 * 1024;
    private static readonly TimeSpan DefaultHelperTerminationTimeout =
        TimeSpan.FromSeconds(2);
    private readonly IWallpaperEngineExecutableTrustVerifier _trustVerifier;
    private readonly IWallpaperEngineCommandProcessFactory _processFactory;
    private readonly TimeSpan _helperTerminationTimeout;

    internal WindowsWallpaperEngineProcessBoundary()
        : this(
            new WindowsAuthenticodeTrustVerifier(),
            new SystemWallpaperEngineCommandProcessFactory(),
            DefaultHelperTerminationTimeout)
    {
    }

    internal WindowsWallpaperEngineProcessBoundary(
        IWallpaperEngineExecutableTrustVerifier trustVerifier)
        : this(
            trustVerifier,
            new SystemWallpaperEngineCommandProcessFactory(),
            DefaultHelperTerminationTimeout)
    {
    }

    internal WindowsWallpaperEngineProcessBoundary(
        IWallpaperEngineExecutableTrustVerifier trustVerifier,
        IWallpaperEngineCommandProcessFactory processFactory,
        TimeSpan helperTerminationTimeout)
    {
        _trustVerifier = trustVerifier ?? throw new ArgumentNullException(nameof(trustVerifier));
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            helperTerminationTimeout,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            helperTerminationTimeout,
            TimeSpan.FromSeconds(10));
        _helperTerminationTimeout = helperTerminationTimeout;
    }

    public ValueTask<bool> IsRunningAsync(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(false);
        }

        var expectedPath = Path.GetFullPath(installation.ControlExecutablePath);
        if (!File.Exists(expectedPath) || !_trustVerifier.IsTrusted(expectedPath))
        {
            throw new WallpaperEngineProcessException(
                WallpaperEngineProcessFailure.ProcessInspectionFailed);
        }

        var processName = Path.GetFileNameWithoutExtension(expectedPath);
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            var currentSessionId = currentProcess.SessionId;
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (process.Id != Environment.ProcessId &&
                            process.SessionId == currentSessionId &&
                            string.Equals(
                                Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                                expectedPath,
                                StringComparison.OrdinalIgnoreCase) &&
                            process.StartTime.ToUniversalTime() <= DateTime.UtcNow)
                        {
                            return ValueTask.FromResult(true);
                        }
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException or
                            System.ComponentModel.Win32Exception or
                            NotSupportedException or
                            PlatformNotSupportedException or
                            System.Security.SecurityException or
                            IOException or
                            UnauthorizedAccessException)
                    {
                        // An unreadable process is not evidence of the authorized running instance.
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException or
                PlatformNotSupportedException or
                System.Security.SecurityException or
                UnauthorizedAccessException)
        {
            throw new WallpaperEngineProcessException(
                WallpaperEngineProcessFailure.ProcessInspectionFailed);
        }

        return ValueTask.FromResult(false);
    }

    public async ValueTask<WallpaperEngineProcessResult> ExecuteAsync(
        WallpaperEngineInstallation installation,
        WallpaperEngineProcessCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new WallpaperEngineProcessException(
                WallpaperEngineProcessFailure.FailedToStart);
        }

        if (!File.Exists(installation.ControlExecutablePath) ||
            !_trustVerifier.IsTrusted(installation.ControlExecutablePath))
        {
            throw new WallpaperEngineProcessException(
                WallpaperEngineProcessFailure.FailedToStart);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = installation.ControlExecutablePath,
            WorkingDirectory = installation.InstallRootPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = _processFactory.Create(startInfo) ??
            throw new WallpaperEngineProcessException(
                WallpaperEngineProcessFailure.FailedToStart);
        try
        {
            if (!process.Start())
            {
                throw new WallpaperEngineProcessException(
                    WallpaperEngineProcessFailure.FailedToStart);
            }
        }
        catch (WallpaperEngineProcessException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                ObjectDisposedException or
                PlatformNotSupportedException)
        {
            throw new WallpaperEngineProcessException(
                WallpaperEngineProcessFailure.FailedToStart);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var outputTask = ReadBoundedAsync(
            process.StandardOutput,
            MaximumCapturedCharacterCount,
            linkedSource.Token);
        var errorTask = ReadBoundedAsync(
            process.StandardError,
            MaximumCapturedCharacterCount,
            linkedSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (output.ExceededLimit || error.ExceededLimit)
            {
                throw new WallpaperEngineProcessException(
                    WallpaperEngineProcessFailure.OutputLimitExceeded);
            }

            return new WallpaperEngineProcessResult(process.ExitCode, output.Text);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (!await TerminateAndDrainHelperAsync(process, outputTask, errorTask)
                    .ConfigureAwait(false))
            {
                throw new WallpaperEngineProcessException(
                    WallpaperEngineProcessFailure.ProcessInspectionFailed);
            }

            throw new WallpaperEngineProcessException(
                WallpaperEngineProcessFailure.TimedOut);
        }
        catch (OperationCanceledException)
        {
            if (!await TerminateAndDrainHelperAsync(process, outputTask, errorTask)
                    .ConfigureAwait(false))
            {
                throw new WallpaperEngineProcessException(
                    WallpaperEngineProcessFailure.ProcessInspectionFailed);
            }

            throw;
        }
        catch (WallpaperEngineProcessException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or
            ObjectDisposedException or System.ComponentModel.Win32Exception or
            UnauthorizedAccessException)
        {
            _ = await TerminateAndDrainHelperAsync(process, outputTask, errorTask)
                .ConfigureAwait(false);
            throw new WallpaperEngineProcessException(
                WallpaperEngineProcessFailure.ProcessInspectionFailed);
        }
    }

    private async ValueTask<bool> TerminateAndDrainHelperAsync(
        IWallpaperEngineCommandProcess process,
        Task<BoundedText> outputTask,
        Task<BoundedText> errorTask)
    {
        try
        {
            if (!process.HasExited)
            {
                process.KillExactProcess();
            }

            using var cleanupSource = new CancellationTokenSource(
                _helperTerminationTimeout);
            await process.WaitForExitAsync(cleanupSource.Token).ConfigureAwait(false);
            if (!process.HasExited)
            {
                return false;
            }

            await ObserveReaderCompletionAsync(outputTask).ConfigureAwait(false);
            await ObserveReaderCompletionAsync(errorTask).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or
                ObjectDisposedException or OperationCanceledException or
                System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async ValueTask ObserveReaderCompletionAsync(Task<BoundedText> readerTask)
    {
        try
        {
            _ = await readerTask
                .WaitAsync(_helperTerminationTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or
                OperationCanceledException or TimeoutException)
        {
            // The exact helper has exited. Reader cancellation is observed and cannot create a
            // late pop-out after process exit.
        }
    }

    private static async Task<BoundedText> ReadBoundedAsync(
        TextReader reader,
        int maximumCharacterCount,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacterCount, 1024));
        var buffer = new char[1024];
        var exceeded = false;
        while (true)
        {
            var read = await reader
                .ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var available = maximumCharacterCount - builder.Length;
            if (available > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, available));
            }

            exceeded |= read > available;
        }

        return new BoundedText(builder.ToString(), exceeded);
    }

    private sealed record BoundedText(string Text, bool ExceededLimit);
}

internal sealed class SystemWallpaperEngineCommandProcessFactory
    : IWallpaperEngineCommandProcessFactory
{
    public IWallpaperEngineCommandProcess Create(ProcessStartInfo startInfo) =>
        new SystemWallpaperEngineCommandProcess(startInfo);
}

internal sealed class SystemWallpaperEngineCommandProcess : IWallpaperEngineCommandProcess
{
    private readonly Process _process;

    internal SystemWallpaperEngineCommandProcess(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
    }

    public TextReader StandardOutput => _process.StandardOutput;

    public TextReader StandardError => _process.StandardError;

    public int ExitCode => _process.ExitCode;

    public bool HasExited => _process.HasExited;

    public bool Start() => _process.Start();

    public ValueTask WaitForExitAsync(CancellationToken cancellationToken) =>
        new(_process.WaitForExitAsync(cancellationToken));

    public void KillExactProcess() => _process.Kill(entireProcessTree: false);

    public void Dispose() => _process.Dispose();
}
