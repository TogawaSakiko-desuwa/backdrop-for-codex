using System.Runtime.InteropServices;
using System.Threading.Channels;
using BackdropForCodex.Core.Media;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace BackdropForCodex.Core.Dynamic;

/// <summary>
/// Captures one explicitly verified wallpaper HWND through Windows Graphics Capture.
/// This implementation never accepts a monitor and never opens the system capture picker.
/// </summary>
public sealed class WindowsGraphicsCaptureFactory : IWallpaperWindowCaptureFactory
{
    internal const int BufferedFrameCapacity = 4;

    internal static long GetMaximumBufferedPixelBytes(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return checked((long)BufferedFrameCapacity * width * height * 4);
    }

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
            if (!GraphicsCaptureSession.IsSupported())
            {
                return ValueTask.FromResult(DynamicWallpaperCapability.Unavailable(
                    DynamicWallpaperCapabilityReasonCode.CaptureApiUnavailable));
            }

            using var device = WindowsDirect3DDevice.Create();
            return ValueTask.FromResult(DynamicWallpaperCapability.Available());
        }
        catch (DynamicWallpaperUnavailableException exception)
        {
            return ValueTask.FromResult(DynamicWallpaperCapability.Unavailable(
                exception.ReasonCode));
        }
        catch (Exception exception) when (
            exception is TypeLoadException or
                TypeInitializationException or
                PlatformNotSupportedException or
                COMException or
                DllNotFoundException or
                EntryPointNotFoundException)
        {
            return ValueTask.FromResult(DynamicWallpaperCapability.Unavailable(
                DynamicWallpaperCapabilityReasonCode.CaptureApiUnavailable));
        }
    }

    public async ValueTask<IWallpaperWindowCaptureSession> StartAsync(
        WallpaperWindowCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var windowHandle = await RevalidateTargetAsync(request, cancellationToken)
            .ConfigureAwait(false);
        EnsureCapturableWindow(windowHandle);

        var capability = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!capability.IsAvailable)
        {
            throw new DynamicWallpaperUnavailableException(capability.ReasonCode);
        }

        WindowsDirect3DDevice? device = null;
        GraphicsCaptureItem? item = null;
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;
        try
        {
            device = WindowsDirect3DDevice.Create();
            windowHandle = await RevalidateTargetAsync(request, cancellationToken)
                .ConfigureAwait(false);
            EnsureCapturableWindow(windowHandle);
            item = GraphicsCaptureItemFactory.CreateForWindow(windowHandle);
            var revalidatedWindowHandle = await RevalidateTargetAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureCapturableWindow(revalidatedWindowHandle);
            if (revalidatedWindowHandle != windowHandle)
            {
                throw new DynamicWallpaperUnavailableException(
                    DynamicWallpaperCapabilityReasonCode.CaptureTargetUnavailable);
            }
            if (item.Size.Width != request.Width || item.Size.Height != request.Height)
            {
                throw new DynamicWallpaperUnavailableException(
                    DynamicWallpaperCapabilityReasonCode.CapturedFrameSizeMismatch);
            }

            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device.Device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: BufferedFrameCapacity,
                new SizeInt32(request.Width, request.Height));
            session = framePool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;

            var capture = new WindowsGraphicsCaptureSession(
                request,
                device,
                item,
                framePool,
                session);
            device = null;
            item = null;
            framePool = null;
            session = null;
            capture.Start();
            return capture;
        }
        catch (DynamicWallpaperUnavailableException)
        {
            session?.Dispose();
            framePool?.Dispose();
            device?.Dispose();
            throw;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            session?.Dispose();
            framePool?.Dispose();
            device?.Dispose();
            throw new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.CaptureTargetUnavailable,
                exception);
        }
    }

    private static async ValueTask<nint> RevalidateTargetAsync(
        WallpaperWindowCaptureRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await request.CaptureTarget
                .RevalidateAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WallpaperEnginePlatformUnavailableException exception)
        {
            throw new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.CaptureTargetUnavailable,
                exception);
        }
    }

    private static void EnsureCapturableWindow(nint windowHandle)
    {
        if (!NativeMethods.IsWindow(windowHandle) ||
            windowHandle == NativeMethods.GetDesktopWindow() ||
            windowHandle == NativeMethods.GetShellWindow())
        {
            throw new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.CaptureTargetUnavailable);
        }
    }

    private sealed class WindowsGraphicsCaptureSession : IWallpaperWindowCaptureSession
    {
        private readonly WallpaperWindowCaptureRequest _request;
        private readonly WindowsDirect3DDevice _device;
        private readonly GraphicsCaptureItem _item;
        private readonly Direct3D11CaptureFramePool _framePool;
        private readonly GraphicsCaptureSession _session;
        private readonly Channel<IWallpaperCapturedFrame> _frames;
        private readonly WindowsGraphicsCaptureEnqueueState _enqueueState;
        private readonly object _disposeSync = new();
        private readonly Queue<IWallpaperCapturedFrame> _pendingFrameCleanup = new();
        private bool _handlersDetached;
        private bool _writerCompleted;
        private bool _sessionDisposed;
        private bool _framePoolDisposed;
        private bool _deviceDisposed;
        private int _disposed;
        private int _disposeCompleted;

        public WindowsGraphicsCaptureSession(
            WallpaperWindowCaptureRequest request,
            WindowsDirect3DDevice device,
            GraphicsCaptureItem item,
            Direct3D11CaptureFramePool framePool,
            GraphicsCaptureSession session)
        {
            _request = request;
            _device = device;
            _item = item;
            _framePool = framePool;
            _session = session;
            _enqueueState = new WindowsGraphicsCaptureEnqueueState(request.FrameRate);
            _frames = Channel.CreateBounded<IWallpaperCapturedFrame>(
                new BoundedChannelOptions(BufferedFrameCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false,
                });

            _framePool.FrameArrived += OnFrameArrived;
            _item.Closed += OnItemClosed;
        }

        public long Generation => _request.Generation;

        public void Start()
        {
            try
            {
                _session.StartCapture();
            }
            catch
            {
                DisposeCore();
                throw;
            }
        }

        public IAsyncEnumerable<IWallpaperCapturedFrame> ReadFramesAsync(
            CancellationToken cancellationToken = default) =>
            _frames.Reader.ReadAllAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            DisposeCore();
            return ValueTask.CompletedTask;
        }

        private void OnFrameArrived(
            Direct3D11CaptureFramePool sender,
            object args)
        {
            _ = args;
            Direct3D11CaptureFrame? nativeFrame = null;
            try
            {
                nativeFrame = sender.TryGetNextFrame();
                if (nativeFrame is null)
                {
                    return;
                }

                if (Volatile.Read(ref _disposed) != 0)
                {
                    nativeFrame.Dispose();
                    return;
                }

                var contentSize = nativeFrame.ContentSize;
                if (contentSize.Width != _request.Width ||
                    contentSize.Height != _request.Height)
                {
                    nativeFrame.Dispose();
                    _frames.Writer.TryComplete(new DynamicWallpaperUnavailableException(
                        DynamicWallpaperCapabilityReasonCode.CapturedFrameSizeMismatch));
                    return;
                }

                var timestamp = nativeFrame.SystemRelativeTime;
                var committed = _enqueueState.TryCommit(timestamp, sequence =>
                {
                    var frame = new WindowsGraphicsCapturedFrame(
                        _request.Generation,
                        sequence,
                        timestamp,
                        nativeFrame!);
                    nativeFrame = null;
                    if (_frames.Writer.TryWrite(frame))
                    {
                        return true;
                    }

                    frame.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    return false;
                });
                if (!committed)
                {
                    nativeFrame?.Dispose();
                    nativeFrame = null;
                }
            }
            catch (Exception exception) when (
                exception is COMException or ObjectDisposedException)
            {
                nativeFrame?.Dispose();
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _frames.Writer.TryComplete(new DynamicWallpaperUnavailableException(
                        DynamicWallpaperCapabilityReasonCode.GraphicsDeviceUnavailable,
                        exception));
                }
            }
        }

        private void OnItemClosed(GraphicsCaptureItem sender, object args)
        {
            _ = sender;
            _ = args;
            _frames.Writer.TryComplete(new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.CaptureTargetUnavailable));
        }

        private void DisposeCore()
        {
            lock (_disposeSync)
            {
                if (Volatile.Read(ref _disposeCompleted) != 0)
                {
                    return;
                }

                Volatile.Write(ref _disposed, 1);
                if (!_handlersDetached)
                {
                    _framePool.FrameArrived -= OnFrameArrived;
                    _item.Closed -= OnItemClosed;
                    _handlersDetached = true;
                }

                if (!_writerCompleted)
                {
                    _frames.Writer.TryComplete();
                    _writerCompleted = true;
                }

                if (!_sessionDisposed)
                {
                    _session.Dispose();
                    _sessionDisposed = true;
                }

                if (!_framePoolDisposed)
                {
                    _framePool.Dispose();
                    _framePoolDisposed = true;
                }

                while (_frames.Reader.TryRead(out var frame))
                {
                    _pendingFrameCleanup.Enqueue(frame);
                }

                while (_pendingFrameCleanup.TryPeek(out var frame))
                {
                    frame.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    _pendingFrameCleanup.Dequeue();
                }

                if (!_deviceDisposed)
                {
                    _device.Dispose();
                    _deviceDisposed = true;
                }

                Volatile.Write(ref _disposeCompleted, 1);
            }
        }
    }

    internal sealed class WindowsGraphicsCapturedFrame : IWallpaperCapturedFrame
    {
        private readonly RetryableNativeDisposalSlot<Direct3D11CaptureFrame> _frame;

        public WindowsGraphicsCapturedFrame(
            long generation,
            long sequence,
            TimeSpan timestamp,
            Direct3D11CaptureFrame frame)
        {
            Generation = generation;
            Sequence = sequence;
            Timestamp = timestamp;
            _frame = new RetryableNativeDisposalSlot<Direct3D11CaptureFrame>(
                frame ?? throw new ArgumentNullException(nameof(frame)));
            Width = frame.ContentSize.Width;
            Height = frame.ContentSize.Height;
        }

        public long Generation { get; }

        public long Sequence { get; }

        public TimeSpan Timestamp { get; }

        public int Width { get; }

        public int Height { get; }

        internal IDirect3DSurface Surface =>
            _frame.Value.Surface;

        public ValueTask DisposeAsync()
        {
            _frame.Dispose(static frame => frame.Dispose());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WindowsDirect3DDevice : IDisposable
    {
        private readonly RetryableNativeDisposalSlot<IDirect3DDevice> _device;

        private WindowsDirect3DDevice(IDirect3DDevice device)
        {
            _device = new RetryableNativeDisposalSlot<IDirect3DDevice>(device);
        }

        public IDirect3DDevice Device => _device.Value;

        public static WindowsDirect3DDevice Create()
        {
            nint d3dDevice = nint.Zero;
            nint d3dContext = nint.Zero;
            nint dxgiDevice = nint.Zero;
            nint inspectable = nint.Zero;
            try
            {
                var result = NativeMethods.D3D11CreateDevice(
                    nint.Zero,
                    driverType: 1,
                    nint.Zero,
                    flags: 0x20,
                    nint.Zero,
                    featureLevels: 0,
                    sdkVersion: 7,
                    out d3dDevice,
                    out _,
                    out d3dContext);
                if (result < 0 || d3dDevice == nint.Zero)
                {
                    throw new DynamicWallpaperUnavailableException(
                        DynamicWallpaperCapabilityReasonCode.GraphicsDeviceUnavailable);
                }

                var iidDxgiDevice = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
                    d3dDevice,
                    in iidDxgiDevice,
                    out dxgiDevice));
                Marshal.ThrowExceptionForHR(
                    NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(
                        dxgiDevice,
                        out inspectable));
                var projected = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                return new WindowsDirect3DDevice(projected);
            }
            catch (DynamicWallpaperUnavailableException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is COMException or DllNotFoundException or EntryPointNotFoundException)
            {
                throw new DynamicWallpaperUnavailableException(
                    DynamicWallpaperCapabilityReasonCode.GraphicsDeviceUnavailable,
                    exception);
            }
            finally
            {
                ReleaseIfPresent(inspectable);
                ReleaseIfPresent(dxgiDevice);
                ReleaseIfPresent(d3dContext);
                ReleaseIfPresent(d3dDevice);
            }
        }

        public void Dispose()
        {
            _device.Dispose(static device => (device as IDisposable)?.Dispose());
        }

        internal static void ReleaseIfPresent(nint value)
        {
            if (value != nint.Zero)
            {
                Marshal.Release(value);
            }
        }
    }

    private static class GraphicsCaptureItemFactory
    {
        private const string RuntimeClassName =
            "Windows.Graphics.Capture.GraphicsCaptureItem";

        private static readonly Guid InteropInterfaceId =
            new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

        private static readonly Guid CaptureItemInterfaceId =
            new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        public static GraphicsCaptureItem CreateForWindow(nint windowHandle)
        {
            nint className = nint.Zero;
            nint factory = nint.Zero;
            nint item = nint.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(NativeMethods.WindowsCreateString(
                    RuntimeClassName,
                    RuntimeClassName.Length,
                    out className));
                var interopId = InteropInterfaceId;
                Marshal.ThrowExceptionForHR(NativeMethods.RoGetActivationFactory(
                    className,
                    ref interopId,
                    out factory));

                var vtable = Marshal.ReadIntPtr(factory);
                var createForWindowAddress = Marshal.ReadIntPtr(
                    vtable,
                    3 * IntPtr.Size);
                var createForWindow = Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(
                    createForWindowAddress);
                var itemId = CaptureItemInterfaceId;
                Marshal.ThrowExceptionForHR(createForWindow(
                    factory,
                    windowHandle,
                    ref itemId,
                    out item));
                if (item == nint.Zero)
                {
                    throw new DynamicWallpaperUnavailableException(
                        DynamicWallpaperCapabilityReasonCode.CaptureTargetUnavailable);
                }

                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
            }
            finally
            {
                WindowsDirect3DDevice.ReleaseIfPresent(item);
                WindowsDirect3DDevice.ReleaseIfPresent(factory);
                if (className != nint.Zero)
                {
                    Marshal.ThrowExceptionForHR(
                        NativeMethods.WindowsDeleteString(className));
                }
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateForWindowDelegate(
            nint @this,
            nint windowHandle,
            ref Guid interfaceId,
            out nint result);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint windowHandle);

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint GetDesktopWindow();

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint GetShellWindow();

        [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int WindowsCreateString(
            string source,
            int length,
            out nint value);

        [DllImport("combase.dll", ExactSpelling = true)]
        internal static extern int WindowsDeleteString(nint value);

        [DllImport("combase.dll", ExactSpelling = true)]
        internal static extern int RoGetActivationFactory(
            nint className,
            ref Guid interfaceId,
            out nint factory);

        [DllImport("d3d11.dll", ExactSpelling = true)]
        internal static extern int D3D11CreateDevice(
            nint adapter,
            int driverType,
            nint software,
            uint flags,
            nint featureLevelArray,
            uint featureLevels,
            uint sdkVersion,
            out nint device,
            out int featureLevel,
            out nint immediateContext);

        [DllImport("d3d11.dll", ExactSpelling = true)]
        internal static extern int CreateDirect3D11DeviceFromDXGIDevice(
            nint dxgiDevice,
            out nint graphicsDevice);
    }
}

