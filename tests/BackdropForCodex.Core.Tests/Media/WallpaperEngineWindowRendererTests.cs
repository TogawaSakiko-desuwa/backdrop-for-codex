using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperEngineWindowRendererTests
{
    [Fact]
    public async Task AllowedAudioPlaybackStartsWithoutInspectingOrMutingAudioSessions()
    {
        var fixture = new RendererFixture();
        fixture.Control.ReportedPath = null;
        var renderer = fixture.CreateRendererAllowingAudioPlayback();
        await using var project = CreateProjectLease();

        var window = await renderer.StartAsync(
            project,
            new WallpaperEngineWindowOptions(1920, 1080));

        Assert.NotNull(window.CaptureTarget);
        Assert.Single(fixture.Control.OpenNames);
        Assert.Equal(0, fixture.Audio.AcquireCount);
        Assert.DoesNotContain("audio-baseline", fixture.Events);
        Assert.DoesNotContain("audio-mute", fixture.Events);

        await window.DisposeAsync();

        Assert.DoesNotContain("audio-unmute", fixture.Events);
    }

    [Fact]
    public async Task UnprovenInitialAudioSilenceFailsClosedBeforeOpeningTheWindow()
    {
        var fixture = new RendererFixture();
        fixture.Audio.Baseline.InitialSilenceIsProven = false;
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await renderer.StartAsync(
                project,
                new WallpaperEngineWindowOptions(1920, 1080)));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.InitialAudioSilenceNotProven,
            exception.Reason);
        Assert.Empty(fixture.Control.OpenNames);
        Assert.DoesNotContain("open", fixture.Events);
        Assert.Equal(0, fixture.Audio.AcquireCount);
        Assert.Equal(1, fixture.Audio.Baseline.DisposeCount);
        Assert.Equal(1, fixture.Journal.ClearCount);
        Assert.DoesNotContain(
            fixture.Installation.InstallRootPath,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            fixture.Installation.ControlExecutablePath,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RendererCapturesAudioBaselineAfterRunningProofBeforeOpeningWindow()
    {
        var fixture = new RendererFixture();
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();

        var window = await renderer.StartAsync(
            project,
            new WallpaperEngineWindowOptions(1920, 1080));

        Assert.Equal(
            ["ensure-running", "audio-baseline", "open", "audio-mute", "baseline-release"],
            fixture.Events.Where(
                entry => entry is "ensure-running" or "audio-baseline" or "open" or
                    "audio-mute" or "baseline-release"));
        Assert.Same(fixture.Audio.Baseline, fixture.Audio.AcquiredBaseline);
        Assert.Equal(1, fixture.Audio.Baseline.DisposeCount);
        Assert.Single(fixture.Control.OpenNames);

        await window.DisposeAsync();
    }

    [Fact]
    public async Task AudioBaselineReleaseFailureIsSanitizedAndRetriedFromTheOwnedGraph()
    {
        const string sensitiveDiagnostic = @"C:\Users\Sensitive\session-secret-42";
        var fixture = new RendererFixture();
        fixture.Audio.Baseline.DisposeException = new IOException(sensitiveDiagnostic);
        fixture.Audio.Baseline.DisposeFailuresRemaining = 1;
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await renderer.StartAsync(
                project,
                new WallpaperEngineWindowOptions(1920, 1080)));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven,
            exception.Reason);
        Assert.DoesNotContain(
            sensitiveDiagnostic,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fixture.Audio.Baseline.DisposeCount);
        Assert.Equal(
            [
                "baseline-release",
                "close",
                "confirm-closed",
                "audio-unmute",
                "baseline-release",
                "journal-clear",
            ],
            fixture.Events.Where(
                entry => entry is "baseline-release" or "close" or
                    "confirm-closed" or "audio-unmute" or "journal-clear"));
    }

    [Fact]
    public async Task PlacementDriftFaultsActiveHealthButStaysMutedUntilExactClose()
    {
        var fixture = new RendererFixture();
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();
        var window = await renderer.StartAsync(
            project,
            new WallpaperEngineWindowOptions(1920, 1080));
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(window);
        fixture.PlacementNative.MoveToVisibleArea();

        fixture.HealthScheduler.Advance();

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await health.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowPlacementNotProven,
            exception.Reason);
        Assert.True(fixture.PlacementNative.WindowExists);
        Assert.Equal(0, fixture.AudioLease.DisposeCount);

        await window.DisposeAsync();

        Assert.Equal(
            ["close", "confirm-closed", "audio-unmute", "journal-clear"],
            fixture.Events.Where(
                entry => entry is "close" or "confirm-closed" or
                    "audio-unmute" or "journal-clear"));
    }

    [Fact]
    public async Task OwnedWindowLeaseCanBeDisposedMoreThanOnceWithoutClosingAnotherWindow()
    {
        var fixture = new RendererFixture();
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();
        var window = await renderer.StartAsync(
            project,
            new WallpaperEngineWindowOptions(1920, 1080));

        await window.DisposeAsync();
        await window.DisposeAsync();

        Assert.Equal(1, fixture.Control.CloseCount);
        Assert.Equal(1, fixture.AudioLease.DisposeCount);
        Assert.Equal(1, fixture.Journal.ClearCount);
        Assert.Null(window.CaptureTarget);
    }

    [Fact]
    public async Task AudioCleanupFailureRetainsOnlyTheFailedResourceUntilRetry()
    {
        var fixture = new RendererFixture();
        var placement = new RetryableTestPopOutPlacement(fixture.Events);
        var renderer = fixture.CreateRenderer(placement);
        await using var project = CreateProjectLease();
        var window = await renderer.StartAsync(
            project,
            new WallpaperEngineWindowOptions(1920, 1080));
        fixture.AudioLease.DisposeException = new InvalidOperationException("audio cleanup");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await window.DisposeAsync());

        Assert.Equal("audio cleanup", exception.Message);
        Assert.Equal(1, fixture.Control.CloseCount);
        Assert.Equal(1, placement.Lease.DisposeCount);
        Assert.Equal(1, fixture.AudioLease.DisposeCount);
        Assert.Equal(0, fixture.Journal.ClearCount);
        Assert.Null(window.CaptureTarget);

        fixture.AudioLease.DisposeException = null;
        await window.DisposeAsync();

        Assert.Equal(1, fixture.Control.CloseCount);
        Assert.Equal(1, placement.Lease.DisposeCount);
        Assert.Equal(2, fixture.AudioLease.DisposeCount);
        Assert.Equal(1, fixture.Journal.ClearCount);
    }

    [Fact]
    public async Task PlacementCleanupFailureRetainsOnlyTheFailedResourceUntilRetry()
    {
        var fixture = new RendererFixture();
        var placement = new RetryableTestPopOutPlacement(fixture.Events)
        {
            DisposeFailuresRemaining = 1,
        };
        var renderer = fixture.CreateRenderer(placement);
        await using var project = CreateProjectLease();
        var window = await renderer.StartAsync(
            project,
            new WallpaperEngineWindowOptions(1920, 1080));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await window.DisposeAsync());

        Assert.Equal("placement cleanup", exception.Message);
        Assert.Equal(1, fixture.Control.CloseCount);
        Assert.Equal(1, placement.Lease.DisposeCount);
        Assert.Equal(1, fixture.AudioLease.DisposeCount);
        Assert.Equal(0, fixture.Journal.ClearCount);

        await window.DisposeAsync();

        Assert.Equal(1, fixture.Control.CloseCount);
        Assert.Equal(2, placement.Lease.DisposeCount);
        Assert.Equal(1, fixture.AudioLease.DisposeCount);
        Assert.Equal(1, fixture.Journal.ClearCount);
    }

    [Fact]
    public async Task RendererPreservesAuthorizationAndCleanupFailuresFromARejectedPopOut()
    {
        var fixture = new RendererFixture();
        fixture.Control.ReportedPath = @"C:\WallpaperEngine\projects\other\index.html";
        fixture.Control.CloseException = new IOException("close failed");
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();

        var exception = await Assert.ThrowsAsync<AggregateException>(
            async () => await renderer.StartAsync(
                project,
                new WallpaperEngineWindowOptions(1920, 1080)));

        Assert.Collection(
            exception.InnerExceptions,
            failure => Assert.IsType<WallpaperSourceCapabilityException>(failure),
            failure => Assert.IsType<IOException>(failure));
        Assert.Equal(1, fixture.Audio.AcquireCount);
        Assert.Equal(0, fixture.AudioLease.DisposeCount);
        Assert.Equal(1, fixture.PlacementNative.BottomPlacementCount);
        Assert.Equal(0, fixture.PlacementNative.RollbackCount);
        Assert.Equal(0, fixture.Journal.ClearCount);
    }

    [Fact]
    public async Task PausedOwnedWindowClosesAndResumeReauthorizesTheSameProject()
    {
        var fixture = new RendererFixture();
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();
        await using var window = await renderer.StartAsync(
            project,
            new WallpaperEngineWindowOptions(1920, 1080));
        var initialCaptureTarget = Assert.IsType<WallpaperEngineWindowCaptureTarget>(
            window.CaptureTarget);
        Assert.Equal((nint)42, await initialCaptureTarget.RevalidateAsync(default));

        await window.SetPausedAsync(paused: true);
        Assert.Null(window.CaptureTarget);
        var revoked = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            () => initialCaptureTarget.RevalidateAsync(default).AsTask());
        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            revoked.Reason);
        await window.SetPausedAsync(paused: false);

        var resumedCaptureTarget = Assert.IsType<WallpaperEngineWindowCaptureTarget>(
            window.CaptureTarget);
        Assert.NotSame(initialCaptureTarget, resumedCaptureTarget);
        Assert.Equal((nint)42, await resumedCaptureTarget.RevalidateAsync(default));
        Assert.Equal(2, fixture.Control.OpenNames.Count);
        Assert.Equal(fixture.Control.OpenNames[0], fixture.Control.OpenNames[1]);
        Assert.Equal(2, fixture.Verifier.WaitCount);
        Assert.Equal(2, fixture.Audio.AcquireCount);
        Assert.Equal(2, fixture.Journal.RecordCount);
        Assert.Equal(1, fixture.Journal.ClearCount);
        Assert.Equal(2, fixture.PlacementNative.BottomPlacementCount);
        Assert.Equal(0, fixture.PlacementNative.RollbackCount);
        Assert.Equal(2, fixture.Verifier.RevalidateCount);
    }

    [Fact]
    public async Task RendererClosesConfinedWindowBeforeUnmutingWithoutRollback()
    {
        var fixture = new RendererFixture();
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();
        var window = await renderer.StartAsync(
            project,
            new WallpaperEngineWindowOptions(1920, 1080));

        Assert.Equal(
            ["ownership", "place-bottom", "audio-mute", "query"],
            fixture.Events.Where(
                entry => entry is "ownership" or "query" or
                    "audio-mute" or "place-bottom"));

        await window.DisposeAsync();

        Assert.Equal(
            ["close", "confirm-closed", "audio-unmute", "journal-clear"],
            fixture.Events.Where(
                entry => entry is "close" or "confirm-closed" or
                    "audio-unmute" or "journal-clear"));
        Assert.Equal(1, fixture.PlacementNative.BottomPlacementCount);
        Assert.Equal(0, fixture.PlacementNative.RollbackCount);
    }

    [Fact]
    public async Task CloseFailureRetainsConfinedMutedOwnedGraphUntilRetrySucceeds()
    {
        var fixture = new RendererFixture();
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();
        var window = await renderer.StartAsync(
            project,
            new WallpaperEngineWindowOptions(1920, 1080));
        fixture.Control.CloseException = new IOException("close failed");

        var exception = await Assert.ThrowsAsync<IOException>(
            async () => await window.DisposeAsync());

        Assert.Equal("close failed", exception.Message);
        var revokedCaptureTarget = Assert.IsType<WallpaperEngineWindowCaptureTarget>(
            window.CaptureTarget);
        var revoked = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            () => revokedCaptureTarget.RevalidateAsync(default).AsTask());
        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            revoked.Reason);
        Assert.True(fixture.PlacementNative.WindowExists);
        Assert.Equal(
            new WallpaperEngineWindowRect(-3839, -1080, -1919, 0),
            fixture.PlacementNative.CurrentRect);
        Assert.Equal(0, fixture.AudioLease.DisposeCount);
        Assert.Equal(0, fixture.Journal.ClearCount);
        Assert.DoesNotContain("confirm-closed", fixture.Events);
        Assert.Equal(0, fixture.PlacementNative.RollbackCount);

        fixture.Control.CloseException = null;
        await window.DisposeAsync();

        Assert.Null(window.CaptureTarget);
        Assert.Equal(1, fixture.AudioLease.DisposeCount);
        Assert.Equal(1, fixture.Journal.ClearCount);
        Assert.Equal(0, fixture.PlacementNative.RollbackCount);
    }

    [Fact]
    public async Task ExactWindowCloseStillRequiresRetryableGlobalNameAbsenceProof()
    {
        var fixture = new RendererFixture();
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();
        var window = await renderer.StartAsync(
            project,
            new WallpaperEngineWindowOptions(1920, 1080));
        fixture.Verifier.AbsenceException =
            new WallpaperEnginePlatformUnavailableException(
                WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await window.DisposeAsync());

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Equal(1, fixture.Verifier.AbsenceCount);
        Assert.NotNull(window.CaptureTarget);
        Assert.Equal(0, fixture.AudioLease.DisposeCount);
        Assert.Equal(0, fixture.Journal.ClearCount);
        Assert.Equal(
            ["close", "confirm-closed", "absence-proof"],
            fixture.Events.Where(
                entry => entry is "close" or "confirm-closed" or
                    "absence-proof" or "journal-clear"));

        fixture.Verifier.AbsenceException = null;
        await window.DisposeAsync();

        Assert.Equal(2, fixture.Control.CloseCount);
        Assert.Equal(2, fixture.Verifier.AbsenceCount);
        Assert.Null(window.CaptureTarget);
        Assert.Equal(1, fixture.AudioLease.DisposeCount);
        Assert.Equal(1, fixture.Journal.ClearCount);
    }

    [Fact]
    public async Task ProductionInitialPlacementStartsAtTheOnePixelVirtualEdge()
    {
        var fixture = new RendererFixture();
        var renderer = fixture.CreateRendererUsingProductionPlacement();
        await using var project = CreateProjectLease();
        var window = await renderer.StartAsync(
            project,
            new WallpaperEngineWindowOptions(1920, 1080));

        Assert.Equal(
            new WallpaperEngineWindowPlacement(-3839, -1080),
            Assert.Single(fixture.Control.OpenPlacements));

        await window.DisposeAsync();
    }

    [Fact]
    public async Task CancellationAfterPlacementClosesAndDisarmsBeforeStartupFails()
    {
        var fixture = new RendererFixture();
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();
        using var cancellation = new CancellationTokenSource();
        fixture.PlacementNative.AfterBottomPlacement = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await renderer.StartAsync(
                project,
                new WallpaperEngineWindowOptions(1920, 1080),
                cancellation.Token));

        Assert.Equal(1, fixture.Control.CloseCount);
        Assert.Equal(0, fixture.AudioLease.DisposeCount);
        Assert.Equal(1, fixture.Journal.ClearCount);
        Assert.Contains("confirm-closed", fixture.Events);
        Assert.Equal(0, fixture.PlacementNative.RollbackCount);
    }

    [Fact]
    public async Task SuccessfulCloseCommandWithoutWindowDestructionRetainsOwnedGraph()
    {
        var fixture = new RendererFixture();
        var renderer = fixture.CreateRendererWithFastCloseProof();
        await using var project = CreateProjectLease();
        var window = await renderer.StartAsync(
            project,
            new WallpaperEngineWindowOptions(1920, 1080));
        fixture.PlacementNative.IgnoreClose = true;

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await window.DisposeAsync());

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowPlacementNotProven,
            exception.Reason);
        Assert.NotNull(window.CaptureTarget);
        Assert.True(fixture.PlacementNative.WindowExists);
        Assert.Equal(0, fixture.AudioLease.DisposeCount);
        Assert.Equal(0, fixture.Journal.ClearCount);
        Assert.Equal(0, fixture.PlacementNative.RollbackCount);
    }

    [Fact]
    public async Task StartupAndCloseFailurePreserveBothErrorsAndTheConfinedMutedGraph()
    {
        var fixture = new RendererFixture();
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();
        using var cancellation = new CancellationTokenSource();
        fixture.Audio.AfterAcquire = () =>
        {
            cancellation.Cancel();
            fixture.Control.CloseException = new IOException("close failed");
        };

        var exception = await Assert.ThrowsAsync<AggregateException>(
            async () => await renderer.StartAsync(
                project,
                new WallpaperEngineWindowOptions(1920, 1080),
                cancellation.Token));

        Assert.Collection(
            exception.InnerExceptions,
            failure => Assert.IsAssignableFrom<OperationCanceledException>(failure),
            failure => Assert.IsType<IOException>(failure));
        Assert.True(fixture.PlacementNative.WindowExists);
        Assert.Equal(
            new WallpaperEngineWindowRect(-3839, -1080, -1919, 0),
            fixture.PlacementNative.CurrentRect);
        Assert.Equal(0, fixture.AudioLease.DisposeCount);
        Assert.Equal(0, fixture.Journal.ClearCount);
        Assert.Equal(0, fixture.PlacementNative.RollbackCount);
    }

    [Fact]
    public async Task AudioAcquisitionFailureClosesAndDisarmsBeforeAuthorizationQuery()
    {
        var fixture = new RendererFixture();
        fixture.Audio.AcquireException = new InvalidOperationException("mute failed");
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await renderer.StartAsync(
                project,
                new WallpaperEngineWindowOptions(1920, 1080)));

        Assert.Equal("mute failed", exception.Message);
        Assert.DoesNotContain("query", fixture.Events);
        Assert.Contains("confirm-closed", fixture.Events);
        Assert.Equal(1, fixture.Control.CloseCount);
        Assert.Equal(1, fixture.Journal.ClearCount);
        Assert.Equal(0, fixture.PlacementNative.RollbackCount);
    }

    [Fact]
    public async Task ProjectMismatchStaysMutedUntilExactWindowCloseIsProven()
    {
        var fixture = new RendererFixture();
        fixture.Control.ReportedPath = @"C:\WallpaperEngine\projects\other\index.html";
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();

        await Assert.ThrowsAsync<WallpaperSourceCapabilityException>(
            async () => await renderer.StartAsync(
                project,
                new WallpaperEngineWindowOptions(1920, 1080)));

        Assert.Equal(
            ["close", "confirm-closed", "audio-unmute", "journal-clear"],
            fixture.Events.Where(
                entry => entry is "close" or "confirm-closed" or
                    "audio-unmute" or "journal-clear"));
        Assert.Equal(1, fixture.AudioLease.DisposeCount);
        Assert.Equal(0, fixture.PlacementNative.RollbackCount);
    }

    [Fact]
    public async Task FailureBeforeWindowCreationOnlyClearsTheRecoveryJournal()
    {
        var fixture = new RendererFixture();
        fixture.Control.EnsureException = new InvalidOperationException("not running");
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await renderer.StartAsync(
                project,
                new WallpaperEngineWindowOptions(1920, 1080)));

        Assert.Equal("not running", exception.Message);
        Assert.Equal(0, fixture.Control.CloseCount);
        Assert.Equal(0, fixture.PlacementNative.BottomPlacementCount);
        Assert.Equal(0, fixture.Audio.AcquireCount);
        Assert.Equal(1, fixture.Journal.ClearCount);
    }

    [Fact]
    public async Task OwnershipFailureClearsJournalOnlyAfterBoundedAbsenceProof()
    {
        var fixture = new RendererFixture();
        fixture.Verifier.WaitException = new WallpaperEnginePlatformUnavailableException(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven);
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await renderer.StartAsync(
                project,
                new WallpaperEngineWindowOptions(1920, 1080)));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Equal(1, fixture.Control.CloseCount);
        Assert.Equal(1, fixture.Verifier.AbsenceCount);
        Assert.Equal(1, fixture.Journal.ClearCount);
        Assert.Equal(0, fixture.PlacementNative.BottomPlacementCount);
        Assert.Equal(0, fixture.Audio.AcquireCount);
    }

    [Fact]
    public async Task OwnershipFailureRetainsJournalWhenWindowAbsenceCannotBeProven()
    {
        var fixture = new RendererFixture();
        fixture.Verifier.WaitException = new WallpaperEnginePlatformUnavailableException(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven);
        fixture.Verifier.AbsenceException = new WallpaperEnginePlatformUnavailableException(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven);
        var renderer = fixture.CreateRenderer();
        await using var project = CreateProjectLease();

        var exception = await Assert.ThrowsAsync<AggregateException>(
            async () => await renderer.StartAsync(
                project,
                new WallpaperEngineWindowOptions(1920, 1080)));

        Assert.Collection(
            exception.InnerExceptions,
            failure => Assert.IsType<WallpaperEnginePlatformUnavailableException>(failure),
            failure => Assert.IsType<WallpaperEnginePlatformUnavailableException>(failure));
        Assert.Equal(1, fixture.Control.CloseCount);
        Assert.Equal(1, fixture.Verifier.AbsenceCount);
        Assert.Equal(0, fixture.Journal.ClearCount);
        Assert.Equal(0, fixture.PlacementNative.BottomPlacementCount);
        Assert.Equal(0, fixture.Audio.AcquireCount);
    }

    private static TestProjectLease CreateProjectLease()
    {
        var reference = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineLocalProject,
            SourceIdentifier = @"C:\WallpaperEngine\projects\web\project.json",
            LastKnownKind = MediaKind.None,
            LastKnownContentKind = WallpaperContentKind.Web,
            LastKnownDisplayName = "Fixture web wallpaper",
        };
        var descriptor = new WallpaperSourceDescriptor(
            reference.SourceKind,
            reference.SourceIdentifier,
            "Fixture web wallpaper",
            WallpaperContentKind.Web,
            WallpaperDeliveryKind.WallpaperEngineWindow,
            WallpaperDeliveryCapabilities.DynamicFrames);
        return new TestProjectLease(
            new WallpaperSourceResolution(reference, descriptor, directMediaMetadata: null),
            @"C:\WallpaperEngine\projects\web\index.html");
    }

    private sealed class RendererFixture
    {
        internal RendererFixture()
        {
            Installation = new WallpaperEngineInstallation(
                @"C:\Steam",
                @"C:\Steam\steamapps\common\wallpaper_engine",
                @"C:\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe",
                [@"C:\Steam"]);
            Locator = new TestInstallationLocator(Installation);
            var verifiedWindow = new WallpaperEngineVerifiedWindow(
                (nint)42,
                processId: 9001,
                DateTimeOffset.UtcNow,
                Installation.ControlExecutablePath,
                WallpaperEngineOwnedWindowName.Create(1));
            PlacementNative = new RendererPlacementNativeApi(verifiedWindow, Events);
            HealthScheduler = new ManuallyAdvancedHealthScheduler();
            PopOutPlacement = new WindowsWallpaperEnginePopOutPlacement(
                PlacementNative,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(25),
                TimeSpan.FromSeconds(1),
                HealthScheduler);
            Control = new TestControlClient(PlacementNative, Events)
            {
                ReportedPath = @"C:\WallpaperEngine\projects\web\index.html",
            };
            Verifier = new TestWindowVerifier(verifiedWindow, Events);
            Audio = new TestAudioIsolation(Events);
            Journal = new TestWindowJournal(Events);
        }

        internal List<string> Events { get; } = [];

        internal WallpaperEngineInstallation Installation { get; }

        internal TestInstallationLocator Locator { get; }

        internal TestControlClient Control { get; }

        internal TestWindowVerifier Verifier { get; }

        internal RendererPlacementNativeApi PlacementNative { get; }

        internal WindowsWallpaperEnginePopOutPlacement PopOutPlacement { get; }

        internal ManuallyAdvancedHealthScheduler HealthScheduler { get; }

        internal TestAudioIsolation Audio { get; }

        internal TestAudioLease AudioLease => Audio.Lease;

        internal TestWindowJournal Journal { get; }

        internal WallpaperEngineWindowRenderer CreateRenderer(
            IWallpaperEnginePopOutPlacement? popOutPlacement = null) => new(
            Locator,
            Control,
            Verifier,
            Audio,
            Journal,
            popOutPlacement ?? PopOutPlacement,
            new WallpaperEngineWindowPlacement(-4096, -2160));

        internal WallpaperEngineWindowRenderer CreateRendererAllowingAudioPlayback() => new(
            Locator,
            Control,
            Verifier,
            Journal,
            PopOutPlacement);

        internal WallpaperEngineWindowRenderer CreateRendererUsingProductionPlacement() => new(
            Locator,
            Control,
            Verifier,
            Audio,
            Journal,
            PopOutPlacement);

        internal WallpaperEngineWindowRenderer CreateRendererWithFastCloseProof() => new(
            Locator,
            Control,
            Verifier,
            Audio,
            Journal,
            new WindowsWallpaperEnginePopOutPlacement(
                PlacementNative,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.Zero),
            new WallpaperEngineWindowPlacement(-4096, -2160));
    }

    private sealed class TestProjectLease(
        WallpaperSourceResolution resolution,
        string launchPath) : IWallpaperEngineProjectLease
    {
        public WallpaperSourceResolution Resolution { get; } = resolution;

        public string LaunchPath { get; } = launchPath;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public override string ToString() =>
            $"{nameof(TestProjectLease)} {{ LaunchPath = <redacted> }}";
    }

    private sealed class TestInstallationLocator(WallpaperEngineInstallation installation)
        : IWallpaperEngineInstallationLocator
    {
        public ValueTask<WallpaperEngineInstallation> LocateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(installation);
        }
    }

    private sealed class TestControlClient(
        RendererPlacementNativeApi placementNative,
        List<string> events) : IWallpaperEngineControlClient
    {
        internal string? ReportedPath { get; set; }

        internal Exception? CloseException { get; set; }

        internal Exception? EnsureException { get; set; }

        internal int CloseCount { get; private set; }

        internal List<WallpaperEngineOwnedWindowName> OpenNames { get; } = [];

        internal List<WallpaperEngineWindowPlacement> OpenPlacements { get; } = [];

        public ValueTask EnsureRunningAsync(
            WallpaperEngineInstallation installation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EnsureException is not null)
            {
                return ValueTask.FromException(EnsureException);
            }

            events.Add("ensure-running");
            return ValueTask.CompletedTask;
        }

        public ValueTask OpenWindowAsync(
            WallpaperEngineInstallation installation,
            string launchPath,
            WallpaperEngineOwnedWindowName windowName,
            WallpaperEngineWindowOptions options,
            WallpaperEngineWindowPlacement placement,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenNames.Add(windowName);
            OpenPlacements.Add(placement);
            events.Add("open");
            placementNative.OpenWindow();
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> QueryWindowWallpaperAsync(
            WallpaperEngineInstallation installation,
            WallpaperEngineOwnedWindowName windowName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("query");
            return ValueTask.FromResult(ReportedPath);
        }

        public ValueTask CloseWindowAsync(
            WallpaperEngineInstallation installation,
            WallpaperEngineOwnedWindowName windowName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseCount++;
            events.Add("close");
            if (CloseException is not null)
            {
                return ValueTask.FromException(CloseException);
            }

            placementNative.CloseWindow();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestWindowVerifier(
        WallpaperEngineVerifiedWindow verifiedWindow,
        List<string> events)
        : IWallpaperEngineOwnedWindowVerifier
    {
        internal int WaitCount { get; private set; }

        internal int AbsenceCount { get; private set; }

        internal int RevalidateCount { get; private set; }

        internal Exception? WaitException { get; set; }

        internal Exception? AbsenceException { get; set; }

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
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitCount++;
            events.Add("ownership");
            if (WaitException is not null)
            {
                return ValueTask.FromException<WallpaperEngineVerifiedWindow>(WaitException);
            }

            return ValueTask.FromResult(verifiedWindow);
        }

        public ValueTask RevalidateOwnedWindowAsync(
            WallpaperEngineVerifiedWindow expectedWindow,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevalidateCount++;
            if (!ReferenceEquals(expectedWindow, verifiedWindow) &&
                expectedWindow != verifiedWindow)
            {
                return ValueTask.FromException(
                    new WallpaperEnginePlatformUnavailableException(
                        WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ConfirmOwnedWindowAbsentAsync(
            WallpaperEngineInstallation installation,
            WallpaperEngineOwnedWindowName windowName,
            WallpaperEngineWindowBaseline baseline,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AbsenceCount++;
            events.Add("absence-proof");
            return AbsenceException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(AbsenceException);
        }
    }

    private sealed class TestAudioIsolation(List<string> events)
        : IWallpaperEngineAudioIsolation
    {
        internal TestAudioBaseline Baseline { get; } = new(events);

        internal TestAudioLease Lease { get; } = new(events);

        internal int AcquireCount { get; private set; }

        internal IWallpaperEngineAudioBaseline? AcquiredBaseline { get; private set; }

        internal Action? AfterAcquire { get; set; }

        internal Exception? AcquireException { get; set; }

        public ValueTask<IWallpaperEngineAudioBaseline> CaptureBaselineAsync(
            WallpaperEngineInstallation installation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("audio-baseline");
            return ValueTask.FromResult<IWallpaperEngineAudioBaseline>(Baseline);
        }

        public ValueTask<IWallpaperEngineMutedAudioLease> AcquireMutedSessionAsync(
            WallpaperEngineVerifiedWindow window,
            IWallpaperEngineAudioBaseline baseline,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            AcquiredBaseline = baseline;
            events.Add("audio-mute");
            AfterAcquire?.Invoke();
            if (AcquireException is not null)
            {
                return ValueTask.FromException<IWallpaperEngineMutedAudioLease>(
                    AcquireException);
            }

            return ValueTask.FromResult<IWallpaperEngineMutedAudioLease>(Lease);
        }
    }

    private sealed class TestAudioBaseline(List<string> events)
        : IWallpaperEngineAudioBaseline
    {
        public bool InitialSilenceIsProven { get; set; } = true;

        internal int DisposeCount { get; private set; }

        internal Exception? DisposeException { get; set; }

        internal int DisposeFailuresRemaining { get; set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            events.Add("baseline-release");
            if (DisposeFailuresRemaining > 0)
            {
                DisposeFailuresRemaining--;
                return ValueTask.FromException(
                    DisposeException ?? new InvalidOperationException("baseline cleanup"));
            }

            return ValueTask.CompletedTask;
        }

        public override string ToString() =>
            $"{nameof(TestAudioBaseline)} {{ Identities = <redacted> }}";
    }

    private sealed class TestAudioLease(List<string> events)
        : IWallpaperEngineMutedAudioLease
    {
        private readonly TaskCompletionSource<WallpaperEnginePlatformUnavailableReason>
            _isolationLost = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int DisposeCount { get; private set; }

        internal Exception? DisposeException { get; set; }

        public Task<WallpaperEnginePlatformUnavailableReason> IsolationLost =>
            _isolationLost.Task;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            events.Add("audio-unmute");
            if (DisposeException is not null)
            {
                return ValueTask.FromException(DisposeException);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestWindowJournal(List<string> events)
        : IWallpaperEngineOwnedWindowJournal
    {
        internal int RecordCount { get; private set; }

        internal int ClearCount { get; private set; }

        public ValueTask RecordAsync(
            WallpaperEngineOwnedWindowName windowName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(
            WallpaperEngineOwnedWindowName windowName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearCount++;
            events.Add("journal-clear");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RetryableTestPopOutPlacement(List<string> events)
        : IWallpaperEnginePopOutPlacement
    {
        internal RetryableTestPlacementLease Lease { get; } = new(events);

        internal int DisposeFailuresRemaining
        {
            get => Lease.DisposeFailuresRemaining;
            set => Lease.DisposeFailuresRemaining = value;
        }

        public WallpaperEngineWindowPlacement GetInitialPlacement(
            WallpaperEngineWindowOptions options) =>
            new(-4096, -2160);

        public ValueTask<IWallpaperEnginePopOutPlacementLease> PlaceAsync(
            WallpaperEngineVerifiedWindow verifiedWindow,
            WallpaperEngineWindowOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IWallpaperEnginePopOutPlacementLease>(Lease);
        }
    }

    private sealed class RetryableTestPlacementLease(List<string> events)
        : IWallpaperEnginePopOutPlacementLease
    {
        internal int DisposeCount { get; private set; }

        internal int DisposeFailuresRemaining { get; set; }

        public Task Completion { get; } = Task.CompletedTask;

        public ValueTask MarkClosedAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("confirm-closed");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            events.Add("placement-release");
            if (DisposeFailuresRemaining > 0)
            {
                DisposeFailuresRemaining--;
                return ValueTask.FromException(
                    new InvalidOperationException("placement cleanup"));
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RendererPlacementNativeApi(
        WallpaperEngineVerifiedWindow verifiedWindow,
        List<string> events) : IWallpaperEngineWindowPlacementNativeApi
    {
        private const uint TopMost = 0x00000008;
        private readonly WallpaperEngineNativeWindowState _openedState = new(
            verifiedWindow.WindowHandle,
            verifiedWindow.WindowName.Value,
            verifiedWindow.ProcessId,
            verifiedWindow.ProcessStartTimeUtc,
            verifiedWindow.ProcessPath,
            new WallpaperEngineWindowRect(-4096, -2160, -2176, -1080),
            style: 0x10CF0000,
            extendedStyle: TopMost);
        private WallpaperEngineNativeWindowState _state = null!;

        internal bool WindowExists { get; private set; }

        internal int BottomPlacementCount { get; private set; }

        internal int RollbackCount { get; private set; }

        internal WallpaperEngineWindowRect CurrentRect => _state.Rect;

        internal Action? AfterBottomPlacement { get; set; }

        internal bool IgnoreClose { get; set; }

        internal void MoveToVisibleArea() =>
            _state = _state with
            {
                Rect = new WallpaperEngineWindowRect(100, 200, 2020, 1280),
            };

        internal void OpenWindow()
        {
            _state = _openedState;
            WindowExists = true;
        }

        internal void CloseWindow()
        {
            if (!IgnoreClose)
            {
                WindowExists = false;
            }
        }

        public WallpaperEngineWindowRect GetVirtualScreen() =>
            new(-1920, -1080, 1920, 1080);

        public nint GetForegroundWindow() => (nint)99;

        public bool DoesWindowExist(nint windowHandle)
        {
            Assert.Equal(verifiedWindow.WindowHandle, windowHandle);
            events.Add("confirm-closed");
            return WindowExists;
        }

        public WallpaperEngineNativeWindowState CaptureWindowState(nint windowHandle)
        {
            Assert.True(WindowExists);
            Assert.Equal(verifiedWindow.WindowHandle, windowHandle);
            return _state;
        }

        public WallpaperEngineWindowRect GetClientRect(nint windowHandle)
        {
            Assert.True(WindowExists);
            Assert.Equal(verifiedWindow.WindowHandle, windowHandle);
            return new WallpaperEngineWindowRect(
                0,
                0,
                _state.Rect.Width,
                _state.Rect.Height);
        }

        public void SetWindowPosition(
            nint windowHandle,
            nint insertAfter,
            WallpaperEngineWindowRect rect,
            WallpaperEngineSetWindowPositionFlags flags)
        {
            Assert.True(WindowExists);
            Assert.Equal(verifiedWindow.WindowHandle, windowHandle);
            if (insertAfter == (nint)1)
            {
                BottomPlacementCount++;
                events.Add("place-bottom");
                _state = _state with
                {
                    Rect = rect,
                    ExtendedStyle = _state.ExtendedStyle & ~TopMost,
                };
                AfterBottomPlacement?.Invoke();
            }
            else
            {
                RollbackCount++;
                events.Add("rollback-position");
                _state = _state with { Rect = rect };
            }
        }

        public void SetWindowStyles(nint windowHandle, uint style, uint extendedStyle)
        {
            Assert.True(WindowExists);
            Assert.Equal(verifiedWindow.WindowHandle, windowHandle);
            if (style == _openedState.Style &&
                extendedStyle == _openedState.ExtendedStyle)
            {
                RollbackCount++;
                events.Add("rollback-style");
            }

            _state = _state with
            {
                Style = style,
                ExtendedStyle = extendedStyle,
            };
        }
    }

    private sealed class ManuallyAdvancedHealthScheduler
        : IWallpaperEnginePopOutHealthScheduler
    {
        private readonly System.Threading.Channels.Channel<bool> _ticks =
            System.Threading.Channels.Channel.CreateUnbounded<bool>();

        internal void Advance() => Assert.True(_ticks.Writer.TryWrite(true));

        public async ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken) =>
            _ = await _ticks.Reader
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
    }
}
