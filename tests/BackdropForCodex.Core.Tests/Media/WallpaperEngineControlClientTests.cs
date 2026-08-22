using System.Diagnostics;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperEngineControlClientTests
{
    [Fact]
    public void PlatformUnavailableExceptionRetainsOnlyPathFreeDiagnostics()
    {
        const string sensitivePath = @"C:\Users\private\WallpaperEngine\wallpaper64.exe";
        var exception = new WallpaperEnginePlatformUnavailableException(
            WallpaperEnginePlatformUnavailableReason.WindowPlacementNotProven,
            new IOException($"Could not inspect '{sensitivePath}'."));

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            sensitivePath,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(nameof(IOException), exception.DiagnosticExceptionType);
        Assert.NotEqual(0, exception.DiagnosticHResult);
    }

    [Fact]
    public async Task MissingRunningInstanceFailsClosedWithoutLaunchingWallpaperEngine()
    {
        var processBoundary = new RecordingProcessBoundary { IsRunning = false };
        var client = new WallpaperEngineControlClient(processBoundary, TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await client.EnsureRunningAsync(CreateInstallation(), CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.StartupNotProven,
            exception.Reason);
        Assert.Empty(processBoundary.Commands);
        Assert.DoesNotContain(@"C:\Steam", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessBoundaryRejectsEveryGlobalWallpaperCommand()
    {
        foreach (var operation in new[] { "pause", "play", "stop", "mute", "unmute" })
        {
            Assert.Throws<ArgumentException>(
                () => new WallpaperEngineProcessCommand(["-control", operation]));
        }
    }

    [Fact]
    public async Task WindowCommandsUseOnlyReviewedWindowLevelArguments()
    {
        var processBoundary = new RecordingProcessBoundary
        {
            IsRunning = true,
            NextResult = new WallpaperEngineProcessResult(0, "  C:\\Projects\\scene.pkg\r\n"),
        };
        var client = new WallpaperEngineControlClient(processBoundary, TimeSpan.FromSeconds(1));
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(1);

        await client.EnsureRunningAsync(installation, CancellationToken.None);
        await client.OpenWindowAsync(
            installation,
            @"C:\Projects\scene.pkg",
            name,
            new WallpaperEngineWindowOptions(1920, 1080),
            new WallpaperEngineWindowPlacement(-4096, -2160),
            CancellationToken.None);
        var reported = await client.QueryWindowWallpaperAsync(
            installation,
            name,
            CancellationToken.None);
        await client.CloseWindowAsync(installation, name, CancellationToken.None);

        Assert.Equal(@"C:\Projects\scene.pkg", reported);
        Assert.Collection(
            processBoundary.Commands,
            open => Assert.Equal("openWallpaper", open.Arguments[1]),
            query => Assert.Equal("getWallpaper", query.Arguments[1]),
            close => Assert.Equal("closeWallpaper", close.Arguments[1]));
        Assert.All(
            processBoundary.Commands,
            command => Assert.DoesNotContain(
                command.Arguments,
                argument => argument is "pause" or "play" or "stop" or "mute" or "unmute"));
    }

    [Theory]
    [InlineData(
        (int)WallpaperEngineProcessFailure.TimedOut,
        (int)WallpaperEnginePlatformUnavailableReason.CommandTimedOut)]
    [InlineData(
        (int)WallpaperEngineProcessFailure.OutputLimitExceeded,
        (int)WallpaperEnginePlatformUnavailableReason.CommandOutputInvalid)]
    public async Task ProcessFailuresRemainTypedAndRedacted(
        int processFailureValue,
        int expectedReasonValue)
    {
        var processFailure = (WallpaperEngineProcessFailure)processFailureValue;
        var expectedReason = (WallpaperEnginePlatformUnavailableReason)expectedReasonValue;
        var processBoundary = new RecordingProcessBoundary
        {
            IsRunning = true,
            Failure = processFailure,
        };
        var client = new WallpaperEngineControlClient(processBoundary, TimeSpan.FromSeconds(1));
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(2);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await client.QueryWindowWallpaperAsync(
                installation,
                name,
                CancellationToken.None));

        Assert.Equal(expectedReason, exception.Reason);
        Assert.DoesNotContain(installation.ControlExecutablePath, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(name.Value, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimedOutCommandKillsAndAwaitsTheExactHelperBeforeReturning()
    {
        var process = new HangingCommandProcess();
        var boundary = new WindowsWallpaperEngineProcessBoundary(
            new TrustAllExecutables(),
            new SingleCommandProcessFactory(process),
            TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<WallpaperEngineProcessException>(
            async () => await boundary.ExecuteAsync(
                CreateProcessInstallation(),
                new WallpaperEngineProcessCommand(
                    ["-control", "getWallpaper", "-name", "fixture"]),
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None));

        Assert.Equal(WallpaperEngineProcessFailure.TimedOut, exception.Failure);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(2, process.WaitCount);
        Assert.True(process.IsDisposed);
    }

    [Fact]
    public async Task CallerCancellationKillsAndAwaitsTheExactHelperBeforeReturning()
    {
        var process = new HangingCommandProcess();
        var boundary = new WindowsWallpaperEngineProcessBoundary(
            new TrustAllExecutables(),
            new SingleCommandProcessFactory(process),
            TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await boundary.ExecuteAsync(
                CreateProcessInstallation(),
                new WallpaperEngineProcessCommand(
                    ["-control", "getWallpaper", "-name", "fixture"]),
                TimeSpan.FromSeconds(1),
                cancellation.Token));

        Assert.Equal(1, process.KillCount);
        Assert.Equal(2, process.WaitCount);
        Assert.True(process.IsDisposed);
    }

    private static WallpaperEngineInstallation CreateInstallation() => new(
        @"C:\Steam",
        @"C:\Steam\steamapps\common\wallpaper_engine",
        @"C:\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe",
        [@"C:\Steam"]);

    private static WallpaperEngineInstallation CreateProcessInstallation()
    {
        var executablePath = Environment.ProcessPath ??
            throw new InvalidOperationException("The test host executable path is unavailable.");
        var executableDirectory = Path.GetDirectoryName(executablePath) ??
            throw new InvalidOperationException("The test host executable directory is unavailable.");
        return new WallpaperEngineInstallation(
            executableDirectory,
            executableDirectory,
            executablePath,
            [executableDirectory]);
    }

    private sealed class RecordingProcessBoundary : IWallpaperEngineProcessBoundary
    {
        internal bool IsRunning { get; init; }

        internal WallpaperEngineProcessResult NextResult { get; init; } =
            new(0, string.Empty);

        internal WallpaperEngineProcessFailure? Failure { get; init; }

        internal List<WallpaperEngineProcessCommand> Commands { get; } = [];

        public ValueTask<bool> IsRunningAsync(
            WallpaperEngineInstallation installation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(IsRunning);
        }

        public ValueTask<WallpaperEngineProcessResult> ExecuteAsync(
            WallpaperEngineInstallation installation,
            WallpaperEngineProcessCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Failure is { } failure
                ? ValueTask.FromException<WallpaperEngineProcessResult>(
                    new WallpaperEngineProcessException(failure))
                : ValueTask.FromResult(NextResult);
        }
    }

    private sealed class TrustAllExecutables : IWallpaperEngineExecutableTrustVerifier
    {
        public bool IsTrusted(string executablePath) => true;
    }

    private sealed class SingleCommandProcessFactory(HangingCommandProcess process)
        : IWallpaperEngineCommandProcessFactory
    {
        public IWallpaperEngineCommandProcess Create(ProcessStartInfo startInfo) => process;
    }

    private sealed class HangingCommandProcess : IWallpaperEngineCommandProcess
    {
        internal int KillCount { get; private set; }

        internal int WaitCount { get; private set; }

        internal bool IsDisposed { get; private set; }

        public TextReader StandardOutput { get; } = new StringReader(string.Empty);

        public TextReader StandardError { get; } = new StringReader(string.Empty);

        public int ExitCode => 0;

        public bool HasExited => KillCount > 0;

        public bool Start() => true;

        public async ValueTask WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCount++;
            if (WaitCount == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public void KillExactProcess()
        {
            KillCount++;
        }

        public void Dispose()
        {
            IsDisposed = true;
            StandardOutput.Dispose();
            StandardError.Dispose();
        }
    }
}
