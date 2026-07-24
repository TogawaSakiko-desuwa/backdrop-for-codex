using BackdropForCodex.Core.Codex;
using System.Collections.Frozen;
using System.Text.Json;

namespace BackdropForCodex.Core.Injection;

/// <summary>
/// Builds the embedded, read-only DOM probes used to degrade presentation capabilities. The
/// package choice has already been made by the compatibility catalog; this builder never falls
/// back from an exact or reviewed-band package to the generic package.
/// </summary>
internal static class CompatibilityProbeScriptBuilder
{
    private const string GenericProbePackageId = "openai-codex-generic-dom-probes-v1";
    private const string GlassSurfaceSelector =
        "[data-app-shell-focus-area], .app-header-tint, aside";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Each entry is a separately reviewed artifact. Adding an exact catalog profile without
    // adding its probe here intentionally fails closed instead of inheriting generic behavior.
    private static readonly FrozenDictionary<Version, ExactProbeDefinition>
        ExactProbeDefinitionsByVersion = new[]
        {
            new ExactProbeDefinition(
                new Version(26, 715, 10079, 0),
                "openai-codex-26.715.10079.0-windows11-x64-v1-dom-probes",
                "body > #root",
                "main",
                GlassSurfaceSelector),
            new ExactProbeDefinition(
                new Version(26, 721, 3404, 0),
                "openai-codex-26.721.3404.0-windows11-x64-v1-dom-probes",
                "body > #root",
                "main",
                GlassSurfaceSelector),
            new ExactProbeDefinition(
                new Version(26, 721, 3996, 0),
                "openai-codex-26.721.3996.0-windows11-x64-v1-dom-probes",
                "body > #root",
                "main",
                GlassSurfaceSelector),
        }.ToFrozenDictionary(definition => definition.PackageVersion);

    private static readonly ReviewedBandProbeDefinition Reviewed721Band = new(
        "openai-codex-26.721-reviewed-band-windows11-x64-v1-dom-probes",
        new Version(26, 721, 3404, 0),
        new Version(26, 722, 0, 0),
        "body > #root",
        "main",
        GlassSurfaceSelector);

    private static readonly GenericProbeDefinition GenericProbe = new(
        GenericProbePackageId,
        "body > #root",
        "main");

    internal static string Build(CodexCompatibilityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.ProbePackageKind switch
        {
            CompatibilityProbePackageKind.Exact => BuildExact(
                profile,
                GetExactProbeDefinition(profile)),
            CompatibilityProbePackageKind.ReviewedBand => BuildReviewedBand(profile),
            CompatibilityProbePackageKind.Generic => BuildGeneric(profile),
            _ => throw new ArgumentOutOfRangeException(
                nameof(profile),
                "The compatibility profile selected an unknown probe package kind."),
        };
    }

    private static ExactProbeDefinition GetExactProbeDefinition(
        CodexCompatibilityProfile profile)
    {
        if (!ExactProbeDefinitionsByVersion.TryGetValue(
                profile.PackageVersion,
                out var definition) ||
            !string.Equals(
                profile.ProbePackageId,
                definition.ProbePackageId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"No immutable exact probe definition is registered for reviewed version " +
                $"{profile.PackageVersion}. Generic fallback is prohibited.");
        }

        return definition;
    }

    private static string BuildExact(
        CodexCompatibilityProfile profile,
        ExactProbeDefinition definition) =>
        BuildReviewedPresentationProbe(
            profile,
            definition.ProbePackageId,
            definition.AppRootSelector,
            definition.MainSelector,
            definition.GlassSurfaceSelector);

    private static string BuildReviewedBand(CodexCompatibilityProfile profile)
    {
        if (!string.Equals(
                profile.ProbePackageId,
                Reviewed721Band.ProbePackageId,
                StringComparison.Ordinal) ||
            !Reviewed721Band.Contains(profile.PackageVersion))
        {
            throw new InvalidOperationException(
                $"No immutable reviewed-band probe definition is registered for version " +
                $"{profile.PackageVersion}. Generic fallback is prohibited.");
        }

        return BuildReviewedPresentationProbe(
            profile,
            Reviewed721Band.ProbePackageId,
            Reviewed721Band.AppRootSelector,
            Reviewed721Band.MainSelector,
            Reviewed721Band.GlassSurfaceSelector);
    }

