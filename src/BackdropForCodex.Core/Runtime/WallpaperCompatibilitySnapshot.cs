using BackdropForCodex.Core.Codex;

namespace BackdropForCodex.Core.Runtime;

/// <summary>
/// The most recent non-sensitive compatibility attempt. It deliberately survives wallpaper
/// cleanup so a user can export diagnostics after a failure or after restoring the official
/// background.
/// </summary>
public sealed record WallpaperCompatibilitySnapshot(
    Version? CodexVersion,
    CodexSecurityResult Security,
    PresentationContractSnapshot Presentation,
    CompatibilityCapabilities Capabilities)
{
    public static WallpaperCompatibilitySnapshot NotEvaluated { get; } = new(
        CodexVersion: null,
        CodexSecurityResult.NotEvaluated(),
        PresentationContractSnapshot.NotEvaluated,
        CompatibilityCapabilities.AllUnavailable(
            CompatibilityCapabilityReasonCode.DisabledForGeneration));
}
