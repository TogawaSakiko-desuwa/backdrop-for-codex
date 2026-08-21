using System.Text;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperEngineOwnedWindowJournalTests
{
    [Fact]
    public async Task JournalSurvivesRestartWithoutPersistingProcessOrPathMetadata()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "owned-windows.json");
        using var first = new WindowsWallpaperEngineOwnedWindowJournal(path);
        var kept = WallpaperEngineOwnedWindowName.Create(1);
        var cleared = WallpaperEngineOwnedWindowName.Create(2);

        await first.RecordAsync(kept, CancellationToken.None);
        await first.RecordAsync(cleared, CancellationToken.None);
        await first.ClearAsync(cleared, CancellationToken.None);
        using var reopened = new WindowsWallpaperEngineOwnedWindowJournal(path);
        var pending = await reopened.ReadPendingAsync(CancellationToken.None);
        var text = await File.ReadAllTextAsync(path);

        Assert.Equal(kept, Assert.Single(pending));
        Assert.Contains("\"schemaVersion\":1", text, StringComparison.Ordinal);
        Assert.Contains(kept.Value, text, StringComparison.Ordinal);
        Assert.DoesNotContain("pid", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hwnd", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(kept.Value, reopened.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"ownedWindowNames\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"ownedWindowNames\":[],\"unknown\":true}")]
    [InlineData("{\"schemaVersion\":1,\"ownedWindowNames\":[\"not-owned\"]}")]
    public async Task InvalidJournalIsRejectedWithoutBeingOverwritten(string content)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "owned-windows.json");
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        var original = await File.ReadAllBytesAsync(path);
        using var journal = new WindowsWallpaperEngineOwnedWindowJournal(path);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await journal.RecordAsync(
                WallpaperEngineOwnedWindowName.Create(3),
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.RecoveryJournalInvalid,
            exception.Reason);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task StartupRecoveryClearsOnlyWindowsConfirmedClosedByName()
    {
        using var directory = new TemporaryDirectory();
        using var journal = new WindowsWallpaperEngineOwnedWindowJournal(
            Path.Combine(directory.Path, "owned-windows.json"));
        var closed = WallpaperEngineOwnedWindowName.Create(4);
        var remaining = WallpaperEngineOwnedWindowName.Create(5);
        await journal.RecordAsync(closed, CancellationToken.None);
        await journal.RecordAsync(remaining, CancellationToken.None);
        var control = new RecoveryControlClient(remaining);
        var verifier = new RecoveryWindowVerifier();
        var recovery = new WallpaperEngineOwnedWindowRecovery(
            new FixedInstallationLocator(CreateInstallation()),
            control,
            verifier,
            journal);

        var result = await recovery.RecoverAsync(CancellationToken.None);

        Assert.Equal(WallpaperEngineOwnedWindowRecoveryState.PartiallyRecovered, result.State);
        Assert.Equal(1, result.ClosedCount);
        Assert.Equal(1, result.RemainingCount);
        Assert.Equal([remaining], await journal.ReadPendingAsync(CancellationToken.None));
        Assert.Equal([closed, remaining], control.CloseAttempts);
        Assert.Equal([closed], verifier.AbsenceAttempts);
        Assert.DoesNotContain(closed.Value, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupRecoveryRetainsTheJournalUntilWindowAbsenceIsProven()
    {
        using var directory = new TemporaryDirectory();
        using var journal = new WindowsWallpaperEngineOwnedWindowJournal(
            Path.Combine(directory.Path, "owned-windows.json"));
        var retained = WallpaperEngineOwnedWindowName.Create(6);
        await journal.RecordAsync(retained, CancellationToken.None);
        var control = new RecoveryControlClient(closeFailure: null);
        var verifier = new RecoveryWindowVerifier(retained);
        var recovery = new WallpaperEngineOwnedWindowRecovery(
            new FixedInstallationLocator(CreateInstallation()),
            control,
            verifier,
            journal);

        var result = await recovery.RecoverAsync(CancellationToken.None);

        Assert.Equal(WallpaperEngineOwnedWindowRecoveryState.PartiallyRecovered, result.State);
        Assert.Equal(0, result.ClosedCount);
        Assert.Equal(1, result.RemainingCount);
        Assert.Equal([retained], await journal.ReadPendingAsync(CancellationToken.None));
        Assert.Equal([retained], control.CloseAttempts);
        Assert.Equal([retained], verifier.AbsenceAttempts);
    }

    private static WallpaperEngineInstallation CreateInstallation() => new(
        @"C:\Steam",
        @"C:\Steam\steamapps\common\wallpaper_engine",
        @"C:\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe",
        [@"C:\Steam"]);

    private sealed class FixedInstallationLocator(WallpaperEngineInstallation installation)
        : IWallpaperEngineInstallationLocator
    {
        public ValueTask<WallpaperEngineInstallation> LocateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(installation);
        }
    }

    private sealed class RecoveryControlClient(WallpaperEngineOwnedWindowName? closeFailure)
        : IWallpaperEngineControlClient
    {
        internal List<WallpaperEngineOwnedWindowName> CloseAttempts { get; } = [];

        public ValueTask EnsureRunningAsync(
            WallpaperEngineInstallation installation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask OpenWindowAsync(
            WallpaperEngineInstallation installation,
            string launchPath,
            WallpaperEngineOwnedWindowName windowName,
            WallpaperEngineWindowOptions options,
            WallpaperEngineWindowPlacement placement,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<string?> QueryWindowWallpaperAsync(
            WallpaperEngineInstallation installation,
            WallpaperEngineOwnedWindowName windowName,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask CloseWindowAsync(
            WallpaperEngineInstallation installation,
            WallpaperEngineOwnedWindowName windowName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseAttempts.Add(windowName);
            return closeFailure is { } failure && windowName == failure
                ? ValueTask.FromException(new IOException("fixture close failure"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class RecoveryWindowVerifier(
        WallpaperEngineOwnedWindowName? absenceFailure = null)
        : IWallpaperEngineOwnedWindowVerifier
    {
        internal List<WallpaperEngineOwnedWindowName> AbsenceAttempts { get; } = [];

        public ValueTask<WallpaperEngineWindowBaseline> CaptureBaselineAsync(
            WallpaperEngineInstallation installation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new WallpaperEngineWindowBaseline([]));
        }

        public ValueTask<WallpaperEngineVerifiedWindow> WaitForOwnedWindowAsync(
            WallpaperEngineInstallation installation,
            WallpaperEngineOwnedWindowName windowName,
            WallpaperEngineWindowBaseline baseline,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask RevalidateOwnedWindowAsync(
            WallpaperEngineVerifiedWindow expectedWindow,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ConfirmOwnedWindowAbsentAsync(
            WallpaperEngineInstallation installation,
            WallpaperEngineOwnedWindowName windowName,
            WallpaperEngineWindowBaseline baseline,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AbsenceAttempts.Add(windowName);
            return absenceFailure is { } failure && windowName == failure
                ? ValueTask.FromException(
                    new WallpaperEnginePlatformUnavailableException(
                        WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"BackdropForCodex-JournalTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