internal sealed class RetryableNativeDisposalSlot<T>
    where T : class
{
    private readonly object _sync = new();
    private T? _value;

    internal RetryableNativeDisposalSlot(T value)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal T Value
    {
        get
        {
            lock (_sync)
            {
                return _value ?? throw new ObjectDisposedException(GetType().Name);
            }
        }
    }

    internal bool IsEmpty
    {
        get
        {
            lock (_sync)
            {
                return _value is null;
            }
        }
    }

    internal void Dispose(Action<T> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (_sync)
        {
            var value = _value;
            if (value is null)
            {
                return;
            }

            release(value);
            _value = null;
        }
    }
}

internal static class WindowsGraphicsCaptureCadence
{
    internal static readonly TimeSpan TimestampJitterTolerance =
        TimeSpan.FromMilliseconds(1);

    internal static TimeSpan GetTargetInterval(double frameRate)
    {
        return TimeSpan.FromTicks(checked((long)Math.Ceiling(
            GetTargetIntervalTicks(frameRate))));
    }

    internal static decimal GetTargetIntervalTicks(double frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        if (!double.IsFinite(frameRate))
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        return TimeSpan.TicksPerSecond / (decimal)frameRate;
    }

    internal static TimeSpan GetMinimumAcceptedInterval(double frameRate)
    {
        var targetInterval = GetTargetInterval(frameRate);
        return targetInterval > TimestampJitterTolerance
            ? targetInterval - TimestampJitterTolerance
            : TimeSpan.Zero;
    }

