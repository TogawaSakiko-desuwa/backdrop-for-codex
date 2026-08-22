using BackdropForCodex.Core.Media;

namespace BackdropForCodex.Core.Dynamic;

public enum DynamicWallpaperCapabilityReasonCode
{
    None = 0,
    UnsupportedOperatingSystem,
    CaptureApiUnavailable,
    CaptureTargetUnavailable,
    CapturedFrameSizeMismatch,
    HardwareEncoderUnavailable,
    CodecUnavailable,
    GraphicsDeviceUnavailable,
    EncodingFailed,
    PrimaryRenderTierUnavailable,
    FallbackRenderTierUnavailable,
    SustainedStreamBackpressure,
    DynamicStartupTimedOut,
    WallpaperEngineUnavailable,
    WallpaperEngineNotRunning,
    WallpaperEngineRecoveryUnavailable,
    WallpaperEngineWindowUnavailable,
    WallpaperEngineAudioIsolationUnavailable,
    WallpaperEnginePlacementUnavailable,
    InitialAudioSilenceNotProven,
}

public sealed class DynamicWallpaperUnavailableException : Exception
{
    public DynamicWallpaperUnavailableException(
        DynamicWallpaperCapabilityReasonCode reasonCode)
        : base(CreateMessage(reasonCode))
    {
        if (!Enum.IsDefined(reasonCode) ||
            reasonCode == DynamicWallpaperCapabilityReasonCode.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reasonCode));
        }

        ReasonCode = reasonCode;
    }

    public DynamicWallpaperUnavailableException(
        DynamicWallpaperCapabilityReasonCode reasonCode,
        Exception innerException)
        : base(CreateMessage(reasonCode), innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
        if (!Enum.IsDefined(reasonCode) ||
            reasonCode == DynamicWallpaperCapabilityReasonCode.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reasonCode));
        }

        ReasonCode = reasonCode;
    }

    public DynamicWallpaperCapabilityReasonCode ReasonCode { get; }

    private static string CreateMessage(
        DynamicWallpaperCapabilityReasonCode reasonCode) =>
        reasonCode switch
        {
            DynamicWallpaperCapabilityReasonCode.UnsupportedOperatingSystem =>
                "Dynamic wallpaper rendering requires Windows 11 x64.",
            DynamicWallpaperCapabilityReasonCode.CaptureApiUnavailable =>
                "Windows Graphics Capture is unavailable.",
            DynamicWallpaperCapabilityReasonCode.CaptureTargetUnavailable =>
                "The verified wallpaper window is no longer available for capture.",
            DynamicWallpaperCapabilityReasonCode.CapturedFrameSizeMismatch =>
                "The captured wallpaper window does not match the active render tier.",
            DynamicWallpaperCapabilityReasonCode.HardwareEncoderUnavailable =>
                "A hardware H.264 encoder is unavailable.",
            DynamicWallpaperCapabilityReasonCode.CodecUnavailable =>
                "The required H.264 fragmented MP4 codec is unavailable.",
            DynamicWallpaperCapabilityReasonCode.GraphicsDeviceUnavailable =>
                "A compatible Direct3D graphics device is unavailable.",
            DynamicWallpaperCapabilityReasonCode.EncodingFailed =>
                "The dynamic wallpaper encoder failed.",
            DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable =>
                "The 1080p dynamic wallpaper tier could not sustain 30 frames per second.",
            DynamicWallpaperCapabilityReasonCode.FallbackRenderTierUnavailable =>
                "The 720p dynamic wallpaper tier could not sustain 15 frames per second.",
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure =>
                "The dynamic wallpaper page could not accept encoded video for five seconds.",
            DynamicWallpaperCapabilityReasonCode.DynamicStartupTimedOut =>
                "The dynamic wallpaper did not produce a verified startup key frame in time.",
            DynamicWallpaperCapabilityReasonCode.WallpaperEngineUnavailable =>
                "Wallpaper Engine is not available from a validated local Steam installation.",
            DynamicWallpaperCapabilityReasonCode.WallpaperEngineNotRunning =>
                "Wallpaper Engine must already be running before a window-level command is sent.",
            DynamicWallpaperCapabilityReasonCode.WallpaperEngineRecoveryUnavailable =>
                "A previously owned Wallpaper Engine pop-out could not be safely recovered.",
            DynamicWallpaperCapabilityReasonCode.WallpaperEngineWindowUnavailable =>
                "A uniquely owned Wallpaper Engine pop-out could not be verified.",
            DynamicWallpaperCapabilityReasonCode.WallpaperEngineAudioIsolationUnavailable =>
                "The Wallpaper Engine pop-out could not be proven silent and isolated.",
            DynamicWallpaperCapabilityReasonCode.WallpaperEnginePlacementUnavailable =>
                "The Wallpaper Engine pop-out could not be confined without interfering with the desktop.",
            DynamicWallpaperCapabilityReasonCode.InitialAudioSilenceNotProven =>
                "The Wallpaper Engine pop-out cannot be proven silent before its window is opened.",
            _ => throw new ArgumentOutOfRangeException(nameof(reasonCode)),
        };
}

