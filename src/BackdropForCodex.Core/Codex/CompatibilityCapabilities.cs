namespace BackdropForCodex.Core.Codex;

/// <summary>
/// Stable, non-sensitive reason codes for diagnostics and capability degradation UI.
/// Human-readable details must never be used as a control-flow contract.
/// </summary>
public enum CompatibilityCapabilityReasonCode
{
    None = 0,
    AvailableFromGlobalBaseline,
    AvailableFromPresentationContract,
    NotImplementedInCurrentRelease,
    NoMatchingPresentationContract,
    AmbiguousPresentationContract,
    StructuralProbeFailed,
    SecurityRejected,
    DisabledForGeneration,
}

public sealed record CompatibilityCapability
{
    public CompatibilityCapability(
        bool isAvailable,
        CompatibilityCapabilityReasonCode reasonCode)
    {
        if (!Enum.IsDefined(reasonCode) ||
            (isAvailable && !IsAvailabilityReason(reasonCode)) ||
            (!isAvailable && IsAvailabilityReason(reasonCode)))
        {
            throw new ArgumentException(
                "The capability availability and reason code are inconsistent.",
                nameof(reasonCode));
        }

        IsAvailable = isAvailable;
        ReasonCode = reasonCode;
    }

    public bool IsAvailable { get; }

    public CompatibilityCapabilityReasonCode ReasonCode { get; }

    internal static CompatibilityCapability AvailableFromGlobalBaseline() => new(
        true,
        CompatibilityCapabilityReasonCode.AvailableFromGlobalBaseline);

    internal static CompatibilityCapability AvailableFromPresentationContract() => new(
        true,
        CompatibilityCapabilityReasonCode.AvailableFromPresentationContract);

    internal static CompatibilityCapability Disabled(
        CompatibilityCapabilityReasonCode reasonCode) => new(false, reasonCode);

    private static bool IsAvailabilityReason(
        CompatibilityCapabilityReasonCode reasonCode) => reasonCode is
        CompatibilityCapabilityReasonCode.AvailableFromGlobalBaseline or
        CompatibilityCapabilityReasonCode.AvailableFromPresentationContract;
}

/// <summary>
/// Independently degradable presentation capabilities. Security acceptance is represented by
/// <see cref="CodexSecurityResult"/> and is deliberately not a capability.
/// </summary>
public sealed record CompatibilityCapabilities
{
    public CompatibilityCapabilities(
        CompatibilityCapability globalBackground,
        CompatibilityCapability regionRecognition,
        CompatibilityCapability glassStyle,
        CompatibilityCapability audio,
        CompatibilityCapability advancedSurfaces)
    {
        GlobalBackground = globalBackground ??
            throw new ArgumentNullException(nameof(globalBackground));
        RegionRecognition = regionRecognition ??
            throw new ArgumentNullException(nameof(regionRecognition));
        GlassStyle = glassStyle ?? throw new ArgumentNullException(nameof(glassStyle));
        Audio = audio ?? throw new ArgumentNullException(nameof(audio));
        AdvancedSurfaces = advancedSurfaces ??
            throw new ArgumentNullException(nameof(advancedSurfaces));
    }

    public CompatibilityCapability GlobalBackground { get; }

    public CompatibilityCapability RegionRecognition { get; }

    public CompatibilityCapability GlassStyle { get; }

    public CompatibilityCapability Audio { get; }

    public CompatibilityCapability AdvancedSurfaces { get; }

    // Short aliases keep call sites readable while the longer names remain self-documenting in
    // serialized diagnostics and public API explorers.
    public CompatibilityCapability Global => GlobalBackground;

    public CompatibilityCapability Regions => RegionRecognition;

    public CompatibilityCapability Glass => GlassStyle;

    public CompatibilityCapability Advanced => AdvancedSurfaces;

    public bool CanInjectGlobalWallpaper => GlobalBackground.IsAvailable;

    internal static CompatibilityCapabilities SecurityRejected() =>
        AllUnavailable(CompatibilityCapabilityReasonCode.SecurityRejected);

    internal static CompatibilityCapabilities AllUnavailable(
        CompatibilityCapabilityReasonCode reasonCode)
    {
        var disabled = CompatibilityCapability.Disabled(reasonCode);
        return new CompatibilityCapabilities(disabled, disabled, disabled, disabled, disabled);
    }

    /// <summary>
    /// Intersects this generation's current state with a new structural observation. A false
    /// capability can therefore never become true until a new generation starts.
    /// </summary>
    public CompatibilityCapabilities DowngradeWith(CompatibilityCapabilities observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return new CompatibilityCapabilities(
            Downgrade(GlobalBackground, observation.GlobalBackground),
            Downgrade(RegionRecognition, observation.RegionRecognition),
            Downgrade(GlassStyle, observation.GlassStyle),
            Downgrade(Audio, observation.Audio),
            Downgrade(AdvancedSurfaces, observation.AdvancedSurfaces));
    }

    private static CompatibilityCapability Downgrade(
        CompatibilityCapability current,
        CompatibilityCapability observation)
    {
        if (!current.IsAvailable)
        {
            return current;
        }

        return observation.IsAvailable
            ? current
            : CompatibilityCapability.Disabled(
                observation.ReasonCode == CompatibilityCapabilityReasonCode.None
                    ? CompatibilityCapabilityReasonCode.DisabledForGeneration
                    : observation.ReasonCode);
    }
}
