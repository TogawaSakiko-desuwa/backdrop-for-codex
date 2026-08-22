using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;
using Windows.Graphics.DirectX.Direct3D11;

namespace BackdropForCodex.Core.Dynamic;

/// <summary>
/// Creates hardware-preferred H.264 encoders backed by the Windows Media Foundation platform.
/// </summary>
public sealed class MediaFoundationFragmentedMp4EncoderFactory :
    IFragmentedMp4WallpaperEncoderFactory
{
    public ValueTask<DynamicWallpaperCapability> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return ValueTask.FromResult(DynamicWallpaperCapability.Unavailable(
                DynamicWallpaperCapabilityReasonCode.UnsupportedOperatingSystem));
        }

        try
        {
            using var lifetime = MediaFoundationLifetime.Acquire();
            return ValueTask.FromResult(MediaFoundationHardwareProbe.HasHardwareH264Encoder()
                ? DynamicWallpaperCapability.Available()
                : DynamicWallpaperCapability.Unavailable(
                    DynamicWallpaperCapabilityReasonCode.HardwareEncoderUnavailable));
        }
        catch (Exception exception) when (
            exception is COMException or
                DllNotFoundException or
                EntryPointNotFoundException or
                PlatformNotSupportedException)
        {
            return ValueTask.FromResult(DynamicWallpaperCapability.Unavailable(
                DynamicWallpaperCapabilityReasonCode.CodecUnavailable));
        }
    }

    public async ValueTask<IFragmentedMp4WallpaperEncoder> StartAsync(
        EncodedWallpaperStreamDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedProfile(descriptor))
        {
            throw new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.CodecUnavailable);
        }

        var capability = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!capability.IsAvailable)
        {
            throw new DynamicWallpaperUnavailableException(capability.ReasonCode);
        }

        return new MediaFoundationFragmentedMp4Encoder(descriptor);
    }

    internal static bool IsSupportedProfile(EncodedWallpaperStreamDescriptor descriptor) =>
        descriptor.MimeType.Contains("avc1.640028", StringComparison.OrdinalIgnoreCase) &&
        DynamicWallpaperRenderProfiles.InPreferenceOrder.Any(
            profile => Matches(descriptor, profile));

    private static bool Matches(
        EncodedWallpaperStreamDescriptor descriptor,
        DynamicWallpaperRenderProfile profile) =>
        descriptor.Width == profile.Width &&
        descriptor.Height == profile.Height &&
        Math.Abs(descriptor.FrameRate - profile.FrameRate) < 0.001;

    private static class MediaFoundationHardwareProbe
    {
        private const uint HardwareAndSortedTransforms = 0x44;

        private static readonly Guid VideoEncoderCategory =
            new("F79EAC7D-E545-4387-BDEE-D647D7BDE42A");

        private static readonly Guid VideoMajorType =
            new("73646976-0000-0010-8000-00AA00389B71");

        private static readonly Guid Nv12Subtype =
            new("3231564E-0000-0010-8000-00AA00389B71");

        private static readonly Guid H264Subtype =
            new("34363248-0000-0010-8000-00AA00389B71");

        public static bool HasHardwareH264Encoder()
        {
            var input = new MftRegisterTypeInfo(VideoMajorType, Nv12Subtype);
            var output = new MftRegisterTypeInfo(VideoMajorType, H264Subtype);
            nint activations = nint.Zero;
            uint count = 0;
            try
            {
                var category = VideoEncoderCategory;
                Marshal.ThrowExceptionForHR(NativeMethods.MFTEnumEx(
                    ref category,
                    HardwareAndSortedTransforms,
                    in input,
                    in output,
                    out activations,
                    out count));
                return count > 0;
            }
            finally
            {
                if (activations != nint.Zero)
                {
                    for (var index = 0u; index < count; index++)
                    {
                        var activation = Marshal.ReadIntPtr(
                            activations,
                            checked((int)index * IntPtr.Size));
                        if (activation != nint.Zero)
                        {
                            Marshal.Release(activation);
                        }
                    }

                    Marshal.FreeCoTaskMem(activations);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct MftRegisterTypeInfo
        {
            public MftRegisterTypeInfo(Guid majorType, Guid subtype)
            {
                MajorType = majorType;
                Subtype = subtype;
            }

            public Guid MajorType { get; }

            public Guid Subtype { get; }
        }

        private static class NativeMethods
        {
            [DllImport("mfplat.dll", ExactSpelling = true)]
            internal static extern int MFTEnumEx(
                ref Guid category,
                uint flags,
                in MftRegisterTypeInfo inputType,
                in MftRegisterTypeInfo outputType,
                out nint activations,
                out uint count);
        }
    }

    private sealed class MediaFoundationLifetime : IDisposable
    {
        private const uint MediaFoundationVersion = 0x00020070;
        private static readonly object Sync = new();
        private static int _referenceCount;
        private int _disposed;

        private MediaFoundationLifetime()
        {
        }

        public static MediaFoundationLifetime Acquire()
        {
            lock (Sync)
            {
                if (_referenceCount == 0)
                {
                    Marshal.ThrowExceptionForHR(NativeMethods.MFStartup(
                        MediaFoundationVersion,
                        flags: 0));
                }

                checked
                {
                    _referenceCount++;
                }
            }

            return new MediaFoundationLifetime();
        }

        public void Dispose()
        {
            lock (Sync)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                _referenceCount--;
                try
                {
                    if (_referenceCount == 0)
                    {
                        Marshal.ThrowExceptionForHR(NativeMethods.MFShutdown());
                    }
                }
                catch
                {
                    _referenceCount++;
                    throw;
                }

                Volatile.Write(ref _disposed, 1);
            }
        }

        private static class NativeMethods
        {
            [DllImport("mfplat.dll", ExactSpelling = true)]
            internal static extern int MFStartup(uint version, uint flags);

            [DllImport("mfplat.dll", ExactSpelling = true)]
            internal static extern int MFShutdown();
        }
    }

    private sealed class MediaFoundationFragmentedMp4Encoder :
        IFragmentedMp4WallpaperEncoder,
        ICapturePauseBoundaryFragmentedMp4WallpaperEncoder
    {
        private const int HighProfile = 100;
        private const int Level40 = 40;
        private const int MaximumFinalizedChunkBytes =
            EncodedWallpaperStreamBuffer.MaximumBufferedBytes + (1024 * 1024);

        private MediaFoundationLifetime? _lifetime;
        // MFByteStream(Stream) creates a managed COM proxy. Keeping it encoder-scoped
        // prevents one proxy from being destroyed while native fragment work retires.
        private MediaFoundationChunkOutput? _chunkOutput;
        private readonly MediaFoundationFragmentedMp4Segmenter _segmenter;
        private readonly GenerationScopedWallpaperFrameOrder _frameOrder;
        private readonly DynamicWallpaperBackpressureMonitor _backpressureMonitor = new();
        private readonly MediaFoundationPrimaryThroughputGate _primaryThroughputGate = new();
        private readonly MediaFoundationFragmentBoundaryState _fragmentBoundary;
        private readonly MediaFoundationEncoderOperationGate _operationGate = new();
        private readonly long _sampleDuration;
        private readonly bool _isPrimary;
        private MediaFoundationChunkWriter? _chunk;
        private bool _completed;

        public MediaFoundationFragmentedMp4Encoder(
            EncodedWallpaperStreamDescriptor descriptor)
        {
            Descriptor = descriptor;
            _lifetime = MediaFoundationLifetime.Acquire();
            _chunkOutput = new MediaFoundationChunkOutput(MaximumFinalizedChunkBytes);
            _segmenter = new MediaFoundationFragmentedMp4Segmenter(descriptor.Generation);
            _frameOrder = new GenerationScopedWallpaperFrameOrder(descriptor.Generation);
            _isPrimary = Matches(descriptor, DynamicWallpaperRenderProfiles.Primary);
            _fragmentBoundary = new MediaFoundationFragmentBoundaryState(
                descriptor.FrameRate,
                _isPrimary);
            _sampleDuration = checked((long)Math.Round(
                TimeSpan.TicksPerSecond / descriptor.FrameRate));
        }

        public EncodedWallpaperStreamDescriptor Descriptor { get; }

        public async ValueTask DiscardPendingFragmentForCapturePauseAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(
                _operationGate.IsDisposeStarted,
                this);
            await _operationGate.WaitAsync(this, cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(
                    _operationGate.IsDisposeStarted,
                    this);
                if (_completed)
                {
                    throw new InvalidOperationException(
                        "The fragmented MP4 encoder has already completed.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                DiscardPendingFragmentForCapturePause();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DynamicWallpaperUnavailableException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new DynamicWallpaperUnavailableException(
                    DynamicWallpaperCapabilityReasonCode.EncodingFailed,
                    exception);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async ValueTask EncodeAsync(
            IWallpaperCapturedFrame frame,
            IEncodedWallpaperSegmentSink output,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(frame);
            ArgumentNullException.ThrowIfNull(output);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(
                _operationGate.IsDisposeStarted,
                this);
            ValidateStream(output);
            if (frame is not WindowsGraphicsCaptureFactory.WindowsGraphicsCapturedFrame windowsFrame ||
                frame.Generation != Descriptor.Generation ||
                frame.Width != Descriptor.Width ||
                frame.Height != Descriptor.Height)
            {
                throw new DynamicWallpaperUnavailableException(
                    DynamicWallpaperCapabilityReasonCode.EncodingFailed);
            }

            await _operationGate.WaitAsync(this, cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(
                    _operationGate.IsDisposeStarted,
                    this);
                if (_completed)
                {
                    throw new InvalidOperationException(
                        "The fragmented MP4 encoder has already completed.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                _frameOrder.Accept(frame);
                if (_isPrimary)
                {
                    _primaryThroughputGate.BeginFrame(GetMonotonicNow());
                }
                using var surfaceFrame =
                    MediaFoundationDxgiSurfaceFrame.Create(windowsFrame.Surface);
                _chunk ??= CreateChunkWriter(surfaceFrame.Device);
                _chunk.WriteFrame(
                    surfaceFrame.Buffer,
                    checked(_sampleDuration * _fragmentBoundary.PendingFrameCount),
                    _sampleDuration);
                var fragmentBoundaryReached = _fragmentBoundary.RecordFrame();
                if (_isPrimary)
                {
                    _primaryThroughputGate.CompleteFrame(GetMonotonicNow());
                }
                if (fragmentBoundaryReached)
                {
                    await FlushChunkAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DynamicWallpaperUnavailableException exception)
            {
                output.Complete(exception);
                throw;
            }
            catch (Exception exception)
            {
                var unavailable = new DynamicWallpaperUnavailableException(
                    DynamicWallpaperCapabilityReasonCode.EncodingFailed,
                    exception);
                output.Complete(unavailable);
                throw unavailable;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async ValueTask CompleteAsync(
            IEncodedWallpaperSegmentSink output,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(output);
            ValidateStream(output);
            await _operationGate.WaitAsync(this, cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(
                    _operationGate.IsDisposeStarted,
                    this);
                if (_completed)
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var flushPendingFragment = false;
                _fragmentBoundary.FlushPendingFragmentForCompletion(
                    () => flushPendingFragment = true);
                if (flushPendingFragment)
                {
                    await FlushChunkAsync(output, cancellationToken).ConfigureAwait(false);
                }

                _completed = true;
                output.Complete();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DynamicWallpaperUnavailableException exception)
            {
                output.Complete(exception);
                throw;
            }
            catch (Exception exception)
            {
                var unavailable = new DynamicWallpaperUnavailableException(
                    DynamicWallpaperCapabilityReasonCode.EncodingFailed,
                    exception);
                output.Complete(unavailable);
                throw unavailable;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_operationGate.TryBeginDispose())
            {
                return;
            }

            await _operationGate.WaitForDisposalAsync().ConfigureAwait(false);
            try
            {
                if (_operationGate.IsDisposeCompleted)
                {
                    return;
                }

                if (_chunk is not null)
                {
                    _chunk.Dispose();
                    _chunk = null;
                }

                if (_chunkOutput is not null)
                {
                    _chunkOutput.Dispose();
                    _chunkOutput = null;
                }

                if (_lifetime is not null)
                {
                    _lifetime.Dispose();
                    _lifetime = null;
                }

                _operationGate.MarkDisposeCompleted();
            }
            finally
            {
                // SemaphoreSlim is deliberately kept alive for the encoder object's lifetime.
                // A caller may have passed its pre-disposal check and still be queued; releasing
                // that caller against a disposed semaphore would mask the intended disposed error.
                _operationGate.Release();
            }
        }

        private MediaFoundationChunkWriter CreateChunkWriter(ComObject device) =>
            new(
                Descriptor.Width,
                Descriptor.Height,
                checked((int)Math.Round(Descriptor.FrameRate)),
                Matches(Descriptor, DynamicWallpaperRenderProfiles.Primary)
                    ? 8_000_000
                    : 4_000_000,
                MaximumFinalizedChunkBytes,
                HighProfile,
                Level40,
                device,
                _chunkOutput ?? throw new ObjectDisposedException(GetType().Name));

        private async ValueTask FlushChunkAsync(
            IEncodedWallpaperSegmentSink output,
            CancellationToken cancellationToken)
        {
            var chunk = _chunk ?? throw new InvalidOperationException(
                "No Media Foundation chunk is active.");
            var completedFrameCount = _fragmentBoundary.PendingFrameCount;
            _chunk = null;
            byte[] finalized;
            try
            {
                finalized = chunk.FinalizeAndTakeBytes();
            }
            finally
            {
                chunk.Dispose();
            }

            var segments = _segmenter.Split(finalized);
            try
            {
                var firstMedia = segments.FirstOrDefault(
                    segment => segment.Kind == EncodedWallpaperSegmentKind.Media);
                _fragmentBoundary.ValidateFragmentForPublication(
                    firstMedia?.IsKeyFrame == true);
                var publicationResult = await PublishSegmentsAsync(
                    segments,
                    output,
                    cancellationToken).ConfigureAwait(false);
                if (!_segmenter.TryCommitPendingBatch(publicationResult))
                {
                    throw new DynamicWallpaperUnavailableException(
                        DynamicWallpaperCapabilityReasonCode.EncodingFailed);
                }
            }
            catch
            {
                _segmenter.DiscardPendingBatch();
                throw;
            }

            _fragmentBoundary.MarkFragmentPublished();
        }

        private void DiscardPendingFragmentForCapturePause()
        {
            var chunk = _chunk;
            _chunk = null;
            try
            {
                chunk?.Dispose();
            }
            finally
            {
                _segmenter.DiscardPendingBatch();
                _fragmentBoundary.ResetForCapturePause();
                _primaryThroughputGate.ResetForCapturePause();
                _backpressureMonitor.ResetForCapturePause();
                _chunkOutput!.Reset();
            }
        }

        private async ValueTask<EncodedWallpaperWriteResult> PublishSegmentsAsync(
            IReadOnlyList<EncodedWallpaperSegment> segments,
            IEncodedWallpaperSegmentSink output,
            CancellationToken cancellationToken)
        {
            var result = await output
                .WriteBatchAsync(segments, cancellationToken)
                .ConfigureAwait(false);
            if (_backpressureMonitor.Observe(result, GetMonotonicNow()))
            {
                throw new DynamicWallpaperUnavailableException(
                    DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
            }

            return result;
        }

        private static TimeSpan GetMonotonicNow() =>
            System.Diagnostics.Stopwatch.GetElapsedTime(
                startingTimestamp: 0,
                endingTimestamp: System.Diagnostics.Stopwatch.GetTimestamp());

        private void ValidateStream(IEncodedWallpaperSegmentSink output)
        {
            if (output.Descriptor != Descriptor)
            {
                throw new ArgumentException(
                    "The encoded segment sink has a different stream descriptor.",
                    nameof(output));
            }
        }

        private sealed class MediaFoundationChunkWriter : IDisposable
        {
            private readonly MediaFoundationChunkOutput _output;
            private readonly IMFMediaSink _sink;
            private readonly IMFSinkWriter _writer;
            private readonly IMFDXGIDeviceManager _deviceManager;
            private readonly int _pixelByteCount;
            private readonly object _disposeSync = new();
            private bool _finalized;
            private bool _writerReleased;
            private bool _sinkShutdown;
            private bool _sinkDisposed;
            private bool _deviceManagerDisposed;
            private int _disposed;

            internal MediaFoundationChunkWriter(
                int width,
                int height,
                int frameRate,
                int bitrate,
                int maximumBytes,
                int profile,
                int level,
                ComObject device,
                MediaFoundationChunkOutput output)
            {
                ArgumentNullException.ThrowIfNull(device);
                ArgumentNullException.ThrowIfNull(output);
                _pixelByteCount = checked(width * height * 4);
                if (maximumBytes != output.MaximumBytes)
                {
                    throw new ArgumentException(
                        "The chunk output has a different memory bound.",
                        nameof(output));
                }

                _output = output;
                _output.Reset();
                using var outputType = CreateMediaType(
                    VideoFormatGuids.H264,
                    width,
                    height,
                    frameRate);
                outputType.Set(MediaTypeAttributeKeys.AvgBitrate, checked((uint)bitrate))
                    .CheckError();
                outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, checked((uint)profile))
                    .CheckError();
                outputType.Set(MediaTypeAttributeKeys.Mpeg2Level, checked((uint)level))
                    .CheckError();
                MediaFactory.MFCreateFMPEG4MediaSink(
                    _output.ByteStream,
                    outputType,
                    null!,
                    out _sink).CheckError();
                _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
                _deviceManager.ResetDevice(device).CheckError();
                using var attributes = MediaFactory.MFCreateAttributes(4);
                attributes.Set(
                    SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms,
                    1u).CheckError();
                attributes.Set(SinkWriterAttributeKeys.LowLatency, 1u).CheckError();
                attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u).CheckError();
                attributes.Set(
                    SinkWriterAttributeKeys.D3DManager,
                    _deviceManager).CheckError();
                _writer = MediaFactory.MFCreateSinkWriterFromMediaSink(
                    _sink,
                    attributes);
                using var inputType = CreateMediaType(
                    VideoFormatGuids.Argb32,
                    width,
                    height,
                    frameRate);
                _writer.SetInputMediaType(0, inputType, null!);
                _writer.BeginWriting();
            }

            internal void WriteFrame(
                IMFMediaBuffer surfaceBuffer,
                long sampleTime,
                long sampleDuration)
            {
                ArgumentNullException.ThrowIfNull(surfaceBuffer);
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed) != 0,
                    this);
                if (_finalized)
                {
                    throw new InvalidOperationException(
                        "The Media Foundation chunk cannot accept this frame.");
                }

                surfaceBuffer.CurrentLength = _pixelByteCount;
                using var sample = MediaFactory.MFCreateSample();
                sample.AddBuffer(surfaceBuffer);
                sample.SampleTime = sampleTime;
                sample.SampleDuration = sampleDuration;
                _writer.WriteSample(0, sample);
            }

            internal byte[] FinalizeAndTakeBytes()
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed) != 0,
                    this);
                if (_finalized)
                {
                    throw new InvalidOperationException(
                        "The Media Foundation chunk has already been finalized.");
                }

                _writer.Finalize();
                ReleaseWriter();
                _sink.Shutdown();
                _sinkShutdown = true;
                _finalized = true;
                return _output.ToArray();
            }

            public void Dispose()
            {
                lock (_disposeSync)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }

                    ReleaseWriter();
                    if (!_finalized && !_sinkShutdown)
                    {
                        _sink.Shutdown();
                        _sinkShutdown = true;
                    }

                    if (!_sinkDisposed)
                    {
                        _sink.Dispose();
                        _sinkDisposed = true;
                    }

                    if (!_deviceManagerDisposed)
                    {
                        _deviceManager.Dispose();
                        _deviceManagerDisposed = true;
                    }

                    Volatile.Write(ref _disposed, 1);
                }
            }

            private void ReleaseWriter()
            {
                if (_writerReleased)
                {
                    return;
                }

                // MF requires the sink writer to be released before its media sink is shut down.
                _writer.Dispose();
                _writerReleased = true;
            }

            private static IMFMediaType CreateMediaType(
                Guid subtype,
                int width,
                int height,
                int frameRate)
            {
                var type = MediaFactory.MFCreateMediaType();
                type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video)
                    .CheckError();
                type.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
                type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
                MediaFactory.MFSetAttributeSize(
                    type,
                    MediaTypeAttributeKeys.FrameSize,
                    checked((uint)width),
                    checked((uint)height)).CheckError();
                MediaFactory.MFSetAttributeRatio(
                    type,
                    MediaTypeAttributeKeys.FrameRate,
                    checked((uint)frameRate),
                    1).CheckError();
                MediaFactory.MFSetAttributeRatio(
                    type,
                    MediaTypeAttributeKeys.PixelAspectRatio,
                    1,
                    1).CheckError();
                return type;
            }

        }

        private sealed class MediaFoundationChunkOutput : IDisposable
        {
            private readonly BoundedMemoryStream _stream;
            private readonly MFByteStream _byteStream;
            private readonly object _disposeSync = new();
            private bool _byteStreamDisposed;
            private bool _streamDisposed;
            private int _disposed;

            internal MediaFoundationChunkOutput(int maximumBytes)
            {
                MaximumBytes = maximumBytes;
                _stream = new BoundedMemoryStream(maximumBytes);
                _byteStream = new MFByteStream(_stream, disposeStream: false);
            }

            internal int MaximumBytes { get; }

            internal MFByteStream ByteStream
            {
                get
                {
                    ObjectDisposedException.ThrowIf(
                        Volatile.Read(ref _disposed) != 0,
                        this);
                    return _byteStream;
                }
            }

            internal void Reset()
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed) != 0,
                    this);
                _stream.SetLength(0);
                _stream.Position = 0;
                // IMFByteStream also tracks its current position independently.
                _byteStream.SetCurrentPosition(0);
            }

            internal byte[] ToArray()
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed) != 0,
                    this);
                return _stream.ToArray();
            }

            public void Dispose()
            {
                lock (_disposeSync)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }

                    if (!_byteStreamDisposed)
                    {
                        _byteStream.Dispose();
                        _byteStreamDisposed = true;
                    }

                    if (!_streamDisposed)
                    {
                        _stream.Dispose();
                        _streamDisposed = true;
                    }

                    Volatile.Write(ref _disposed, 1);
                }
            }
        }

        private sealed class MediaFoundationDxgiSurfaceFrame : IDisposable
        {
            private static readonly Guid Direct3DDxgiInterfaceAccessId =
                new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

            private static readonly Guid Direct3D11Texture2DId =
                new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

            private IMFMediaBuffer? _buffer;
            private ComObject? _device;

            private MediaFoundationDxgiSurfaceFrame(
                IMFMediaBuffer buffer,
                ComObject device)
            {
                _buffer = buffer;
                _device = device;
            }

            internal IMFMediaBuffer Buffer =>
                Volatile.Read(ref _buffer) ??
                throw new ObjectDisposedException(nameof(MediaFoundationDxgiSurfaceFrame));

            internal ComObject Device =>
                Volatile.Read(ref _device) ??
                throw new ObjectDisposedException(nameof(MediaFoundationDxgiSurfaceFrame));

            internal static MediaFoundationDxgiSurfaceFrame Create(
                IDirect3DSurface surface)
            {
                ArgumentNullException.ThrowIfNull(surface);
                nint inspectable = nint.Zero;
                nint interfaceAccess = nint.Zero;
                nint texture = nint.Zero;
                nint device = nint.Zero;
                nint mediaBuffer = nint.Zero;
                try
                {
                    inspectable = WinRT.MarshalInterface<IDirect3DSurface>
                        .FromManaged(surface);
                    var accessId = Direct3DDxgiInterfaceAccessId;
                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
                        inspectable,
                        in accessId,
                        out interfaceAccess));
                    var vtable = Marshal.ReadIntPtr(interfaceAccess);
                    var getInterfaceAddress = Marshal.ReadIntPtr(
                        vtable,
                        3 * IntPtr.Size);
                    var getInterface = Marshal.GetDelegateForFunctionPointer<
                        GetInterfaceDelegate>(getInterfaceAddress);
                    var textureId = Direct3D11Texture2DId;
                    Marshal.ThrowExceptionForHR(getInterface(
                        interfaceAccess,
                        in textureId,
                        out texture));
                    var textureVtable = Marshal.ReadIntPtr(texture);
                    var getDeviceAddress = Marshal.ReadIntPtr(
                        textureVtable,
                        3 * IntPtr.Size);
                    var getDevice = Marshal.GetDelegateForFunctionPointer<
                        GetDeviceDelegate>(getDeviceAddress);
                    getDevice(texture, out device);
                    if (device == nint.Zero)
                    {
                        throw new InvalidOperationException(
                            "The captured texture did not expose its Direct3D device.");
                    }

                    Marshal.ThrowExceptionForHR(NativeMethods.MFCreateDXGISurfaceBuffer(
                        in textureId,
                        texture,
                        subresourceIndex: 0,
                        bottomUpWhenLinear: false,
                        out mediaBuffer));
                    var result = new MediaFoundationDxgiSurfaceFrame(
                        new IMFMediaBuffer(mediaBuffer),
                        new ComObject(device));
                    mediaBuffer = nint.Zero;
                    device = nint.Zero;
                    return result;
                }
                finally
                {
                    ReleaseIfPresent(mediaBuffer);
                    ReleaseIfPresent(device);
                    ReleaseIfPresent(texture);
                    ReleaseIfPresent(interfaceAccess);
                    ReleaseIfPresent(inspectable);
                }
            }

            private static void ReleaseIfPresent(nint value)
            {
                if (value != nint.Zero)
                {
                    Marshal.Release(value);
                }
            }

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int GetInterfaceDelegate(
                nint @this,
                in Guid interfaceId,
                out nint result);

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate void GetDeviceDelegate(
                nint @this,
                out nint device);

            public void Dispose()
            {
                Interlocked.Exchange(ref _buffer, null)?.Dispose();
                Interlocked.Exchange(ref _device, null)?.Dispose();
            }

            private static class NativeMethods
            {
                [DllImport("mfplat.dll", ExactSpelling = true)]
                internal static extern int MFCreateDXGISurfaceBuffer(
                    in Guid interfaceId,
                    nint surface,
                    uint subresourceIndex,
                    [MarshalAs(UnmanagedType.Bool)] bool bottomUpWhenLinear,
                    out nint buffer);
            }
        }

        private sealed class BoundedMemoryStream : Stream
        {
            private readonly MemoryStream _inner;
            private readonly long _maximumLength;

            internal BoundedMemoryStream(int maximumLength)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
                _maximumLength = maximumLength;
                _inner = new MemoryStream(Math.Min(maximumLength, 1024 * 1024));
            }

            public override bool CanRead => _inner.CanRead;

            public override bool CanSeek => _inner.CanSeek;

            public override bool CanWrite => _inner.CanWrite;

            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set
                {
                    if (value < 0 || value > _maximumLength)
                    {
                        throw new IOException("The encoded chunk exceeded its memory bound.");
                    }

                    _inner.Position = value;
                }
            }

            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count) =>
                _inner.Read(buffer, offset, count);

            public override long Seek(long offset, SeekOrigin origin)
            {
                var position = _inner.Seek(offset, origin);
                if (position < 0 || position > _maximumLength)
                {
                    throw new IOException("The encoded chunk exceeded its memory bound.");
                }

                return position;
            }

            public override void SetLength(long value)
            {
                if (value < 0 || value > _maximumLength)
                {
                    throw new IOException("The encoded chunk exceeded its memory bound.");
                }

                _inner.SetLength(value);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                EnsureWritable(count);
                _inner.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                EnsureWritable(buffer.Length);
                _inner.Write(buffer);
            }

            public override void WriteByte(byte value)
            {
                EnsureWritable(1);
                _inner.WriteByte(value);
            }

            internal byte[] ToArray() => _inner.ToArray();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }

            private void EnsureWritable(int count)
            {
                if (count < 0 || Position > _maximumLength - count)
                {
                    throw new IOException("The encoded chunk exceeded its memory bound.");
                }
            }
        }
    }
}

