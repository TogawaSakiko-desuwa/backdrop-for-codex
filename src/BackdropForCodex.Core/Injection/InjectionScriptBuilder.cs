using BackdropForCodex.Core.Codex;

namespace BackdropForCodex.Core.Injection;

/// <summary>
/// Produces self-contained expressions suitable for Runtime.evaluate or PuppeteerSharp's
/// EvaluateExpressionAsync. The expressions own only nodes marked with this component's owner id.
/// </summary>
public static class InjectionScriptBuilder
{
    public const string Owner = InjectionOwnershipContract.Owner;
    public const string RootElementId = InjectionOwnershipContract.RootElementId;
    public const string StyleElementId = InjectionOwnershipContract.StyleElementId;
    public const string FileInputElementId = InjectionOwnershipContract.FileInputElementId;
    public const string StateProperty = InjectionOwnershipContract.StateProperty;

    public static readonly TimeSpan HeartbeatInterval =
        InjectionLifecycleScriptModule.HeartbeatInterval;
    public static readonly TimeSpan LeaseTimeout = InjectionLifecycleScriptModule.LeaseTimeout;

    internal static string BuildInstall(WallpaperInjectionOptions options) =>
        BuildInstall(
            options,
            PresentationContractCatalog.CreateFullySupportedCapabilities());

    internal static string BuildInstall(
        WallpaperInjectionOptions options,
        CompatibilityCapabilities capabilities) =>
        InjectionInstallScriptModule.Build(options, capabilities);

    public static string BuildActivateMedia(long generation) =>
        InjectionMediaScriptModule.BuildActivateMedia(generation);

    public static string BuildCapabilityDowngrade(
        long generation,
        CompatibilityCapabilities capabilities) =>
        InjectionStyleScriptModule.BuildCapabilityDowngrade(generation, capabilities);

    public static string BuildHeartbeat(long generation) =>
        InjectionLifecycleScriptModule.BuildHeartbeat(generation);

    public static string BuildSetPaused(long generation, bool paused) =>
        InjectionLifecycleScriptModule.BuildSetPaused(generation, paused);

    public static string BuildCleanup(long generation) =>
        InjectionLifecycleScriptModule.BuildCleanup(generation);
}
