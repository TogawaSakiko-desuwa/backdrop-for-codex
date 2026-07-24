using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using System.Runtime.ExceptionServices;

namespace BackdropForCodex.Core.Runtime;

/// <summary>
/// Owns generation-scoped observations emitted by the injection session. Capability evidence is
/// retained for diagnostics, while stale generations cannot overwrite the current attempt.
/// Health callbacks are serialized so cleanup cannot be started twice concurrently.
/// </summary>
internal sealed class WallpaperInjectionGenerationMonitor
{
    private readonly IWallpaperInjectionHealthSource? _healthSource;
    private readonly IWallpaperInjectionCapabilitySource? _capabilitySource;
    private readonly object _eventSender;
    private readonly Func<long, Task> _healthFaultHandler;
    private readonly object _backgroundTaskSync = new();
    private readonly object _capabilitySync = new();
    private Task _healthFaultTask = Task.CompletedTask;
    private long _activeGeneration;
    private long _capabilityObservationGeneration;
    private CompatibilityCapabilities _capabilitySnapshot =
        CompatibilityCapabilities.AllUnavailable(
            CompatibilityCapabilityReasonCode.DisabledForGeneration);
    private int _stopped;

    public WallpaperInjectionGenerationMonitor(
        IWallpaperInjectionHealthSource? healthSource,
        IWallpaperInjectionCapabilitySource? capabilitySource,
        object eventSender,
        Func<long, Task> healthFaultHandler)
    {
        _healthSource = healthSource;
        _capabilitySource = capabilitySource;
        _eventSender = eventSender ?? throw new ArgumentNullException(nameof(eventSender));
        _healthFaultHandler = healthFaultHandler ??
            throw new ArgumentNullException(nameof(healthFaultHandler));

        if (_healthSource is not null)
        {
            _healthSource.HealthFaulted += InjectionSession_HealthFaulted;
        }

        if (_capabilitySource is not null)
        {
            _capabilitySource.CapabilitiesChanged += InjectionSession_CapabilitiesChanged;
        }
    }

    public event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>?
        CapabilitiesChanged;

    public CompatibilityCapabilities Capabilities =>
        Volatile.Read(ref _capabilitySnapshot);

    public void CaptureCapabilities(CompatibilityCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        lock (_capabilitySync)
        {
            Volatile.Write(ref _capabilitySnapshot, capabilities);
        }
    }

    public void BeginCapabilityObservation(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        lock (_capabilitySync)
        {
            _capabilityObservationGeneration = generation;
        }
    }

    public void ResetCapabilities()
    {
        lock (_capabilitySync)
        {
            _capabilityObservationGeneration = 0;
            Volatile.Write(
                ref _capabilitySnapshot,
                CompatibilityCapabilities.AllUnavailable(
                    CompatibilityCapabilityReasonCode.DisabledForGeneration));
        }
    }

    public void MarkActive(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Interlocked.Exchange(ref _activeGeneration, generation);
    }

    public void ClearActive() => Interlocked.Exchange(ref _activeGeneration, 0);

    public bool IsActiveGeneration(long generation) =>
        generation > 0 &&
        Interlocked.Read(ref _activeGeneration) == generation;

    public Task StopObserving()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            if (_healthSource is not null)
            {
                _healthSource.HealthFaulted -= InjectionSession_HealthFaulted;
            }

            if (_capabilitySource is not null)
            {
                _capabilitySource.CapabilitiesChanged -=
                    InjectionSession_CapabilitiesChanged;
            }
        }

        lock (_backgroundTaskSync)
        {
            return _healthFaultTask;
        }
    }

    private void InjectionSession_HealthFaulted(
        object? sender,
        WallpaperInjectionHealthFaultedEventArgs eventArgs)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        lock (_backgroundTaskSync)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            var predecessor = _healthFaultTask;
            _healthFaultTask = Task.Run(
                () => RunQueuedHealthFaultAsync(predecessor, eventArgs.Generation));
        }
    }

    private async Task RunQueuedHealthFaultAsync(Task predecessor, long generation)
    {
        Exception? predecessorFailure = null;
        try
        {
            await predecessor.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            predecessorFailure = exception;
        }

        try
        {
            await _healthFaultHandler(generation).ConfigureAwait(false);
        }
        catch (Exception currentFailure)
        {
            if (predecessorFailure is not null)
            {
                throw new AggregateException(
                    "Multiple queued wallpaper health transitions failed.",
                    predecessorFailure,
                    currentFailure);
            }

            throw;
        }

        if (predecessorFailure is not null)
        {
            ExceptionDispatchInfo.Capture(predecessorFailure).Throw();
        }
    }

    private void InjectionSession_CapabilitiesChanged(
        object? sender,
        WallpaperInjectionCapabilitiesChangedEventArgs eventArgs)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        lock (_capabilitySync)
        {
            if (_capabilityObservationGeneration != eventArgs.Generation)
            {
                return;
            }

            Volatile.Write(ref _capabilitySnapshot, eventArgs.Current);
        }

        PublishCapabilitiesChanged(eventArgs);
    }

    private void PublishCapabilitiesChanged(
        WallpaperInjectionCapabilitiesChangedEventArgs eventArgs)
    {
        var handlers = CapabilitiesChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs> handler in
                 handlers.GetInvocationList())
        {
            try
            {
                handler(_eventSender, eventArgs);
            }
            catch (Exception)
            {
                // Runtime observers cannot interfere with capability fail-closed behavior.
            }
        }
    }
}