/// <summary>
/// Serializes encoder operations with disposal without disposing the underlying managed semaphore.
/// A caller can pass the first lifetime check and become queued immediately before disposal starts;
/// it must still be able to acquire, observe disposal, and release the gate safely.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The managed semaphore intentionally follows owner GC lifetime; disposing it while a pre-disposal caller is queued would make that caller release a disposed gate.")]
internal sealed class MediaFoundationEncoderOperationGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposeStarted;
    private int _disposeCompleted;

    internal bool IsDisposeStarted => Volatile.Read(ref _disposeStarted) != 0;

    internal bool IsDisposeCompleted => Volatile.Read(ref _disposeCompleted) != 0;

    internal async ValueTask WaitAsync(
        object owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ObjectDisposedException.ThrowIf(IsDisposeStarted, owner);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposeStarted, owner);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    internal bool TryBeginDispose()
    {
        if (IsDisposeCompleted)
        {
            return false;
        }

        Volatile.Write(ref _disposeStarted, 1);
        return true;
    }

    internal async ValueTask WaitForDisposalAsync()
    {
        if (!IsDisposeStarted)
        {
            throw new InvalidOperationException(
                "Encoder disposal must begin before waiting for exclusive cleanup access.");
        }

        await _gate.WaitAsync().ConfigureAwait(false);
    }

    internal void MarkDisposeCompleted()
    {
        if (!IsDisposeStarted)
        {
            throw new InvalidOperationException(
                "Encoder disposal cannot complete before disposal admission.");
        }

        Volatile.Write(ref _disposeCompleted, 1);
    }

    internal void Release() => _gate.Release();
}