    internal static bool ShouldAccept(
        long previousTimestampTicks,
        long currentTimestampTicks,
        TimeSpan minimumInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumInterval, TimeSpan.Zero);
        if (previousTimestampTicks == long.MinValue)
        {
            return true;
        }

        return currentTimestampTicks > previousTimestampTicks &&
            currentTimestampTicks - previousTimestampTicks >= minimumInterval.Ticks;
    }
}

internal sealed class WindowsGraphicsCaptureEnqueueState
{
    private readonly object _gate = new();
    private readonly decimal _targetIntervalTicks;
    private long _lastAcceptedTimestampTicks = long.MinValue;
    private decimal _nextDueTimestampTicks;
    private bool _hasNextDueTimestamp;
    private long _nextSequence;

    internal WindowsGraphicsCaptureEnqueueState(double frameRate)
    {
        _targetIntervalTicks =
            WindowsGraphicsCaptureCadence.GetTargetIntervalTicks(frameRate);
    }

    internal bool TryCommit(TimeSpan timestamp, Func<long, bool> tryEnqueue)
    {
        ArgumentNullException.ThrowIfNull(tryEnqueue);
        lock (_gate)
        {
            var timestampTicks = timestamp.Ticks;
            if (_lastAcceptedTimestampTicks != long.MinValue &&
                (timestampTicks <= _lastAcceptedTimestampTicks ||
                    (decimal)timestampTicks <
                    _nextDueTimestampTicks -
                    WindowsGraphicsCaptureCadence.TimestampJitterTolerance.Ticks))
            {
                return false;
            }

            if (_nextSequence == long.MaxValue)
            {
                throw new OverflowException("The capture frame sequence was exhausted.");
            }

            if (!tryEnqueue(_nextSequence))
            {
                return false;
            }

            if (!_hasNextDueTimestamp)
            {
                _nextDueTimestampTicks = timestampTicks + _targetIntervalTicks;
                _hasNextDueTimestamp = true;
            }
            else
            {
                do
                {
                    _nextDueTimestampTicks += _targetIntervalTicks;
                }
                while (_nextDueTimestampTicks -
                    WindowsGraphicsCaptureCadence.TimestampJitterTolerance.Ticks <=
                    timestampTicks);
            }

            _lastAcceptedTimestampTicks = timestampTicks;
            _nextSequence++;
            return true;
        }
    }
}
