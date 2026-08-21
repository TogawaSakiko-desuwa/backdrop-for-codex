using BackdropForCodex.Core.Dynamic;
using Xunit;

namespace BackdropForCodex.Core.Tests.Dynamic;

public sealed class MediaFoundationFragmentedMp4EncoderFactoryTests
{
    [Fact]
    public void EveryAdaptiveRenderProfileIsAcceptedByTheEncoderContract()
    {
        foreach (var profile in DynamicWallpaperRenderProfiles.InPreferenceOrder)
        {
            var descriptor = new EncodedWallpaperStreamDescriptor(
                generation: 41,
                "video/mp4; codecs=\"avc1.640028\"",
                profile.Width,
                profile.Height,
                profile.FrameRate);

            Assert.True(
                MediaFoundationFragmentedMp4EncoderFactory.IsSupportedProfile(descriptor),
                $"The {profile.Width}x{profile.Height}@{profile.FrameRate} profile was rejected.");
        }
    }

    [Fact]
    public void UndocumentedRenderProfileIsRejectedByTheEncoderContract()
    {
        var descriptor = new EncodedWallpaperStreamDescriptor(
            generation: 41,
            "video/mp4; codecs=\"avc1.640028\"",
            width: 640,
            height: 480,
            frameRate: 24);

        Assert.False(MediaFoundationFragmentedMp4EncoderFactory.IsSupportedProfile(descriptor));
    }

    [Fact]
    public void PauseResetDiscardsPendingFramesAndRestartsTheInitialCadence()
    {
        var boundary = new MediaFoundationFragmentBoundaryState(
            frameRate: 15,
            isPrimary: false);

        for (var frame = 0; frame < 15; frame++)
        {
            Assert.Equal(frame == 14, boundary.RecordFrame());
        }

        boundary.ValidateFragmentForPublication(startsWithKeyFrame: true);
        boundary.MarkFragmentPublished();
        Assert.Equal(30, boundary.RequiredFrameCount);

        for (var frame = 0; frame < 7; frame++)
        {
            Assert.False(boundary.RecordFrame());
        }

        boundary.ResetForCapturePause();

        Assert.Equal(0, boundary.PendingFrameCount);
        Assert.Equal(15, boundary.RequiredFrameCount);
        for (var frame = 0; frame < 15; frame++)
        {
            Assert.Equal(frame == 14, boundary.RecordFrame());
        }
    }