internal static class MediaFoundationFragmentCadence
{
    internal static int GetInitialFrameCount(double frameRate)
    {
        if (!double.IsFinite(frameRate) || frameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        return checked((int)Math.Round(frameRate));
    }

    internal static int GetSteadyStateFrameCount(double frameRate, bool isPrimary)
    {
        var initialFrameCount = GetInitialFrameCount(frameRate);
        return isPrimary
            ? initialFrameCount
            : checked(initialFrameCount * 2);
    }

    internal static bool IsFragmentBoundary(
        long completedFrameCount,
        int initialFrameCount,
        int steadyStateFrameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialFrameCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(steadyStateFrameCount);
        if (completedFrameCount < initialFrameCount)
        {
            return false;
        }

        return completedFrameCount == initialFrameCount ||
            (completedFrameCount - initialFrameCount) % steadyStateFrameCount == 0;
    }
}

/// <summary>
/// Owns fragment cadence independently from capture-session cadence. A capture pause discards
/// every unpublished sample and deliberately restarts the one-second initial fragment without
/// making the MP4 initialization segment eligible for re-publication.
/// </summary>
internal sealed class MediaFoundationFragmentBoundaryState
{
    private readonly int _initialFrameCount;
    private readonly int _steadyStateFrameCount;
    private int _requiredFrameCount;
    private bool _hasPublishedFragment;