    private static string BuildReviewedPresentationProbe(
        CodexCompatibilityProfile profile,
        string probePackageId,
        string appRootSelector,
        string mainSelector,
        string glassSurfaceSelector)
    {
        var declared = profile.Capabilities;
        var payload = JsonSerializer.Serialize(
            new ReviewedProbePayload(
                probePackageId,
                appRootSelector,
                mainSelector,
                glassSurfaceSelector,
                declared.Global.IsAvailable,
                declared.Regions.IsAvailable,
                declared.Glass.IsAvailable,
                declared.Audio.IsAvailable,
                declared.Advanced.IsAvailable),
            SerializerOptions);

        return $$"""
            (() => {
              "use strict";
              const probe = Object.freeze({{payload}});
              const root = document.documentElement;
              const body = document.body;
              const appRoot = document.querySelector(probe.appRootSelector);
              const main = appRoot && appRoot.querySelector(probe.mainSelector);
              const globalBackground = probe.globalBackground &&
                Boolean(root && body && appRoot && main && appRoot.contains(main));
              const glassStructure = Boolean(
                appRoot && appRoot.querySelector(probe.glassSurfaceSelector));
              const cssApi = globalThis.CSS;
              const glassPlatform = Boolean(cssApi && typeof cssApi.supports === "function" &&
                (cssApi.supports("backdrop-filter", "blur(1px)") ||
                 cssApi.supports("-webkit-backdrop-filter", "blur(1px)")));
              const selectorPlatform = Boolean(cssApi &&
                typeof cssApi.supports === "function" &&
                cssApi.supports("selector(:has(*))"));
              return JSON.stringify({
                globalBackground,
                regionRecognition: probe.regionRecognition && false,
                glassStyle: probe.glassStyle && globalBackground &&
                  glassPlatform && selectorPlatform && glassStructure,
                audio: probe.audio && false,
                // Advanced route surfaces are optional and appear only after navigation.
                // Their absence is not a structural failure; reviewed selectors safely no-op.
                advancedSurfaces: probe.advancedSurfaces && globalBackground &&
                  selectorPlatform
              });
            })()
            """;
    }

    private static string BuildGeneric(CodexCompatibilityProfile profile)
    {
        if (!string.Equals(
                profile.ProbePackageId,
                GenericProbe.ProbePackageId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The generic compatibility profile selected an unknown probe definition.");
        }

        var payload = JsonSerializer.Serialize(
            new GenericProbePayload(
                GenericProbe.ProbePackageId,
                GenericProbe.AppRootSelector,
                GenericProbe.MainSelector,
                profile.Capabilities.Global.IsAvailable),
            SerializerOptions);

        return $$"""
            (() => {
              "use strict";
              const probe = Object.freeze({{payload}});
              const root = document.documentElement;
              const body = document.body;
              const appRoot = document.querySelector(probe.appRootSelector);
              const main = appRoot && appRoot.querySelector(probe.mainSelector);
              const globalBackground = probe.globalBackground &&
                Boolean(root && body && appRoot && main && appRoot.contains(main));
              return JSON.stringify({
                globalBackground,
                regionRecognition: false,
                glassStyle: false,
                audio: false,
                advancedSurfaces: false
              });
            })()
            """;
    }

    internal static CompatibilityCapabilities ParseObservation(
        string json,
        CompatibilityProbePackageKind packageKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (!Enum.IsDefined(packageKind))
        {
            throw new ArgumentOutOfRangeException(nameof(packageKind));
        }

        var observation = JsonSerializer.Deserialize<ProbeObservation>(json, SerializerOptions)
            ?? throw new JsonException("The compatibility probe returned no observation.");

        if (packageKind == CompatibilityProbePackageKind.Generic)
        {
            var genericNotImplemented = new CompatibilityCapability(
                false,
                CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease);
            var unavailableForGeneric = new CompatibilityCapability(
                false,
                CompatibilityCapabilityReasonCode.UnavailableForGenericProbePackage);
            return new CompatibilityCapabilities(
                FromObservation(observation.GlobalBackground, packageKind),
                genericNotImplemented,
                unavailableForGeneric,
                genericNotImplemented,
                unavailableForGeneric);
        }

        var notImplemented = new CompatibilityCapability(
            false,
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease);
        return new CompatibilityCapabilities(
            FromObservation(observation.GlobalBackground, packageKind),
            notImplemented,
            FromObservation(observation.GlassStyle, packageKind),
            notImplemented,
            FromObservation(observation.AdvancedSurfaces, packageKind));
    }

    internal static CompatibilityCapabilities FailedObservation() =>
        CompatibilityCapabilities.AllUnavailable(
            CompatibilityCapabilityReasonCode.StructuralProbeFailed);

    private static CompatibilityCapability FromObservation(
        bool available,
        CompatibilityProbePackageKind packageKind) => available
        ? CompatibilityCapability.Available(packageKind)
        : new CompatibilityCapability(
            false,
            CompatibilityCapabilityReasonCode.StructuralProbeFailed);

    private sealed record ExactProbeDefinition(
        Version PackageVersion,
        string ProbePackageId,
        string AppRootSelector,
        string MainSelector,
        string GlassSurfaceSelector);

    private sealed record ReviewedBandProbeDefinition(
        string ProbePackageId,
        Version MinimumVersionInclusive,
        Version MaximumVersionExclusive,
        string AppRootSelector,
        string MainSelector,
        string GlassSurfaceSelector)
    {
        public bool Contains(Version version) =>
            version >= MinimumVersionInclusive &&
            version < MaximumVersionExclusive;
    }

    private sealed record GenericProbeDefinition(
        string ProbePackageId,
        string AppRootSelector,
        string MainSelector);

    private sealed record ReviewedProbePayload(
        string ProbePackageId,
        string AppRootSelector,
        string MainSelector,
        string GlassSurfaceSelector,
        bool GlobalBackground,
        bool RegionRecognition,
        bool GlassStyle,
        bool Audio,
        bool AdvancedSurfaces);

    private sealed record GenericProbePayload(
        string ProbePackageId,
        string AppRootSelector,
        string MainSelector,
        bool GlobalBackground);

    private sealed record ProbeObservation(
        bool GlobalBackground,
        bool RegionRecognition,
        bool GlassStyle,
        bool Audio,
        bool AdvancedSurfaces);
}
