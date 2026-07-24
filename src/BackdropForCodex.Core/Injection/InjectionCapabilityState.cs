using BackdropForCodex.Core.Codex;

namespace BackdropForCodex.Core.Injection;

/// <summary>
/// Keeps compatibility observations monotonic within one wallpaper generation.
/// </summary>
internal sealed class InjectionCapabilityState
{
    public CompatibilityCapabilities Current { get; private set; } =
        CompatibilityCapabilities.AllUnavailable(
            CompatibilityCapabilityReasonCode.DisabledForGeneration);

    public void Begin(CompatibilityCapabilities declared, bool continuesCurrentGeneration)
    {
        ArgumentNullException.ThrowIfNull(declared);
        Current = continuesCurrentGeneration
            ? Current.DowngradeWith(declared)
            : declared;
    }

    public CapabilityTransition Observe(CompatibilityCapabilities observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var previous = Current;
        Current = previous.DowngradeWith(observation);
        return new CapabilityTransition(previous, Current);
    }

    public void Reset()
    {
        Current = CompatibilityCapabilities.AllUnavailable(
            CompatibilityCapabilityReasonCode.DisabledForGeneration);
    }

    public static bool RequiresOwnedStyleDowngrade(
        CompatibilityCapabilities previous,
        CompatibilityCapabilities current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        return (previous.Glass.IsAvailable && !current.Glass.IsAvailable) ||
               (previous.Advanced.IsAvailable && !current.Advanced.IsAvailable);
    }
}

internal readonly record struct CapabilityTransition(
    CompatibilityCapabilities Previous,
    CompatibilityCapabilities Current);