    internal MediaFoundationFragmentBoundaryState(double frameRate, bool isPrimary)
    {
        _initialFrameCount =
            MediaFoundationFragmentCadence.GetInitialFrameCount(frameRate);
        _steadyStateFrameCount =
            MediaFoundationFragmentCadence.GetSteadyStateFrameCount(
                frameRate,
                isPrimary);
        _requiredFrameCount = _initialFrameCount;
    }

    internal int PendingFrameCount { get; private set; }

    internal int RequiredFrameCount => _requiredFrameCount;

    internal bool RecordFrame()
    {
        if (PendingFrameCount >= _requiredFrameCount)
        {
            throw new InvalidOperationException(
                "The completed fragment must be published before accepting another frame.");
        }

        PendingFrameCount++;
        return PendingFrameCount == _requiredFrameCount;
    }

    internal void ValidateFragmentForPublication(bool startsWithKeyFrame)
    {
        if (PendingFrameCount <= 0 || !startsWithKeyFrame)
        {
            throw new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.EncodingFailed);
        }
    }

    internal void MarkFragmentPublished()
    {
        if (PendingFrameCount <= 0)
        {
            throw new InvalidOperationException(
                "No Media Foundation fragment is pending publication.");
        }

        PendingFrameCount = 0;
        _requiredFrameCount = _steadyStateFrameCount;
        _hasPublishedFragment = true;
    }

