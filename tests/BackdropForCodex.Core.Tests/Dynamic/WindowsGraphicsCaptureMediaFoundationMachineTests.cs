using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using Windows.Graphics.Capture;

namespace BackdropForCodex.Core.Tests.Dynamic;

public sealed class WindowsGraphicsCaptureMediaFoundationMachineTests
{
    private const long MaximumManagedBytesForSixtyFrames = 64L * 1024 * 1024;
    private const long MaximumSoakPrivateBytesGrowth = 256L * 1024 * 1024;
    private const long MaximumSoakManagedBytesGrowth = 64L * 1024 * 1024;

    private readonly ITestOutputHelper _output;

    public WindowsGraphicsCaptureMediaFoundationMachineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RealDynamicWallpaperFact]
    [Trait("Category", "RealDynamicWallpaper")]
    public async Task OwnedSyntheticHwndSustainsPrimaryTierWithoutCaptureDrops()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var descriptor = new EncodedWallpaperStreamDescriptor(
            generation: 57,
            "video/mp4; codecs=\"avc1.640028\"",
            DynamicWallpaperRenderProfiles.Primary.Width,
            DynamicWallpaperRenderProfiles.Primary.Height,
            DynamicWallpaperRenderProfiles.Primary.FrameRate);
        var captureFactory = new WindowsGraphicsCaptureFactory();
        var encoderFactory = new MediaFoundationFragmentedMp4EncoderFactory();
        var captureCapability = await captureFactory.ProbeAsync(timeout.Token);
        var encoderCapability = await encoderFactory.ProbeAsync(timeout.Token);
        Assert.True(
            captureCapability.IsAvailable,
            $"WGC unavailable: reason={captureCapability.ReasonCode}");
        Assert.True(
            encoderCapability.IsAvailable,
            $"MF H264 unavailable: reason={encoderCapability.ReasonCode}");

        using var window = new SyntheticCaptureWindow(
            descriptor.Width,
            descriptor.Height,
            timerPeriodMilliseconds: 8);
        await using var capture = await captureFactory.StartAsync(
            new WallpaperWindowCaptureRequest(
                descriptor.Generation,
                CreateCaptureTarget(window.Handle),
                descriptor.Width,
                descriptor.Height,
                descriptor.FrameRate),
            timeout.Token);
        await using var encoder = await encoderFactory.StartAsync(
            descriptor,
            timeout.Token);
        await using var stream = new EncodedWallpaperStreamBuffer(descriptor);
        var receivedSegments = ReadAllSegmentsAsync(stream, timeout.Token);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long firstSequence = -1;
        long lastSequence = -1;
        var encodedFrames = 0;
        DynamicWallpaperUnavailableException? unavailable = null;
        try
        {
            await foreach (var frame in capture.ReadFramesAsync(timeout.Token))
            {
                await using (frame)
                {
                    firstSequence = firstSequence < 0 ? frame.Sequence : firstSequence;
                    lastSequence = frame.Sequence;
                    await encoder.EncodeAsync(frame, stream, timeout.Token);
                }

                encodedFrames++;
                if (encodedFrames >= 30)
                {
                    break;
                }
            }
        }
        catch (DynamicWallpaperUnavailableException exception) when (
            exception.ReasonCode ==
                DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable)
        {
            unavailable = exception;
        }

        stopwatch.Stop();
        if (unavailable is not null)
        {
            _output.WriteLine(
                "primary=unavailable reason={0} frames={1} elapsedSeconds={2:F3}",
                unavailable.ReasonCode,
                encodedFrames,
                stopwatch.Elapsed.TotalSeconds);
            Assert.True(
                stopwatch.Elapsed <= TimeSpan.FromSeconds(5),
                $"Primary calibration took {stopwatch.Elapsed.TotalSeconds:F2}s.");
            Assert.InRange(encodedFrames, 0, 29);
            var streamFailure = await Assert.ThrowsAsync<
                DynamicWallpaperUnavailableException>(() => receivedSegments);
            Assert.Equal(unavailable.ReasonCode, streamFailure.ReasonCode);
            return;
        }

        Assert.Equal(30, encodedFrames);
        Assert.InRange(lastSequence - firstSequence, 29, 33);
        var effectiveFramesPerSecond = encodedFrames / stopwatch.Elapsed.TotalSeconds;
        _output.WriteLine(
            "primary=sustained frames={0} elapsedSeconds={1:F3} fps={2:F2}",
            encodedFrames,
            stopwatch.Elapsed.TotalSeconds,
            effectiveFramesPerSecond);
        Assert.True(
            effectiveFramesPerSecond >= 27,
            $"Primary tier sustained only {effectiveFramesPerSecond:F2} fps " +
            $"over {stopwatch.Elapsed.TotalSeconds:F2}s.");

        await encoder.CompleteAsync(stream, timeout.Token);
        var segments = await receivedSegments;
        var initialization = Assert.Single(
            segments,
            segment => segment.Kind == EncodedWallpaperSegmentKind.Initialization);
        var media = Assert.Single(
            segments.Where(segment => segment.Kind == EncodedWallpaperSegmentKind.Media).Take(1));
        Assert.Equal(EncodedWallpaperSegmentKind.Initialization, initialization.Kind);
        Assert.True(media.IsKeyFrame);
    }

    [RealDynamicWallpaperFact]
    [Trait("Category", "RealDynamicWallpaper")]
    public async Task OwnedSyntheticHwndProducesHighProfileFragmentsDecodedByRealEdgeMse()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var descriptor = new EncodedWallpaperStreamDescriptor(
            generation: 59,
            "video/mp4; codecs=\"avc1.640028\"",
            DynamicWallpaperRenderProfiles.Fallback.Width,
            DynamicWallpaperRenderProfiles.Fallback.Height,
            DynamicWallpaperRenderProfiles.Fallback.FrameRate);
        var captureFactory = new WindowsGraphicsCaptureFactory();
        var encoderFactory = new MediaFoundationFragmentedMp4EncoderFactory();
        var captureCapability = await captureFactory.ProbeAsync(timeout.Token);
        var encoderCapability = await encoderFactory.ProbeAsync(timeout.Token);
        Assert.True(
            captureCapability.IsAvailable,
            $"WGC unavailable: reason={captureCapability.ReasonCode}");
        Assert.True(
            encoderCapability.IsAvailable,
            $"MF H264 unavailable: reason={encoderCapability.ReasonCode}");

        using var window = new SyntheticCaptureWindow(
            descriptor.Width,
            descriptor.Height);
        await using var encoder = await encoderFactory.StartAsync(
            descriptor,
            timeout.Token);
        await using var stream = new EncodedWallpaperStreamBuffer(descriptor);
        var receivedSegments = ReadAllSegmentsAsync(stream, timeout.Token);

        var firstSessionStart = await EncodeFramesFromFreshSessionAsync(
            captureFactory,
            window.Handle,
            descriptor,
            encoder,
            stream,
            frameCount: 5,
            timeout.Token);
        var resumedSessionStart = await EncodeFramesFromFreshSessionAsync(
            captureFactory,
            window.Handle,
            descriptor,
            encoder,
            stream,
            frameCount: 10,
            timeout.Token);
        var steadySessionStart = await EncodeFramesFromFreshSessionAsync(
            captureFactory,
            window.Handle,
            descriptor,
            encoder,
            stream,
            frameCount: 30,
            timeout.Token);
        Assert.Equal(0, firstSessionStart);
        Assert.Equal(0, resumedSessionStart);
        Assert.Equal(0, steadySessionStart);

        await encoder.CompleteAsync(stream, timeout.Token);
        var segments = await receivedSegments;
        var initialization = Assert.Single(
            segments,
            segment => segment.Kind == EncodedWallpaperSegmentKind.Initialization);
        var media = segments
            .Where(segment => segment.Kind == EncodedWallpaperSegmentKind.Media)
            .ToArray();
        Assert.Equal(EncodedWallpaperSegmentKind.Initialization, initialization.Kind);
        Assert.True(media.Length >= 2);
        Assert.True(media[0].IsKeyFrame);
        Assert.Equal(
            Enumerable.Range(0, segments.Count).Select(static index => (long)index),
            segments.Select(static segment => segment.Sequence));
        Assert.True(initialization.Payload.Length < EncodedWallpaperStreamBuffer.MaximumBufferedBytes);
        Assert.All(
            media,
            segment => Assert.True(
                segment.Payload.Length < EncodedWallpaperStreamBuffer.MaximumBufferedBytes));

        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await page.SetContentAsync(
                "<!doctype html><html><body><video id=wallpaper muted playsinline></video></body></html>");
            var receipt = await page.EvaluateExpressionAsync<DecodeReceipt>(
                CreateDecodeExpression(descriptor, initialization, media));

            Assert.True(receipt.TypeSupported, receipt.Error);
            Assert.True(receipt.Decoded, receipt.Error);
            Assert.Equal(descriptor.Width, receipt.VideoWidth);
            Assert.Equal(descriptor.Height, receipt.VideoHeight);
            Assert.True(receipt.ReadyState >= 2, receipt.Error);
            _output.WriteLine(
                "fallback=edge-mse-decoded width={0} height={1} readyState={2}",
                receipt.VideoWidth,
                receipt.VideoHeight,
                receipt.ReadyState);
        });

    }

    [RealDynamicWallpaperFact]
    [Trait("Category", "RealDynamicWallpaper")]
    public async Task CompatibilityTierProducesAFragmentDecodedByRealEdgeMse()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var profile = DynamicWallpaperRenderProfiles.Compatibility;
        var descriptor = new EncodedWallpaperStreamDescriptor(
            generation: 60,
            "video/mp4; codecs=\"avc1.640028\"",
            profile.Width,
            profile.Height,
            profile.FrameRate);
        var captureFactory = new WindowsGraphicsCaptureFactory();
        var encoderFactory = new MediaFoundationFragmentedMp4EncoderFactory();
        using var window = new SyntheticCaptureWindow(
            descriptor.Width,
            descriptor.Height);
        await using var encoder = await encoderFactory.StartAsync(
            descriptor,
            timeout.Token);
        await using var stream = new EncodedWallpaperStreamBuffer(descriptor);
        var receivedSegments = ReadAllSegmentsAsync(stream, timeout.Token);

        _ = await EncodeFramesFromFreshSessionAsync(
            captureFactory,
            window.Handle,
            descriptor,
            encoder,
            stream,
            frameCount: 5,
            timeout.Token);
        await encoder.CompleteAsync(stream, timeout.Token);
        var segments = await receivedSegments;
        var initialization = Assert.Single(
            segments,
            segment => segment.Kind == EncodedWallpaperSegmentKind.Initialization);
        var media = segments
            .Where(segment => segment.Kind == EncodedWallpaperSegmentKind.Media)
            .ToArray();
        Assert.NotEmpty(media);
        Assert.True(media[0].IsKeyFrame);

        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await page.SetContentAsync(
                "<!doctype html><html><body><video id=wallpaper muted playsinline></video></body></html>");
            var receipt = await page.EvaluateExpressionAsync<DecodeReceipt>(
                CreateDecodeExpression(descriptor, initialization, media));

            Assert.True(receipt.TypeSupported, receipt.Error);
            Assert.True(receipt.Decoded, receipt.Error);
            Assert.Equal(descriptor.Width, receipt.VideoWidth);
            Assert.Equal(descriptor.Height, receipt.VideoHeight);
            Assert.True(receipt.ReadyState >= 2, receipt.Error);
        });
    }

    [RealDynamicWallpaperFact]
    [Trait("Category", "RealDynamicWallpaper")]
    public async Task FallbackTierDoesNotAllocateAFullManagedPixelBufferPerFrame()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var descriptor = new EncodedWallpaperStreamDescriptor(
            generation: 61,
            "video/mp4; codecs=\"avc1.640028\"",
            DynamicWallpaperRenderProfiles.Fallback.Width,
            DynamicWallpaperRenderProfiles.Fallback.Height,
            DynamicWallpaperRenderProfiles.Fallback.FrameRate);
        var captureFactory = new WindowsGraphicsCaptureFactory();
        var encoderFactory = new MediaFoundationFragmentedMp4EncoderFactory();
        var captureCapability = await captureFactory.ProbeAsync(timeout.Token);
        var encoderCapability = await encoderFactory.ProbeAsync(timeout.Token);
        Assert.True(
            captureCapability.IsAvailable,
            $"WGC unavailable: reason={captureCapability.ReasonCode}");
        Assert.True(
            encoderCapability.IsAvailable,
            $"MF H264 unavailable: reason={encoderCapability.ReasonCode}");

        using var window = new SyntheticCaptureWindow(
            descriptor.Width,
            descriptor.Height);
        await using var capture = await captureFactory.StartAsync(
            new WallpaperWindowCaptureRequest(
                descriptor.Generation,
                CreateCaptureTarget(window.Handle),
                descriptor.Width,
                descriptor.Height,
                DynamicWallpaperRenderProfiles.Fallback.CaptureFrameRate),
            timeout.Token);
        await using var encoder = await encoderFactory.StartAsync(
            descriptor,
            timeout.Token);
        var sink = new DiscardingSegmentSink(descriptor);

        var measuredFrames = 0;
        long allocatedBefore = 0;
        await foreach (var frame in capture.ReadFramesAsync(timeout.Token))
        {
            await using (frame)
            {
                await encoder.EncodeAsync(frame, sink, timeout.Token);
            }

            if (measuredFrames == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            }

            measuredFrames++;
            if (measuredFrames >= 61)
            {
                break;
            }
        }

        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        _output.WriteLine(
            "fallback=managed-allocation frames={0} bytes={1} bytesPerFrame={2}",
            measuredFrames - 1,
            allocatedBytes,
            allocatedBytes / Math.Max(1, measuredFrames - 1));
        Assert.True(
            allocatedBytes <= MaximumManagedBytesForSixtyFrames,
            $"Sixty fallback frames allocated {allocatedBytes:N0} managed bytes; " +
            $"the budget is {MaximumManagedBytesForSixtyFrames:N0} bytes.");

        await encoder.CompleteAsync(sink, timeout.Token);
    }

    [RealDynamicWallpaperFact]
    [Trait("Category", "RealDynamicWallpaper")]
    public async Task FallbackTierProfilesCaptureAndEncodingCadence()
    {
        const int warmupFrames = 15;
        const int measuredFrames = 300;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var descriptor = new EncodedWallpaperStreamDescriptor(
            generation: 62,
            "video/mp4; codecs=\"avc1.640028\"",
            DynamicWallpaperRenderProfiles.Fallback.Width,
            DynamicWallpaperRenderProfiles.Fallback.Height,
            DynamicWallpaperRenderProfiles.Fallback.FrameRate);
        var captureFactory = new WindowsGraphicsCaptureFactory();
        var encoderFactory = new MediaFoundationFragmentedMp4EncoderFactory();
        var captureCapability = await captureFactory.ProbeAsync(timeout.Token);
        var encoderCapability = await encoderFactory.ProbeAsync(timeout.Token);
        Assert.True(
            captureCapability.IsAvailable,
            $"WGC unavailable: reason={captureCapability.ReasonCode}");
        Assert.True(
            encoderCapability.IsAvailable,
            $"MF H264 unavailable: reason={encoderCapability.ReasonCode}");

        using var window = new SyntheticCaptureWindow(
            descriptor.Width,
            descriptor.Height,
            timerPeriodMilliseconds: 4);
        await using var capture = await captureFactory.StartAsync(
            new WallpaperWindowCaptureRequest(
                descriptor.Generation,
                CreateCaptureTarget(window.Handle),
                descriptor.Width,
                descriptor.Height,
                DynamicWallpaperRenderProfiles.Fallback.CaptureFrameRate),
            timeout.Token);
        await using var encoder = await encoderFactory.StartAsync(
            descriptor,
            timeout.Token);
        var sink = new DiscardingSegmentSink(descriptor);
        await using var frames = capture.ReadFramesAsync(timeout.Token).GetAsyncEnumerator();

        for (var index = 0; index < warmupFrames; index++)
        {
            for (var captureFrame = 0; captureFrame < 2; captureFrame++)
            {
                Assert.True(await frames.MoveNextAsync());
                await using var warmupFrame = frames.Current;
                if (captureFrame == 1)
                {
                    await encoder.EncodeAsync(warmupFrame, sink, timeout.Token);
                }
            }
        }

        var captureWait = TimeSpan.Zero;
        var encodeTime = TimeSpan.Zero;
        var timestampDeltas = new List<TimeSpan>(measuredFrames - 1);
        var encodeDurations = new List<TimeSpan>(measuredFrames);
        TimeSpan? previousTimestamp = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < measuredFrames; index++)
        {
            for (var captureFrame = 0; captureFrame < 2; captureFrame++)
            {
                var waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                Assert.True(await frames.MoveNextAsync());
                captureWait += System.Diagnostics.Stopwatch.GetElapsedTime(waitStarted);
                await using var frame = frames.Current;
                if (captureFrame == 0)
                {
                    continue;
                }

                if (previousTimestamp is { } previous)
                {
                    timestampDeltas.Add(frame.Timestamp - previous);
                }

                previousTimestamp = frame.Timestamp;
                var encodeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                await encoder.EncodeAsync(frame, sink, timeout.Token);
                var encodedIn = System.Diagnostics.Stopwatch.GetElapsedTime(encodeStarted);
                encodeTime += encodedIn;
                encodeDurations.Add(encodedIn);
            }
        }

        stopwatch.Stop();
        await encoder.CompleteAsync(sink, timeout.Token);
        var effectiveFramesPerSecond = measuredFrames / stopwatch.Elapsed.TotalSeconds;
        var orderedEncodeDurations = encodeDurations.Order().ToArray();
        var encodeP95 = orderedEncodeDurations[
            checked((int)Math.Floor((orderedEncodeDurations.Length - 1) * 0.95))];
        _output.WriteLine(
            "fallback=cadence frames={0} elapsed={1} fps={2:F3} " +
            "captureWaitAvgMs={3:F3} encodeAvgMs={4:F3} encodeP95Ms={5:F3} " +
            "captureTimestampDeltaAvgMs={6:F3} captureTimestampDeltaMinMs={7:F3} " +
            "captureTimestampDeltaMaxMs={8:F3}",
            measuredFrames,
            stopwatch.Elapsed,
            effectiveFramesPerSecond,
            captureWait.TotalMilliseconds / measuredFrames,
            encodeTime.TotalMilliseconds / measuredFrames,
            encodeP95.TotalMilliseconds,
            timestampDeltas.Average(static delta => delta.TotalMilliseconds),
            timestampDeltas.Min(static delta => delta.TotalMilliseconds),
            timestampDeltas.Max(static delta => delta.TotalMilliseconds));
    }

    [RealDynamicWallpaperFact]
    [Trait("Category", "RealDynamicWallpaper")]
    public async Task FallbackCaptureAloneSustainsRequestedCadence()
    {
        const int warmupFrames = 15;
        const int measuredFrames = 300;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var descriptor = new EncodedWallpaperStreamDescriptor(
            generation: 63,
            "video/mp4; codecs=\"avc1.640028\"",
            DynamicWallpaperRenderProfiles.Fallback.Width,
            DynamicWallpaperRenderProfiles.Fallback.Height,
            DynamicWallpaperRenderProfiles.Fallback.FrameRate);
        var captureFactory = new WindowsGraphicsCaptureFactory();
        var captureCapability = await captureFactory.ProbeAsync(timeout.Token);
        Assert.True(
            captureCapability.IsAvailable,
            $"WGC unavailable: reason={captureCapability.ReasonCode}");

        using var window = new SyntheticCaptureWindow(
            descriptor.Width,
            descriptor.Height,
            timerPeriodMilliseconds: 8);
        await using var capture = await captureFactory.StartAsync(
            new WallpaperWindowCaptureRequest(
                descriptor.Generation,
                CreateCaptureTarget(window.Handle),
                descriptor.Width,
                descriptor.Height,
                DynamicWallpaperRenderProfiles.Fallback.CaptureFrameRate),
            timeout.Token);
        await using var frames = capture.ReadFramesAsync(timeout.Token).GetAsyncEnumerator();
        TimeSpan? deliveryWindowStartedAt = null;
        for (var index = 0; index < warmupFrames; index++)
        {
            Assert.True(await frames.MoveNextAsync());
            await using var warmupFrame = frames.Current;
            deliveryWindowStartedAt = warmupFrame.Timestamp;
        }

        var timestampDeltas = new List<TimeSpan>(measuredFrames - 1);
        TimeSpan? previousTimestamp = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < measuredFrames; index++)
        {
            Assert.True(await frames.MoveNextAsync());
            await using var frame = frames.Current;
            if (previousTimestamp is { } previous)
            {
                timestampDeltas.Add(frame.Timestamp - previous);
            }

            previousTimestamp = frame.Timestamp;
        }

        stopwatch.Stop();
        var deliveryWindow = previousTimestamp - deliveryWindowStartedAt;
        Assert.True(deliveryWindow is { } && deliveryWindow > TimeSpan.Zero);
        var captureTimelineFramesPerSecond =
            measuredFrames / deliveryWindow.Value.TotalSeconds;
        var observedWallClockFramesPerSecond =
            measuredFrames / stopwatch.Elapsed.TotalSeconds;
        _output.WriteLine(
            "fallback=capture-only frames={0} wallElapsed={1} wallFps={2:F3} " +
            "captureElapsed={3} captureFps={4:F3} timestampDeltaAvgMs={5:F3} " +
            "timestampDeltaMinMs={6:F3} timestampDeltaMaxMs={7:F3}",
            measuredFrames,
            stopwatch.Elapsed,
            observedWallClockFramesPerSecond,
            deliveryWindow,
            captureTimelineFramesPerSecond,
            timestampDeltas.Average(static delta => delta.TotalMilliseconds),
            timestampDeltas.Min(static delta => delta.TotalMilliseconds),
            timestampDeltas.Max(static delta => delta.TotalMilliseconds));
    }

    [RealDynamicWallpaperFact]
    [Trait("Category", "RealDynamicWallpaper")]
    public async Task RepeatedFragmentFinalizeDoesNotReleaseADeadManagedComShadow()
    {
        const int fragmentCount = 650;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var descriptor = new EncodedWallpaperStreamDescriptor(
            generation: 62,
            "video/mp4; codecs=\"avc1.640028\"",
            DynamicWallpaperRenderProfiles.Fallback.Width,
            DynamicWallpaperRenderProfiles.Fallback.Height,
            DynamicWallpaperRenderProfiles.Fallback.FrameRate);
        var captureFactory = new WindowsGraphicsCaptureFactory();
        var encoderFactory = new MediaFoundationFragmentedMp4EncoderFactory();
        var captureCapability = await captureFactory.ProbeAsync(timeout.Token);
        var encoderCapability = await encoderFactory.ProbeAsync(timeout.Token);
        Assert.True(
            captureCapability.IsAvailable,
            $"WGC unavailable: reason={captureCapability.ReasonCode}");
        Assert.True(
            encoderCapability.IsAvailable,
            $"MF H264 unavailable: reason={encoderCapability.ReasonCode}");

        using var window = new SyntheticCaptureWindow(
            descriptor.Width,
            descriptor.Height);
        await using var capture = await captureFactory.StartAsync(
            new WallpaperWindowCaptureRequest(
                descriptor.Generation,
                CreateCaptureTarget(window.Handle),
                descriptor.Width,
                descriptor.Height,
                descriptor.FrameRate),
            timeout.Token);
        await using var encoder = await encoderFactory.StartAsync(
            descriptor,
            timeout.Token);
        var sink = new DiscardingSegmentSink(descriptor);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        var baselinePrivateBytes = process.PrivateMemorySize64;
        var baselineManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
        var peakPrivateBytes = baselinePrivateBytes;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var finalizedFragments = 0;

        await foreach (var frame in capture.ReadFramesAsync(timeout.Token))
        {
            await using (frame)
            {
                var capturedFrame = Assert.IsType<
                    WindowsGraphicsCaptureFactory.WindowsGraphicsCapturedFrame>(frame);
                var nativeFrame = GetNativeCaptureFrame(capturedFrame);
                var initialFramesPerFragment =
                    MediaFoundationFragmentCadence.GetInitialFrameCount(
                        descriptor.FrameRate);
                var steadyStateFramesPerFragment =
                    MediaFoundationFragmentCadence.GetSteadyStateFrameCount(
                        descriptor.FrameRate,
                        isPrimary: false);
                var replayFrameCount = checked(
                    initialFramesPerFragment +
                    ((fragmentCount - 1) * steadyStateFramesPerFragment));
                for (var sequence = 0; sequence < replayFrameCount; sequence++)
                {
                    // Each wrapper intentionally borrows the one live native frame. Disposing a
                    // replay wrapper would dispose the shared surface owned by capturedFrame.
                    var replay = new WindowsGraphicsCaptureFactory.WindowsGraphicsCapturedFrame(
                        descriptor.Generation,
                        sequence,
                        TimeSpan.FromTicks(sequence),
                        nativeFrame);
                    await encoder.EncodeAsync(replay, sink, timeout.Token);

                    if (MediaFoundationFragmentCadence.IsFragmentBoundary(
                        sequence + 1,
                        initialFramesPerFragment,
                        steadyStateFramesPerFragment))
                    {
                        finalizedFragments++;
                        if (finalizedFragments % 25 == 0)
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            process.Refresh();
                            peakPrivateBytes = Math.Max(
                                peakPrivateBytes,
                                process.PrivateMemorySize64);
                        }
                    }
                }
            }

            break;
        }

        await encoder.CompleteAsync(sink, timeout.Token);
        stopwatch.Stop();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        peakPrivateBytes = Math.Max(peakPrivateBytes, process.PrivateMemorySize64);
        var privateBytesGrowth = peakPrivateBytes - baselinePrivateBytes;
        var managedBytesGrowth =
            GC.GetTotalMemory(forceFullCollection: false) - baselineManagedBytes;
        _output.WriteLine(
            "fallback=fragment-finalize-churn finalizedFragments={0} elapsed={1} " +
            "peakPrivateGrowth={2} managedGrowth={3}",
            finalizedFragments,
            stopwatch.Elapsed,
            privateBytesGrowth,
            managedBytesGrowth);
        Assert.Equal(fragmentCount, finalizedFragments);
        Assert.True(
            privateBytesGrowth <= MaximumSoakPrivateBytesGrowth,
            $"Private memory grew by {privateBytesGrowth:N0} bytes during fragment churn.");
        Assert.True(
            managedBytesGrowth <= MaximumSoakManagedBytesGrowth,
            "Managed memory exceeded the soak budget during fragment churn.");
    }

    [RealDynamicWallpaperFact]
    [Trait("Category", "RealDynamicWallpaper")]
    public Task FallbackTierSustainsEightMinuteCadenceWithinMemoryBound() =>
        RunFallbackSoakAsync(
            TimeSpan.FromMinutes(8),
            generation: 65,
            label: "8m");

    [RealWgcMfSoakFact]
    [Trait("Category", "RealDynamicWallpaperSoak")]
    public Task FallbackTierKeepsMemoryBoundedForThirtyMinutes() =>
        RunFallbackSoakAsync(
            TimeSpan.FromMinutes(30),
            generation: 67,
            label: "30m");

    private async Task RunFallbackSoakAsync(
        TimeSpan soakDuration,
        long generation,
        string label)
    {
        using var timeout = new CancellationTokenSource(soakDuration + TimeSpan.FromMinutes(2));
        var descriptor = new EncodedWallpaperStreamDescriptor(
            generation,
            "video/mp4; codecs=\"avc1.640028\"",
            DynamicWallpaperRenderProfiles.Fallback.Width,
            DynamicWallpaperRenderProfiles.Fallback.Height,
            DynamicWallpaperRenderProfiles.Fallback.FrameRate);
        var captureFactory = new WindowsGraphicsCaptureFactory();
        var encoderFactory = new MediaFoundationFragmentedMp4EncoderFactory();
        var captureCapability = await captureFactory.ProbeAsync(timeout.Token);
        var encoderCapability = await encoderFactory.ProbeAsync(timeout.Token);
        Assert.True(
            captureCapability.IsAvailable,
            $"WGC unavailable: reason={captureCapability.ReasonCode}");
        Assert.True(
            encoderCapability.IsAvailable,
            $"MF H264 unavailable: reason={encoderCapability.ReasonCode}");

        using var window = new SyntheticCaptureWindow(
            descriptor.Width,
            descriptor.Height,
            timerPeriodMilliseconds: 4);
        await using var capture = await captureFactory.StartAsync(
            new WallpaperWindowCaptureRequest(
                descriptor.Generation,
                CreateCaptureTarget(window.Handle),
                descriptor.Width,
                descriptor.Height,
                descriptor.FrameRate),
            timeout.Token);
        await using var encoder = await encoderFactory.StartAsync(
            descriptor,
            timeout.Token);
        var sink = new DiscardingSegmentSink(descriptor);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        var baselinePrivateBytes = process.PrivateMemorySize64;
        var baselineManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
        var peakPrivateBytes = baselinePrivateBytes;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var encodedFrames = 0;
        var initialFramesPerFragment =
            MediaFoundationFragmentCadence.GetInitialFrameCount(descriptor.FrameRate);
        var steadyStateFramesPerFragment =
            MediaFoundationFragmentCadence.GetSteadyStateFrameCount(
                descriptor.FrameRate,
                isPrimary: false);
        var fragmentFinalizeDurations = new List<TimeSpan>();

        await foreach (var frame in capture.ReadFramesAsync(timeout.Token))
        {
            await using (frame)
            {
                var encodeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                await encoder.EncodeAsync(frame, sink, timeout.Token);
                if (MediaFoundationFragmentCadence.IsFragmentBoundary(
                    encodedFrames + 1,
                    initialFramesPerFragment,
                    steadyStateFramesPerFragment))
                {
                    fragmentFinalizeDurations.Add(
                        System.Diagnostics.Stopwatch.GetElapsedTime(encodeStarted));
                }
            }

            encodedFrames++;
            if (MediaFoundationFragmentCadence.IsFragmentBoundary(
                encodedFrames,
                initialFramesPerFragment,
                steadyStateFramesPerFragment))
            {
                process.Refresh();
                peakPrivateBytes = Math.Max(
                    peakPrivateBytes,
                    process.PrivateMemorySize64);
            }

            if (stopwatch.Elapsed >= soakDuration)
            {
                break;
            }
        }

        await encoder.CompleteAsync(sink, timeout.Token);
        stopwatch.Stop();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        peakPrivateBytes = Math.Max(peakPrivateBytes, process.PrivateMemorySize64);
        var finalManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
        var privateBytesGrowth = peakPrivateBytes - baselinePrivateBytes;
        var managedBytesGrowth = finalManagedBytes - baselineManagedBytes;
        var effectiveFramesPerSecond = encodedFrames / stopwatch.Elapsed.TotalSeconds;
        var orderedFinalizeDurations = fragmentFinalizeDurations.Order().ToArray();
        var finalizeP95 = orderedFinalizeDurations[
            checked((int)Math.Floor((orderedFinalizeDurations.Length - 1) * 0.95))];
        var finalizeMaximum = orderedFinalizeDurations[^1];
        _output.WriteLine(
            "fallback={0}-soak frames={1} elapsed={2} fps={3:F3} " +
            "finalizeP95Ms={4:F3} finalizeMaxMs={5:F3} " +
            "peakPrivateGrowth={6} managedGrowth={7}",
            label,
            encodedFrames,
            stopwatch.Elapsed,
            effectiveFramesPerSecond,
            finalizeP95.TotalMilliseconds,
            finalizeMaximum.TotalMilliseconds,
            privateBytesGrowth,
            managedBytesGrowth);

        Assert.True(stopwatch.Elapsed >= soakDuration);
        Assert.True(
            privateBytesGrowth <= MaximumSoakPrivateBytesGrowth,
            $"Private memory grew by {privateBytesGrowth:N0} bytes.");
        Assert.True(
            managedBytesGrowth <= MaximumSoakManagedBytesGrowth,
            $"Managed memory grew by {managedBytesGrowth:N0} bytes.");
    }

    private static async Task<long> EncodeFramesFromFreshSessionAsync(
        WindowsGraphicsCaptureFactory captureFactory,
        nint windowHandle,
        EncodedWallpaperStreamDescriptor descriptor,
        IFragmentedMp4WallpaperEncoder encoder,
        IEncodedWallpaperSegmentSink stream,
        int frameCount,
        CancellationToken cancellationToken)
    {
        await using var capture = await captureFactory.StartAsync(
            new WallpaperWindowCaptureRequest(
                descriptor.Generation,
                CreateCaptureTarget(windowHandle),
                descriptor.Width,
                descriptor.Height,
                descriptor.FrameRate),
            cancellationToken);
        long firstSequence = -1;
        var encodedFrames = 0;
        await foreach (var frame in capture.ReadFramesAsync(cancellationToken))
        {
            await using (frame)
            {
                firstSequence = firstSequence < 0 ? frame.Sequence : firstSequence;
                await encoder.EncodeAsync(frame, stream, cancellationToken);
            }

            encodedFrames++;
            if (encodedFrames >= frameCount)
            {
                break;
            }
        }

        return firstSequence;
    }

    private static async Task<IReadOnlyList<EncodedWallpaperSegment>> ReadAllSegmentsAsync(
        EncodedWallpaperStreamBuffer stream,
        CancellationToken cancellationToken)
    {
        var segments = new List<EncodedWallpaperSegment>();
        while (await stream.ReadAsync(cancellationToken) is { } segment)
        {
            segments.Add(segment);
        }

        return segments;
    }

    private static WallpaperEngineWindowCaptureTarget CreateCaptureTarget(
        nint windowHandle) =>
        new(new FixedCaptureAuthority(windowHandle));

    private static Direct3D11CaptureFrame GetNativeCaptureFrame(
        WindowsGraphicsCaptureFactory.WindowsGraphicsCapturedFrame frame)
    {
        var field = typeof(WindowsGraphicsCaptureFactory.WindowsGraphicsCapturedFrame)
            .GetField("_frame", BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<Direct3D11CaptureFrame>(field?.GetValue(frame));
    }

    private sealed class FixedCaptureAuthority(nint windowHandle)
        : IWallpaperEngineWindowCaptureAuthority
    {
        public ValueTask<nint> RevalidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(windowHandle);
        }
    }

    private static string CreateDecodeExpression(
        EncodedWallpaperStreamDescriptor descriptor,
        EncodedWallpaperSegment initialization,
        IReadOnlyList<EncodedWallpaperSegment> media) =>
        $$"""
        (async () => {
          const mimeType = {{JsonSerializer.Serialize(descriptor.MimeType)}};
          if (!MediaSource.isTypeSupported(mimeType)) {
            return { typeSupported: false, decoded: false, readyState: 0,
              videoWidth: 0, videoHeight: 0, error: "mime-not-supported" };
          }
          const decode = value => Uint8Array.from(atob(value), char => char.charCodeAt(0));
          const initialization = decode({{JsonSerializer.Serialize(
              Convert.ToBase64String(initialization.Payload.Span))}});
          const media = {{JsonSerializer.Serialize(
              media.Select(segment => Convert.ToBase64String(segment.Payload.Span)))}}
            .map(decode);
          const video = document.getElementById("wallpaper");
          const source = new MediaSource();
          const url = URL.createObjectURL(source);
          video.src = url;
          video.muted = true;
          const waitFor = (target, event, errorEvent) => new Promise((resolve, reject) => {
            const onEvent = () => { cleanup(); resolve(); };
            const onError = () => { cleanup(); reject(new Error(errorEvent || event)); };
            const cleanup = () => {
              target.removeEventListener(event, onEvent);
              if (errorEvent) target.removeEventListener(errorEvent, onError);
            };
            target.addEventListener(event, onEvent, { once: true });
            if (errorEvent) target.addEventListener(errorEvent, onError, { once: true });
          });
          const append = (buffer, bytes) => {
            const completed = waitFor(buffer, "updateend", "error");
            buffer.appendBuffer(bytes);
            return completed;
          };
          try {
            await waitFor(source, "sourceopen", "sourceclose");
            const buffer = source.addSourceBuffer(mimeType);
            await append(buffer, initialization);
            for (const segment of media) {
              await append(buffer, segment);
            }
            source.endOfStream();
            await video.play();
            await Promise.race([
              new Promise(resolve => video.requestVideoFrameCallback(() => resolve())),
              new Promise((_, reject) => setTimeout(
                () => reject(new Error("decode-timeout")), 8000))
            ]);
            return { typeSupported: true, decoded: true, readyState: video.readyState,
              videoWidth: video.videoWidth, videoHeight: video.videoHeight, error: "" };
          } catch (error) {
            return { typeSupported: true, decoded: false, readyState: video.readyState,
              videoWidth: video.videoWidth, videoHeight: video.videoHeight,
              error: String(error?.message || error) };
          } finally {
            video.pause();
            video.removeAttribute("src");
            video.load();
            URL.revokeObjectURL(url);
          }
        })()
        """;

    private sealed record DecodeReceipt(
        bool TypeSupported,
        bool Decoded,
        int ReadyState,
        int VideoWidth,
        int VideoHeight,
        string Error);

    private sealed class DiscardingSegmentSink(
        EncodedWallpaperStreamDescriptor descriptor)
        : IEncodedWallpaperSegmentSink
    {
        public EncodedWallpaperStreamDescriptor Descriptor { get; } = descriptor;

        public EncodedWallpaperWriteResult TryWrite(EncodedWallpaperSegment segment)
        {
            ArgumentNullException.ThrowIfNull(segment);
            return EncodedWallpaperWriteResult.Accepted;
        }

        public ValueTask<EncodedWallpaperWriteResult> WriteBatchAsync(
            IReadOnlyList<EncodedWallpaperSegment> segments,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(segments);
            if (segments.Count == 0)
            {
                throw new ArgumentException(
                    "An encoded wallpaper batch must contain at least one segment.",
                    nameof(segments));
            }

            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = TryWrite(segment);
            }

            return ValueTask.FromResult(EncodedWallpaperWriteResult.Accepted);
        }

        public void Complete(Exception? failure = null)
        {
            _ = failure;
        }
    }

    private sealed class SyntheticCaptureWindow : IDisposable
    {
        private const uint ClassOwnDc = 0x0020;
        private const uint WindowStylePopup = 0x80000000;
        private const uint WindowExStyleNoActivate = 0x08000000;
        private const uint WindowExStyleToolWindow = 0x00000080;
        private const int ShowNoActivate = 4;
        private const uint SetWindowPositionNoActivate = 0x0010;
        private const uint SetWindowPositionNoMove = 0x0002;
        private const uint SetWindowPositionNoSize = 0x0001;
        private const uint MessageClose = 0x0010;
        private const uint MessageDestroy = 0x0002;
        private const uint MessagePaint = 0x000F;
        private const uint MessageTimer = 0x0113;

        private readonly ManualResetEventSlim _ready = new();
        private readonly Thread _thread;
        private readonly WindowProcedure _windowProcedure;
        private readonly int _width;
        private readonly int _height;
        private readonly uint _timerPeriodMilliseconds;
        private Exception? _failure;
        private nint _handle;
        private int _disposed;
        private byte _shade;

        internal SyntheticCaptureWindow(
            int width,
            int height,
            uint timerPeriodMilliseconds = 33)
        {
            if (timerPeriodMilliseconds is 0 or > 1000)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timerPeriodMilliseconds));
            }

            _width = width;
            _height = height;
            _timerPeriodMilliseconds = timerPeriodMilliseconds;
            _windowProcedure = WindowProc;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Backdrop synthetic WGC target",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The synthetic WGC target did not start.");
            }

            if (_failure is not null)
            {
                throw new InvalidOperationException(
                    "The synthetic WGC target failed to start.",
                    _failure);
            }
        }

        internal nint Handle => _handle != nint.Zero
            ? _handle
            : throw new ObjectDisposedException(nameof(SyntheticCaptureWindow));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_handle != nint.Zero)
            {
                NativeMethods.PostMessage(_handle, MessageClose, nint.Zero, nint.Zero);
            }

            _thread.Join(TimeSpan.FromSeconds(5));
            _ready.Dispose();
        }

        private void Run()
        {
            var timerResolutionRaised = NativeMethods.TimeBeginPeriod(1) == 0;
            try
            {
                var className = $"BackdropWgcTest-{Guid.NewGuid():N}";
                var windowClass = new WindowClass
                {
                    Size = checked((uint)Marshal.SizeOf<WindowClass>()),
                    Style = ClassOwnDc,
                    WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                    Instance = NativeMethods.GetModuleHandle(null),
                    ClassName = className,
                };
                if (NativeMethods.RegisterClassEx(in windowClass) == 0)
                {
                    throw new InvalidOperationException("RegisterClassEx failed.");
                }

                _handle = NativeMethods.CreateWindowEx(
                    WindowExStyleNoActivate | WindowExStyleToolWindow,
                    className,
                    "Backdrop synthetic capture target",
                    WindowStylePopup,
                    0,
                    0,
                    _width,
                    _height,
                    nint.Zero,
                    nint.Zero,
                    windowClass.Instance,
                    nint.Zero);
                if (_handle == nint.Zero)
                {
                    throw new InvalidOperationException("CreateWindowEx failed.");
                }

                NativeMethods.ShowWindow(_handle, ShowNoActivate);
                NativeMethods.SetWindowPos(
                    _handle,
                    new nint(1),
                    0,
                    0,
                    0,
                    0,
                    SetWindowPositionNoActivate |
                        SetWindowPositionNoMove |
                        SetWindowPositionNoSize);
                NativeMethods.UpdateWindow(_handle);
                NativeMethods.SetTimer(
                    _handle,
                    1,
                    _timerPeriodMilliseconds,
                    nint.Zero);
                _ready.Set();
                while (NativeMethods.GetMessage(out var message, nint.Zero, 0, 0) > 0)
                {
                    NativeMethods.TranslateMessage(in message);
                    NativeMethods.DispatchMessage(in message);
                }
            }
            catch (Exception exception)
            {
                _failure = exception;
                _ready.Set();
            }
            finally
            {
                _handle = nint.Zero;
                if (timerResolutionRaised)
                {
                    _ = NativeMethods.TimeEndPeriod(1);
                }
            }
        }

        private nint WindowProc(nint window, uint message, nint wParam, nint lParam)
        {
            _ = wParam;
            _ = lParam;
            switch (message)
            {
                case MessageTimer:
                    _shade += 7;
                    NativeMethods.InvalidateRect(window, nint.Zero, erase: false);
                    return nint.Zero;
                case MessagePaint:
                    NativeMethods.BeginPaint(window, out var paint);
                    try
                    {
                        NativeMethods.GetClientRect(window, out var rectangle);
                        var color = (uint)(_shade | (80 << 8) | (180 << 16));
                        var brush = NativeMethods.CreateSolidBrush(color);
                        try
                        {
                            _ = NativeMethods.FillRect(
                                paint.DeviceContext,
                                in rectangle,
                                brush);
                        }
                        finally
                        {
                            NativeMethods.DeleteObject(brush);
                        }
                    }
                    finally
                    {
                        NativeMethods.EndPaint(window, in paint);
                    }

                    return nint.Zero;
                case MessageDestroy:
                    NativeMethods.KillTimer(window, 1);
                    NativeMethods.PostQuitMessage(0);
                    return nint.Zero;
                default:
                    return NativeMethods.DefWindowProc(window, message, wParam, lParam);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint WindowProcedure(
            nint window,
            uint message,
            nint wParam,
            nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            internal uint Size;
            internal uint Style;
            internal nint WindowProcedure;
            internal int ClassExtra;
            internal int WindowExtra;
            internal nint Instance;
            internal nint Icon;
            internal nint Cursor;
            internal nint Background;
            [MarshalAs(UnmanagedType.LPWStr)]
            internal string? MenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            internal string? ClassName;
            internal nint SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            internal nint Window;
            internal uint Value;
            internal nuint WParam;
            internal nint LParam;
            internal uint Time;
            internal Point Point;
            internal uint Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rectangle
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Paint
        {
            internal nint DeviceContext;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool Erase;
            internal Rectangle PaintRectangle;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool Restore;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool IncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            internal byte[] Reserved;
        }

        private static class NativeMethods
        {
            [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", ExactSpelling = true)]
            internal static extern uint TimeBeginPeriod(uint periodMilliseconds);

            [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", ExactSpelling = true)]
            internal static extern uint TimeEndPeriod(uint periodMilliseconds);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            internal static extern nint GetModuleHandle(string? moduleName);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            internal static extern ushort RegisterClassEx(in WindowClass windowClass);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            internal static extern nint CreateWindowEx(
                uint extendedStyle,
                string className,
                string windowName,
                uint style,
                int x,
                int y,
                int width,
                int height,
                nint parent,
                nint menu,
                nint instance,
                nint parameter);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool ShowWindow(nint window, int command);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool UpdateWindow(nint window);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetWindowPos(
                nint window,
                nint insertAfter,
                int x,
                int y,
                int width,
                int height,
                uint flags);

            [DllImport("user32.dll")]
            internal static extern nuint SetTimer(
                nint window,
                nuint timerId,
                uint interval,
                nint callback);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool KillTimer(nint window, nuint timerId);

            [DllImport("user32.dll")]
            internal static extern int GetMessage(
                out Message message,
                nint window,
                uint minimum,
                uint maximum);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool TranslateMessage(in Message message);

            [DllImport("user32.dll")]
            internal static extern nint DispatchMessage(in Message message);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool PostMessage(
                nint window,
                uint message,
                nint wParam,
                nint lParam);

            [DllImport("user32.dll")]
            internal static extern void PostQuitMessage(int exitCode);

            [DllImport("user32.dll")]
            internal static extern nint DefWindowProc(
                nint window,
                uint message,
                nint wParam,
                nint lParam);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool InvalidateRect(
                nint window,
                nint rectangle,
                [MarshalAs(UnmanagedType.Bool)] bool erase);

            [DllImport("user32.dll")]
            internal static extern nint BeginPaint(nint window, out Paint paint);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool EndPaint(nint window, in Paint paint);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetClientRect(nint window, out Rectangle rectangle);

            [DllImport("gdi32.dll")]
            internal static extern nint CreateSolidBrush(uint color);

            [DllImport("user32.dll")]
            internal static extern int FillRect(
                nint deviceContext,
                in Rectangle rectangle,
                nint brush);

            [DllImport("gdi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DeleteObject(nint value);
        }
    }
}

internal sealed class RealWgcMfSoakFactAttribute : FactAttribute
{
    private const string OptInVariable = "BACKDROP_RUN_REAL_WGC_MF_SOAK";

    public RealWgcMfSoakFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {OptInVariable}=1 to run the 30-minute WGC/MF soak gate.";
        }
    }
}