public sealed record DynamicWallpaperCapability
{
    public DynamicWallpaperCapability(
        bool isAvailable,
        DynamicWallpaperCapabilityReasonCode reasonCode)
    {
        if (!Enum.IsDefined(reasonCode) ||
            (isAvailable && reasonCode != DynamicWallpaperCapabilityReasonCode.None) ||
            (!isAvailable && reasonCode == DynamicWallpaperCapabilityReasonCode.None))
        {
            throw new ArgumentException(
                "Availability and the capability reason code are inconsistent.",
                nameof(reasonCode));
        }

        IsAvailable = isAvailable;
        ReasonCode = reasonCode;
    }

    public bool IsAvailable { get; }

    public DynamicWallpaperCapabilityReasonCode ReasonCode { get; }

    public static DynamicWallpaperCapability Available() =>
        new(isAvailable: true, DynamicWallpaperCapabilityReasonCode.None);

    public static DynamicWallpaperCapability Unavailable(
        DynamicWallpaperCapabilityReasonCode reasonCode) =>
        new(isAvailable: false, reasonCode);
}

public sealed record WallpaperWindowCaptureRequest
{
    public WallpaperWindowCaptureRequest(
        long generation,
        WallpaperEngineWindowCaptureTarget captureTarget,
        int width,
        int height,
        double frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentNullException.ThrowIfNull(captureTarget);

        if (width is <= 0 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height is <= 0 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (!double.IsFinite(frameRate) || frameRate is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        Generation = generation;
        CaptureTarget = captureTarget;
        Width = width;
        Height = height;
        FrameRate = frameRate;
    }

    public long Generation { get; }

    public WallpaperEngineWindowCaptureTarget CaptureTarget { get; }

    public int Width { get; }

    public int Height { get; }

    public double FrameRate { get; }
}

/// <summary>
/// Opaque lifetime for one captured GPU frame. The capture and encoder implementations agree on
/// the concrete surface type without exposing a transferable native handle to other subsystems.
/// </summary>
public interface IWallpaperCapturedFrame : IAsyncDisposable
{
    long Generation { get; }

    long Sequence { get; }

    TimeSpan Timestamp { get; }

    int Width { get; }

    int Height { get; }
}

public interface IWallpaperWindowCaptureSession : IAsyncDisposable
{
    long Generation { get; }

    IAsyncEnumerable<IWallpaperCapturedFrame> ReadFramesAsync(
        CancellationToken cancellationToken = default);
}

public interface IWallpaperWindowCaptureFactory
{
    ValueTask<DynamicWallpaperCapability> ProbeAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IWallpaperWindowCaptureSession> StartAsync(
        WallpaperWindowCaptureRequest request,
        CancellationToken cancellationToken = default);
}

public interface IEncodedWallpaperSegmentSink
{
    EncodedWallpaperStreamDescriptor Descriptor { get; }

    EncodedWallpaperWriteResult TryWrite(EncodedWallpaperSegment segment);

    /// <summary>
    /// Writes every segment emitted by one encoder flush in order without silently dropping a
    /// prefix or suffix. Implementations must make their backpressure policy explicit so a new
    /// sink cannot accidentally regress to independent best-effort writes.
    /// </summary>
    ValueTask<EncodedWallpaperWriteResult> WriteBatchAsync(
        IReadOnlyList<EncodedWallpaperSegment> segments,
        CancellationToken cancellationToken = default);

    void Complete(Exception? failure = null);
}

public interface IFragmentedMp4WallpaperEncoder : IAsyncDisposable
{
    EncodedWallpaperStreamDescriptor Descriptor { get; }

    ValueTask EncodeAsync(
        IWallpaperCapturedFrame frame,
        IEncodedWallpaperSegmentSink output,
        CancellationToken cancellationToken = default);

    ValueTask CompleteAsync(
        IEncodedWallpaperSegmentSink output,
        CancellationToken cancellationToken = default);
}

internal interface ICapturePauseBoundaryFragmentedMp4WallpaperEncoder
{
    ValueTask DiscardPendingFragmentForCapturePauseAsync(
        CancellationToken cancellationToken = default);
}

public interface IFragmentedMp4WallpaperEncoderFactory
{
    ValueTask<DynamicWallpaperCapability> ProbeAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IFragmentedMp4WallpaperEncoder> StartAsync(
        EncodedWallpaperStreamDescriptor descriptor,
        CancellationToken cancellationToken = default);
}