    internal void FlushPendingFragmentForCompletion(Action flushPendingFragment)
    {
        ArgumentNullException.ThrowIfNull(flushPendingFragment);
        if (PendingFrameCount == 0)
        {
            return;
        }

        if (!_hasPublishedFragment)
        {
            throw new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.EncodingFailed);
        }

        flushPendingFragment();
    }

    internal void ResetForCapturePause()
    {
        PendingFrameCount = 0;
        _requiredFrameCount = _initialFrameCount;
    }
}

/// <summary>
/// Validates primary-tier hardware throughput and lets a capture pause open a fresh measurement
/// epoch instead of charging paused wall-clock time to the renderer.
/// </summary>
internal sealed class MediaFoundationPrimaryThroughputGate
{
    private const int CalibrationFrameCount = 30;
    private const double MinimumFramesPerSecond = 27;
    private static readonly TimeSpan MaximumCalibrationWindow = TimeSpan.FromSeconds(5);

    private TimeSpan? _startedAt;
    private int _completedFrameCount;

    internal void BeginFrame(TimeSpan monotonicNow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(monotonicNow, TimeSpan.Zero);
        _startedAt ??= monotonicNow;
    }

    internal void CompleteFrame(TimeSpan monotonicNow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(monotonicNow, TimeSpan.Zero);
        var startedAt = _startedAt ??
            throw new InvalidOperationException("No primary throughput epoch is active.");
        if (monotonicNow < startedAt)
        {
            throw new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.EncodingFailed);
        }

        _completedFrameCount++;
        var elapsed = monotonicNow - startedAt;
        if (_completedFrameCount < CalibrationFrameCount &&
            elapsed < MaximumCalibrationWindow)
        {
            return;
        }

        var effectiveFramesPerSecond = elapsed == TimeSpan.Zero
            ? double.PositiveInfinity
            : _completedFrameCount / elapsed.TotalSeconds;
        if (effectiveFramesPerSecond < MinimumFramesPerSecond)
        {
            throw new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable);
        }
    }

    internal void ResetForCapturePause()
    {
        _startedAt = null;
        _completedFrameCount = 0;
    }
}