    [Fact]
    public void AResumedFragmentMustStartWithANewKeyFrame()
    {
        var boundary = new MediaFoundationFragmentBoundaryState(
            frameRate: 15,
            isPrimary: false);
        boundary.RecordFrame();
        boundary.ResetForCapturePause();
        for (var frame = 0; frame < 15; frame++)
        {
            boundary.RecordFrame();
        }

        var exception = Assert.Throws<DynamicWallpaperUnavailableException>(() =>
            boundary.ValidateFragmentForPublication(startsWithKeyFrame: false));

        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.EncodingFailed,
            exception.ReasonCode);
        boundary.ValidateFragmentForPublication(startsWithKeyFrame: true);
    }

    [Fact]
    public void CompleteRejectsAnIncompleteInitialFragmentBeforeItsPublishCallback()
    {
        var boundary = new MediaFoundationFragmentBoundaryState(
            frameRate: 15,
            isPrimary: false);
        var publishCalls = 0;
        for (var frame = 0; frame < 14; frame++)
        {
            boundary.RecordFrame();
        }

        var exception = Assert.Throws<DynamicWallpaperUnavailableException>(() =>
            boundary.FlushPendingFragmentForCompletion(() => publishCalls++));

        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.EncodingFailed,
            exception.ReasonCode);
        Assert.Equal(0, publishCalls);
    }

    [Fact]
    public void CompleteMayPublishATerminalPartialOnlyAfterTheInitialFragment()
    {
        var boundary = new MediaFoundationFragmentBoundaryState(
            frameRate: 15,
            isPrimary: false);
        for (var frame = 0; frame < 15; frame++)
        {
            boundary.RecordFrame();
        }

        boundary.MarkFragmentPublished();
        for (var frame = 0; frame < 7; frame++)
        {
            boundary.RecordFrame();
        }

        var publishCalls = 0;
        boundary.FlushPendingFragmentForCompletion(() => publishCalls++);

        Assert.Equal(1, publishCalls);
    }

    [Fact]
    public void PrimaryPauseResetDoesNotChargeThePausedWallClock()
    {
        var gate = new MediaFoundationPrimaryThroughputGate();
        gate.BeginFrame(TimeSpan.Zero);
        gate.CompleteFrame(TimeSpan.FromSeconds(0.9));

        gate.ResetForCapturePause();
        for (var frame = 0; frame < 30; frame++)
        {
            var startedAt = TimeSpan.FromSeconds(100 + (frame / 30d));
            gate.BeginFrame(startedAt);
            gate.CompleteFrame(startedAt + TimeSpan.FromMilliseconds(1));
        }
    }

    [Fact]
    public void FallbackUsesOneSecondStartupThenTwoSecondSteadyFragments()
    {
        var initialFrameCount = MediaFoundationFragmentCadence.GetInitialFrameCount(
            frameRate: 15);
        var steadyStateFrameCount =
            MediaFoundationFragmentCadence.GetSteadyStateFrameCount(
                frameRate: 15,
                isPrimary: false);

        Assert.Equal(15, initialFrameCount);
        Assert.Equal(30, steadyStateFrameCount);
        Assert.True(MediaFoundationFragmentCadence.IsFragmentBoundary(
            completedFrameCount: 15,
            initialFrameCount,
            steadyStateFrameCount));
        Assert.False(MediaFoundationFragmentCadence.IsFragmentBoundary(
            completedFrameCount: 30,
            initialFrameCount,
            steadyStateFrameCount));
        Assert.True(MediaFoundationFragmentCadence.IsFragmentBoundary(
            completedFrameCount: 45,
            initialFrameCount,
            steadyStateFrameCount));
    }

    [Fact]
    public void PrimaryKeepsOneSecondFragmentsThroughout()
    {
        var initialFrameCount = MediaFoundationFragmentCadence.GetInitialFrameCount(
            frameRate: 30);
        var steadyStateFrameCount =
            MediaFoundationFragmentCadence.GetSteadyStateFrameCount(
                frameRate: 30,
                isPrimary: true);

        Assert.Equal(30, initialFrameCount);
        Assert.Equal(30, steadyStateFrameCount);
        Assert.True(MediaFoundationFragmentCadence.IsFragmentBoundary(
            completedFrameCount: 30,
            initialFrameCount,
            steadyStateFrameCount));
        Assert.True(MediaFoundationFragmentCadence.IsFragmentBoundary(
            completedFrameCount: 60,
            initialFrameCount,
            steadyStateFrameCount));
    }

    [Fact]
    public async Task ProbeReturnsATypedHardwareH264Capability()
    {
        var factory = new MediaFoundationFragmentedMp4EncoderFactory();

        var capability = await factory.ProbeAsync();

        Assert.True(
            capability.IsAvailable ||
            capability.ReasonCode is
                DynamicWallpaperCapabilityReasonCode.UnsupportedOperatingSystem or
                DynamicWallpaperCapabilityReasonCode.HardwareEncoderUnavailable or
                DynamicWallpaperCapabilityReasonCode.CodecUnavailable);
    }

    [Fact]
    public async Task StartRejectsAnUndocumentedRenderTier()
    {
        var factory = new MediaFoundationFragmentedMp4EncoderFactory();
        var descriptor = new EncodedWallpaperStreamDescriptor(
            generation: 29,
            "video/mp4; codecs=\"avc1.640028\"",
            width: 640,
            height: 480,
            frameRate: 24);

        var exception = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(
            () => factory.StartAsync(descriptor).AsTask());

        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.CodecUnavailable,
            exception.ReasonCode);
    }

    [Fact]
    public async Task QueuedEncoderOperationIsRejectedCleanlyWhenDisposalStarts()
    {
        var gate = new MediaFoundationEncoderOperationGate();
        var owner = new object();
        await gate.WaitAsync(owner);
        var queuedOperation = gate.WaitAsync(owner).AsTask();

        Assert.True(gate.TryBeginDispose());
        var disposal = gate.WaitForDisposalAsync().AsTask();
        gate.Release();

        await disposal.WaitAsync(TimeSpan.FromSeconds(1));
        gate.MarkDisposeCompleted();
        gate.Release();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queuedOperation);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            gate.WaitAsync(owner).AsTask());
        Assert.False(gate.TryBeginDispose());
    }

    [Fact]
    public async Task EncoderDisposalAdmissionCanRetryAfterTheFirstCleanupFails()
    {
        var gate = new MediaFoundationEncoderOperationGate();

        Assert.True(gate.TryBeginDispose());
        await gate.WaitForDisposalAsync();
        gate.Release();

        Assert.True(gate.TryBeginDispose());
        await gate.WaitForDisposalAsync();
        gate.MarkDisposeCompleted();
        gate.Release();

        Assert.True(gate.IsDisposeCompleted);
        Assert.False(gate.TryBeginDispose());
    }
}
