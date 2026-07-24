using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed class CompatibilityProbeScriptBuilderTests
{
    [Theory]
    [InlineData("26.715.10079.0", CompatibilityProbePackageKind.Exact)]
    [InlineData("26.721.3404.0", CompatibilityProbePackageKind.Exact)]
    [InlineData("26.721.3996.0", CompatibilityProbePackageKind.Exact)]
    [InlineData("26.721.3405.0", CompatibilityProbePackageKind.ReviewedBand)]
    [InlineData("26.722.0.0", CompatibilityProbePackageKind.Generic)]
    [InlineData("27.4.5.6", CompatibilityProbePackageKind.Generic)]
    public void Build_UsesCatalogSelectedProbePackageWithoutEmbeddingMachineIdentity(
        string version,
        CompatibilityProbePackageKind expectedKind)
    {
        var profile = BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests.GetProfile(
            Version.Parse(version));

        var script = CompatibilityProbeScriptBuilder.Build(profile);

        Assert.Equal(expectedKind, profile.ProbePackageKind);
        Assert.Contains("\"globalBackground\":true", script, StringComparison.Ordinal);
        Assert.Contains(profile.ProbePackageId, script, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.PackageFullName, script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        CompatibilityProbePackageKind.Exact,
        CompatibilityCapabilityReasonCode.AvailableFromExactProbePackage,
        CompatibilityCapabilityReasonCode.AvailableFromExactProbePackage,
        CompatibilityCapabilityReasonCode.StructuralProbeFailed)]
    [InlineData(
        CompatibilityProbePackageKind.ReviewedBand,
        CompatibilityCapabilityReasonCode.AvailableFromReviewedBandProbePackage,
        CompatibilityCapabilityReasonCode.AvailableFromReviewedBandProbePackage,
        CompatibilityCapabilityReasonCode.StructuralProbeFailed)]
    [InlineData(
        CompatibilityProbePackageKind.Generic,
        CompatibilityCapabilityReasonCode.AvailableFromGenericProbePackage,
        CompatibilityCapabilityReasonCode.UnavailableForGenericProbePackage,
        CompatibilityCapabilityReasonCode.UnavailableForGenericProbePackage)]
    public void ParseObservation_EnforcesProbePackageCapabilityBoundary(
        CompatibilityProbePackageKind packageKind,
        CompatibilityCapabilityReasonCode expectedGlobalReason,
        CompatibilityCapabilityReasonCode expectedGlassReason,
        CompatibilityCapabilityReasonCode expectedAdvancedReason)
    {
        const string json =
            """
            {
              "globalBackground": true,
              "regionRecognition": false,
              "glassStyle": true,
              "audio": false,
              "advancedSurfaces": false
            }
            """;

        var capabilities = CompatibilityProbeScriptBuilder.ParseObservation(json, packageKind);

        Assert.Equal(expectedGlobalReason, capabilities.Global.ReasonCode);
        Assert.Equal(expectedGlassReason, capabilities.Glass.ReasonCode);
        Assert.Equal(expectedAdvancedReason, capabilities.Advanced.ReasonCode);
    }

    [Fact]
    public void BuildInstall_DisablesOnlyFailedStyleCapabilities()
    {
        var declared = BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests
            .GetProfile()
            .Capabilities;
        var degraded = declared.DowngradeWith(
            new CompatibilityCapabilities(
                declared.Global,
                declared.Regions,
                new CompatibilityCapability(
                    false,
                    CompatibilityCapabilityReasonCode.StructuralProbeFailed),
                declared.Audio,
                declared.Advanced));
        var options = new WallpaperInjectionOptions(
            generation: 3,
            source: new Uri("file:///C:/Wallpapers/wallpaper.png"),
            localMediaPath: @"C:\Wallpapers\wallpaper.png",
            expectedContentLength: 4096,
            WallpaperMediaKind.Image);

        var script = InjectionScriptBuilder.BuildInstall(options, degraded);

        Assert.Contains("\"glassEnabled\":false", script, StringComparison.Ordinal);
        Assert.Contains("\"advancedSurfacesEnabled\":true", script, StringComparison.Ordinal);
        Assert.Contains(
            "body[data-codex-wallpaper-glass-disabled]",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UsesDistinctExactReviewedBandAndConservativeGenericProbePackages()
    {
        var exactProfile =
            BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests.GetProfile();
        var reviewedBandProfile =
            BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests.GetProfile(
                new Version(26, 721, 3405, 0));
        var genericProfile =
            BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests.GetProfile(
                new Version(26, 722, 0, 0));
        var exactScript = CompatibilityProbeScriptBuilder.Build(exactProfile);
        var reviewedBandScript =
            CompatibilityProbeScriptBuilder.Build(reviewedBandProfile);
        var genericScript = CompatibilityProbeScriptBuilder.Build(genericProfile);

        foreach (var script in new[] { exactScript, reviewedBandScript, genericScript })
        {
            Assert.Contains("body \\u003E #root", script, StringComparison.Ordinal);
            Assert.Contains("appRoot.contains(main)", script, StringComparison.Ordinal);
        }

        foreach (var script in new[] { exactScript, reviewedBandScript })
        {
            Assert.Contains("selector(:has(*))", script, StringComparison.Ordinal);
            Assert.Contains("glassPlatform", script, StringComparison.Ordinal);
            Assert.Contains("glassStructure", script, StringComparison.Ordinal);
            Assert.Contains("advancedStructure", script, StringComparison.Ordinal);
            Assert.Contains("data-user-message-bubble", script, StringComparison.Ordinal);
            Assert.Contains("data-app-shell-focus-area", script, StringComparison.Ordinal);
            Assert.Contains("app-header-tint", script, StringComparison.Ordinal);
            Assert.Contains(
                "data-app-shell-tab-panel-controller",
                script,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "data-home-ambient-suggestions",
                script,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "data-radix-popper-content-wrapper",
                script,
                StringComparison.Ordinal);
            Assert.Contains(
                "glassPlatform && selectorPlatform && glassStructure",
                script,
                StringComparison.Ordinal);
            Assert.Contains(
                "selectorPlatform && advancedStructure",
                script,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "openai-codex-generic-dom-probes-v1",
                script,
                StringComparison.Ordinal);
        }

        Assert.NotEqual(exactScript, genericScript);
        Assert.NotEqual(exactScript, reviewedBandScript);
        Assert.NotEqual(reviewedBandScript, genericScript);
        Assert.Contains(exactProfile.ProbePackageId, exactScript, StringComparison.Ordinal);
        Assert.Contains(
            reviewedBandProfile.ProbePackageId,
            reviewedBandScript,
            StringComparison.Ordinal);
        Assert.Contains(genericProfile.ProbePackageId, genericScript, StringComparison.Ordinal);
        Assert.DoesNotContain("selector(:has(*))", genericScript, StringComparison.Ordinal);
        Assert.DoesNotContain("glassPlatform", genericScript, StringComparison.Ordinal);
        Assert.DoesNotContain("glassStructure", genericScript, StringComparison.Ordinal);
        Assert.DoesNotContain("advancedStructure", genericScript, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "data-home-ambient-suggestions",
            genericScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "data-user-message-bubble",
            genericScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "data-app-shell-focus-area",
            genericScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "data-radix-popper-content-wrapper",
            genericScript,
            StringComparison.Ordinal);
        Assert.Contains("glassStyle: false", genericScript, StringComparison.Ordinal);
        Assert.Contains("advancedSurfaces: false", genericScript, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactStructuralFailure_RemainsFailedWithoutGenericFallback()
    {
        const string failedExactObservation =
            """
            {
              "globalBackground": false,
              "regionRecognition": false,
              "glassStyle": false,
              "audio": false,
              "advancedSurfaces": false
            }
            """;
        var exactProfile =
            BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests.GetProfile();
        var exactScript = CompatibilityProbeScriptBuilder.Build(exactProfile);

        var capabilities = CompatibilityProbeScriptBuilder.ParseObservation(
            failedExactObservation,
            CompatibilityProbePackageKind.Exact);

        Assert.DoesNotContain(
            "openai-codex-generic-dom-probes-v1",
            exactScript,
            StringComparison.Ordinal);
        Assert.False(capabilities.Global.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.StructuralProbeFailed,
            capabilities.Global.ReasonCode);
        Assert.False(capabilities.Glass.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.StructuralProbeFailed,
            capabilities.Glass.ReasonCode);
        Assert.NotEqual(
            CompatibilityCapabilityReasonCode.AvailableFromGenericProbePackage,
            capabilities.Global.ReasonCode);
    }

    [Fact]
    public void ReviewedBandStructuralFailure_RemainsFailedWithoutGenericFallback()
    {
        const string failedObservation =
            """
            {
              "globalBackground": false,
              "regionRecognition": true,
              "glassStyle": false,
              "audio": true,
              "advancedSurfaces": false
            }
            """;
        var profile =
            BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests.GetProfile(
                new Version(26, 721, 3405, 0));
        var script = CompatibilityProbeScriptBuilder.Build(profile);

        var capabilities = CompatibilityProbeScriptBuilder.ParseObservation(
            failedObservation,
            CompatibilityProbePackageKind.ReviewedBand);

        Assert.DoesNotContain(
            "openai-codex-generic-dom-probes-v1",
            script,
            StringComparison.Ordinal);
        Assert.False(capabilities.Global.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.StructuralProbeFailed,
            capabilities.Global.ReasonCode);
        Assert.False(capabilities.Glass.IsAvailable);
        Assert.False(capabilities.Advanced.IsAvailable);
        Assert.False(capabilities.Regions.IsAvailable);
        Assert.False(capabilities.Audio.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease,
            capabilities.Regions.ReasonCode);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease,
            capabilities.Audio.ReasonCode);
    }

    [Fact]
    public void Build_UnregisteredExactVersionFailsClosedInsteadOfUsingGenericProbe()
    {
        var unknownVersionProfile =
            BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests.GetProfile(
                new Version(27, 4, 5, 6));
        var exactCapabilities =
            BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests
                .GetProfile()
                .Capabilities;
        var unregisteredExactProfile = new CodexCompatibilityProfile(
            "unregistered-exact-profile",
            unknownVersionProfile.PackageName,
            unknownVersionProfile.PackageFamilyName,
            unknownVersionProfile.PackageFullName,
            unknownVersionProfile.PackageRoot,
            unknownVersionProfile.PackageVersion,
            unknownVersionProfile.ApplicationId,
            unknownVersionProfile.ExecutableNames,
            unknownVersionProfile.PageTitleMarkers,
            unknownVersionProfile.AllowedRemotePageHosts,
            CompatibilityProbePackageKind.Exact,
            exactCapabilities);

        var exception = Assert.Throws<InvalidOperationException>(
            () => CompatibilityProbeScriptBuilder.Build(unregisteredExactProfile));

        Assert.Contains(
            "Generic fallback is prohibited",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ReviewedBandProfileOutsideRegisteredRangeFailsClosed()
    {
        var outsideBandProfile =
            BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests.GetProfile(
                new Version(26, 722, 0, 0));
        var reviewedBandCapabilities =
            BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests.GetProfile(
                new Version(26, 721, 3405, 0))
                .Capabilities;
        var invalidReviewedBandProfile = new CodexCompatibilityProfile(
            "openai-codex-26.721-reviewed-band-windows11-x64-v1",
            outsideBandProfile.PackageName,
            outsideBandProfile.PackageFamilyName,
            outsideBandProfile.PackageFullName,
            outsideBandProfile.PackageRoot,
            outsideBandProfile.PackageVersion,
            outsideBandProfile.ApplicationId,
            outsideBandProfile.ExecutableNames,
            outsideBandProfile.PageTitleMarkers,
            outsideBandProfile.AllowedRemotePageHosts,
            CompatibilityProbePackageKind.ReviewedBand,
            reviewedBandCapabilities);

        var exception = Assert.Throws<InvalidOperationException>(
            () => CompatibilityProbeScriptBuilder.Build(invalidReviewedBandProfile));

        Assert.Contains(
            "Generic fallback is prohibited",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_OwnsHomeSurfaceStylesAsGlassCapability()
    {
        var installScript = InjectionScriptBuilder.BuildInstall(
            new WallpaperInjectionOptions(
                generation: 3,
                source: new Uri("file:///C:/Wallpapers/wallpaper.png"),
                localMediaPath: @"C:\Wallpapers\wallpaper.png",
                expectedContentLength: 4096,
                WallpaperMediaKind.Image));
        const string homeRule = "[role=\"main\"]:has([data-home-ambient-suggestions])";
        var homeRuleIndex = installScript.IndexOf(homeRule, StringComparison.Ordinal);
        var glassStartIndex = installScript.LastIndexOf(
            "codex-wallpaper-glass:start",
            homeRuleIndex,
            StringComparison.Ordinal);
        var glassEndIndex = installScript.IndexOf(
            "codex-wallpaper-glass:end",
            homeRuleIndex,
            StringComparison.Ordinal);

        Assert.True(homeRuleIndex > glassStartIndex);
        Assert.True(glassEndIndex > homeRuleIndex);
    }

    [Fact]
    public void BuildCapabilityDowngrade_RemovesOwnedOptionalStyleBlocksInPlace()
    {
        var declared = BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests
            .GetProfile()
            .Capabilities;
        var degraded = declared.DowngradeWith(
            new CompatibilityCapabilities(
                declared.Global,
                declared.Regions,
                new CompatibilityCapability(
                    false,
                    CompatibilityCapabilityReasonCode.StructuralProbeFailed),
                declared.Audio,
                declared.Advanced));

        var script = InjectionScriptBuilder.BuildCapabilityDowngrade(7, degraded);

        Assert.Contains("codex-wallpaper-glass:start", script, StringComparison.Ordinal);
        Assert.Contains("codex-wallpaper-glass:end", script, StringComparison.Ordinal);
        Assert.Contains("state.style.textContent = css", script, StringComparison.Ordinal);
        Assert.Contains("state.glassEnabled = false", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresOwnedStyleDowngrade_DetectsTrueToFalseButNeverRecovery()
    {
        var declared = BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests
            .GetProfile()
            .Capabilities;
        var degraded = declared.DowngradeWith(
            new CompatibilityCapabilities(
                declared.Global,
                declared.Regions,
                declared.Glass,
                declared.Audio,
                new CompatibilityCapability(
                    false,
                    CompatibilityCapabilityReasonCode.StructuralProbeFailed)));

        Assert.True(PuppeteerWallpaperSession.RequiresOwnedStyleDowngrade(
            declared,
            degraded));
        Assert.False(PuppeteerWallpaperSession.RequiresOwnedStyleDowngrade(
            degraded,
            declared));
    }
}
