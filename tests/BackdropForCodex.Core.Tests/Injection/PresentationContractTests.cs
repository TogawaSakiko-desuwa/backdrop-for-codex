using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed class PresentationContractTests
{
    [Fact]
    public void EvidenceProbe_IsVersionIndependentAndContainsNoMachineIdentity()
    {
        var script = PresentationEvidenceScriptBuilder.Build();

        Assert.Contains("body \\u003E #root", script, StringComparison.Ordinal);
        Assert.Contains("appRoot.contains(main)", script, StringComparison.Ordinal);
        Assert.Contains(
            "\"shellHeaderSelector\":\"" +
            ".app-header-tint[data-app-shell-header-edge-scroll]\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"mainViewportSelector\":\"" +
            ".app-shell-main-content-viewport[data-app-shell-main-content-layout]\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "appRoot && appRoot.querySelector(probe.shellHeaderSelector)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "main && main.querySelector(probe.mainViewportSelector)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "shellHeader && mainViewport",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("aside", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "data-app-shell-focus-area",
            script,
            StringComparison.Ordinal);
        Assert.Contains("selector(:has(*))", script, StringComparison.Ordinal);
        Assert.DoesNotContain("packageVersion", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("packageFullName", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("26.721", script, StringComparison.Ordinal);
        Assert.DoesNotContain("http", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ReturnsOnlyTypedStructuralEvidence()
    {
        const string json =
            """
            {
              "globalStructure": true,
              "shellStructure": true,
              "backdropFilterSupported": false,
              "selectorHasSupported": true
            }
            """;

        var evidence = PresentationEvidenceScriptBuilder.Parse(json);

        Assert.True(evidence.GlobalStructure);
        Assert.True(evidence.ShellStructure);
        Assert.False(evidence.BackdropFilterSupported);
        Assert.True(evidence.SelectorHasSupported);
    }

    [Fact]
    public void Match_SameEvidenceAlwaysSelectsSameContractAndCapabilities()
    {
        var first = PresentationContractCatalog.Match(
            PresentationEvidence.FullySupported,
            finalizeBaselineFallback: false);
        var second = PresentationContractCatalog.Match(
            PresentationEvidence.FullySupported,
            finalizeBaselineFallback: false);

        Assert.True(first.IsFinalized);
        Assert.Equal(ContractMatchState.Matched, first.Snapshot.MatchState);
        Assert.Equal(PresentationContractCatalog.CodexShellId, first.Snapshot.ActiveContractId);
        Assert.Equal(first, second);
        Assert.True(first.Capabilities.Global.IsAvailable);
        Assert.True(first.Capabilities.Glass.IsAvailable);
        Assert.True(first.Capabilities.Advanced.IsAvailable);
    }

    [Fact]
    public void ContractSelectionSurfaceCannotReceivePackageIdentityOrVersion()
    {
        var selectionMethods = typeof(PresentationContractCatalog)
            .GetMethods(
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic)
            .Where(method => method.Name is "Match" or "Observe")
            .ToArray();

        Assert.NotEmpty(selectionMethods);
        Assert.All(
            selectionMethods.SelectMany(method => method.GetParameters()),
            parameter =>
            {
                Assert.NotEqual(typeof(Version), parameter.ParameterType);
                Assert.NotEqual(typeof(VerifiedCodexIdentity), parameter.ParameterType);
            });
    }

    [Fact]
    public void Match_NoAdvancedContractWaitsThenUsesGlobalBaseline()
    {
        var evidence = PresentationEvidence.FullySupported with
        {
            ShellStructure = false,
        };

        var pending = PresentationContractCatalog.Match(
            evidence,
            finalizeBaselineFallback: false);
        var finalized = PresentationContractCatalog.Match(
            evidence,
            finalizeBaselineFallback: true);

        Assert.False(pending.IsFinalized);
        Assert.True(finalized.IsFinalized);
        Assert.Equal(
            ContractMatchState.NoMatchUsingGlobalBaseline,
            finalized.Snapshot.MatchState);
        Assert.Equal(
            PresentationContractCatalog.GlobalBaselineId,
            finalized.Snapshot.ActiveContractId);
        Assert.True(finalized.Capabilities.Global.IsAvailable);
        Assert.False(finalized.Capabilities.Glass.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.NoMatchingPresentationContract,
            finalized.Capabilities.Glass.ReasonCode);
    }

    [Fact]
    public void Match_MultipleAdvancedContractsNeverUsesRegistrationOrderAsTieBreaker()
    {
        PresentationContract[] contracts =
        [
            new("first-shell", requiresShellStructure: true),
            new("second-shell", requiresShellStructure: true),
        ];

        var decision = PresentationContractCatalog.Match(
            PresentationEvidence.FullySupported,
            finalizeBaselineFallback: true,
            contracts);

        Assert.True(decision.IsFinalized);
        Assert.Equal(
            ContractMatchState.AmbiguousUsingGlobalBaseline,
            decision.Snapshot.MatchState);
        Assert.Equal(
            PresentationContractCatalog.GlobalBaselineId,
            decision.Snapshot.ActiveContractId);
        Assert.True(decision.Capabilities.Global.IsAvailable);
        Assert.False(decision.Capabilities.Advanced.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.AmbiguousPresentationContract,
            decision.Capabilities.Advanced.ReasonCode);
    }

    [Fact]
    public void Match_GlobalBaselineFailureNeverSelectsOrInjects()
    {
        var decision = PresentationContractCatalog.Match(
            PresentationEvidence.Unavailable,
            finalizeBaselineFallback: true);

        Assert.True(decision.IsFinalized);
        Assert.Null(decision.Snapshot.ActiveContractId);
        Assert.Equal(
            ContractMatchState.GlobalBaselineFailed,
            decision.Snapshot.MatchState);
        Assert.False(decision.Capabilities.Global.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.StructuralProbeFailed,
            decision.Capabilities.Global.ReasonCode);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease,
            decision.Capabilities.Regions.ReasonCode);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.StructuralProbeFailed,
            decision.Capabilities.Glass.ReasonCode);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease,
            decision.Capabilities.Audio.ReasonCode);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.StructuralProbeFailed,
            decision.Capabilities.Advanced.ReasonCode);
    }

    [Fact]
    public void Match_GlobalBaselineFailureWaitsUntilFallbackIsFinalized()
    {
        var pending = PresentationContractCatalog.Match(
            PresentationEvidence.Unavailable,
            finalizeBaselineFallback: false);

        Assert.False(pending.IsFinalized);
        Assert.Equal(
            ContractMatchState.GlobalBaselineFailed,
            pending.Snapshot.MatchState);
        Assert.False(pending.Capabilities.Global.IsAvailable);
    }

    [Fact]
    public void Observe_IndependentlyDegradesGlassPlatformEvidence()
    {
        var selection = PresentationContractCatalog.Match(
            PresentationEvidence.FullySupported,
            finalizeBaselineFallback: false);
        var degraded = PresentationContractCatalog.Observe(
            selection.Snapshot,
            PresentationEvidence.FullySupported with
            {
                BackdropFilterSupported = false,
            });

        Assert.True(degraded.Global.IsAvailable);
        Assert.False(degraded.Glass.IsAvailable);
        Assert.True(degraded.Advanced.IsAvailable);
    }

    [Fact]
    public void Observe_SelectorSupportDegradesOnlyDependentShellCapabilities()
    {
        var selection = PresentationContractCatalog.Match(
            PresentationEvidence.FullySupported,
            finalizeBaselineFallback: false);
        var degraded = PresentationContractCatalog.Observe(
            selection.Snapshot,
            PresentationEvidence.FullySupported with
            {
                SelectorHasSupported = false,
            });

        Assert.True(degraded.Global.IsAvailable);
        Assert.False(degraded.Glass.IsAvailable);
        Assert.False(degraded.Advanced.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease,
            degraded.Regions.ReasonCode);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease,
            degraded.Audio.ReasonCode);
    }

    [Fact]
    public void CapabilityRejectsAvailabilityWithADisabledReason()
    {
        Assert.Throws<ArgumentException>(
            () => new CompatibilityCapability(
                isAvailable: true,
                CompatibilityCapabilityReasonCode.StructuralProbeFailed));
    }

    [Fact]
    public void ContractState_NeverSwitchesWithinGeneration()
    {
        var state = new PresentationContractState();
        var selected = state.Select(
            PresentationEvidence.FullySupported,
            finalizeBaselineFallback: false);
        var lostShell = state.Select(
            PresentationEvidence.FullySupported with
            {
                ShellStructure = false,
            },
            finalizeBaselineFallback: true);

        Assert.True(selected.IsFinalized);
        Assert.Equal(selected.Snapshot, state.Current);
        Assert.Equal(selected.Snapshot, lostShell.Snapshot);
        Assert.False(lostShell.Capabilities.Glass.IsAvailable);
        Assert.False(lostShell.Capabilities.Advanced.IsAvailable);
    }

    [Fact]
    public void BuildInstall_DisablesOnlyFailedStyleCapabilities()
    {
        var declared = PresentationContractCatalog.CreateFullySupportedCapabilities();
        var degraded = declared.DowngradeWith(
            new CompatibilityCapabilities(
                declared.Global,
                declared.Regions,
                CompatibilityCapability.Disabled(
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
    public void BuildInstall_OwnsRouteStylesAsGlassAndChangedFilesAsAdvanced()
    {
        var options = new WallpaperInjectionOptions(
            generation: 3,
            source: new Uri("file:///C:/Wallpapers/wallpaper.png"),
            localMediaPath: @"C:\Wallpapers\wallpaper.png",
            expectedContentLength: 4096,
            WallpaperMediaKind.Image);
        var declared = PresentationContractCatalog.CreateFullySupportedCapabilities();
        var glassDisabled = declared.DowngradeWith(
            new CompatibilityCapabilities(
                declared.Global,
                declared.Regions,
                CompatibilityCapability.Disabled(
                    CompatibilityCapabilityReasonCode.StructuralProbeFailed),
                declared.Audio,
                declared.Advanced));
        var advancedDisabled = declared.DowngradeWith(
            new CompatibilityCapabilities(
                declared.Global,
                declared.Regions,
                declared.Glass,
                declared.Audio,
                CompatibilityCapability.Disabled(
                    CompatibilityCapabilityReasonCode.StructuralProbeFailed)));
        var installScript = InjectionScriptBuilder.BuildInstall(options, declared);
        var degradedScript = InjectionScriptBuilder.BuildInstall(options, glassDisabled);
        var advancedDegradedScript = InjectionScriptBuilder.BuildInstall(
            options,
            advancedDisabled);
        string[] glassRuleAnchors =
        [
            "plugins-page-search",
            "scheduled-page-search",
            "appgen-site-search",
            "pull-request-inbox-search",
            "data-settings-panel-slug",
        ];

        foreach (var anchor in glassRuleAnchors)
        {
            AssertEveryOccurrenceIsInsideOwnedStyleBlocks(
                installScript,
                anchor,
                "glass");
        }

        AssertEveryOccurrenceIsInsideOwnedStyleBlocks(
            installScript,
            "[data-above-composer-portal]",
            "advanced");

        var baseline = RemoveOwnedStyleBlocks(
            installScript,
            "glass",
            "advanced");
        Assert.All(
            glassRuleAnchors,
            anchor => Assert.DoesNotContain(
                anchor,
                baseline,
                StringComparison.Ordinal));

        var degradedGlassStyles = string.Join(
            "\n",
            ExtractOwnedStyleBlocks(degradedScript, "glass"));
        foreach (var anchor in glassRuleAnchors)
        {
            AssertEveryOccurrenceUsesGuard(
                degradedGlassStyles,
                anchor,
                "body[data-codex-wallpaper-glass-disabled]");
        }

        const string ChangedFilesRule =
            "body main [data-codex-composer-root] [data-above-composer-portal]";
        Assert.Contains(ChangedFilesRule, degradedScript, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "body[data-codex-wallpaper-glass-disabled] main " +
            "[data-codex-composer-root] [data-above-composer-portal]",
            degradedScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "body[data-codex-wallpaper-advanced-disabled] main " +
            "[data-codex-composer-root] [data-above-composer-portal]",
            degradedScript,
            StringComparison.Ordinal);

        var degradedAdvancedStyles = string.Join(
            "\n",
            ExtractOwnedStyleBlocks(advancedDegradedScript, "advanced"));
        AssertEveryOccurrenceUsesGuard(
            degradedAdvancedStyles,
            "[data-above-composer-portal]",
            "body[data-codex-wallpaper-advanced-disabled]");
    }

    [Fact]
    public void BuildCapabilityDowngrade_RemovesOwnedOptionalStyleBlocksInPlace()
    {
        var declared = PresentationContractCatalog.CreateFullySupportedCapabilities();
        var degraded = declared.DowngradeWith(
            new CompatibilityCapabilities(
                declared.Global,
                declared.Regions,
                CompatibilityCapability.Disabled(
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
        var declared = PresentationContractCatalog.CreateFullySupportedCapabilities();
        var degraded = declared.DowngradeWith(
            new CompatibilityCapabilities(
                declared.Global,
                declared.Regions,
                declared.Glass,
                declared.Audio,
                CompatibilityCapability.Disabled(
                    CompatibilityCapabilityReasonCode.StructuralProbeFailed)));

        Assert.True(PuppeteerWallpaperSession.RequiresOwnedStyleDowngrade(
            declared,
            degraded));
        Assert.False(PuppeteerWallpaperSession.RequiresOwnedStyleDowngrade(
            degraded,
            declared));
    }

    private static void AssertEveryOccurrenceIsInsideOwnedStyleBlocks(
        string source,
        string token,
        string capability)
    {
        var sourceCount = CountOccurrences(source, token);
        var ownedCount = ExtractOwnedStyleBlocks(source, capability)
            .Sum(block => CountOccurrences(block, token));

        Assert.True(sourceCount > 0, $"Expected '{token}' in the generated script.");
        Assert.Equal(sourceCount, ownedCount);
    }

    private static void AssertEveryOccurrenceUsesGuard(
        string source,
        string token,
        string guard)
    {
        var searchIndex = 0;
        var occurrenceCount = 0;
        while (true)
        {
            var occurrenceIndex = source.IndexOf(
                token,
                searchIndex,
                StringComparison.Ordinal);
            if (occurrenceIndex < 0)
            {
                break;
            }

            var precedingRuleEnd = source.LastIndexOf('}', occurrenceIndex);
            var selectorStart = precedingRuleEnd >= 0
                ? precedingRuleEnd + 1
                : 0;
            var declarationStart = source.IndexOf('{', selectorStart);
            Assert.True(
                declarationStart > occurrenceIndex,
                $"Expected '{token}' to occur in a selector.");
            var selectorGroup = source[selectorStart..declarationStart];
            var matchingSelector = ExtractTopLevelSelectorAt(
                selectorGroup,
                occurrenceIndex - selectorStart);
            Assert.Contains(
                guard,
                matchingSelector,
                StringComparison.Ordinal);
            occurrenceCount++;
            searchIndex = occurrenceIndex + token.Length;
        }

        Assert.True(occurrenceCount > 0, $"Expected '{token}' in the owned style block.");
    }

    private static string ExtractTopLevelSelectorAt(string selectorGroup, int offset)
    {
        Assert.InRange(offset, 0, selectorGroup.Length - 1);
        var segmentStart = 0;
        var segmentEnd = selectorGroup.Length;
        var parentheses = 0;
        var brackets = 0;
        var quote = '\0';

        for (var index = 0; index < selectorGroup.Length; index++)
        {
            var character = selectorGroup[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            switch (character)
            {
                case '(':
                    parentheses++;
                    break;
                case ')':
                    parentheses--;
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets--;
                    break;
                case ',' when parentheses == 0 && brackets == 0:
                    if (offset < index)
                    {
                        segmentEnd = index;
                        index = selectorGroup.Length;
                    }
                    else
                    {
                        segmentStart = index + 1;
                    }

                    break;
            }
        }

        Assert.InRange(offset, segmentStart, segmentEnd - 1);
        return selectorGroup[segmentStart..segmentEnd].Trim();
    }

    private static List<string> ExtractOwnedStyleBlocks(
        string source,
        string capability)
    {
        var startMarker = $"/* codex-wallpaper-{capability}:start */";
        var endMarker = $"/* codex-wallpaper-{capability}:end */";
        var blocks = new List<string>();
        var searchIndex = 0;

        while (true)
        {
            var startIndex = source.IndexOf(
                startMarker,
                searchIndex,
                StringComparison.Ordinal);
            if (startIndex < 0)
            {
                break;
            }

            var contentStart = startIndex + startMarker.Length;
            var endIndex = source.IndexOf(
                endMarker,
                contentStart,
                StringComparison.Ordinal);
            Assert.True(endIndex >= 0, $"Missing end marker for '{capability}'.");
            blocks.Add(source[contentStart..endIndex]);
            searchIndex = endIndex + endMarker.Length;
        }

        Assert.NotEmpty(blocks);
        return blocks;
    }

    private static string RemoveOwnedStyleBlocks(
        string source,
        params string[] capabilities)
    {
        var result = source;
        foreach (var capability in capabilities)
        {
            var startMarker = $"/* codex-wallpaper-{capability}:start */";
            var endMarker = $"/* codex-wallpaper-{capability}:end */";
            while (true)
            {
                var startIndex = result.IndexOf(
                    startMarker,
                    StringComparison.Ordinal);
                if (startIndex < 0)
                {
                    break;
                }

                var endIndex = result.IndexOf(
                    endMarker,
                    startIndex + startMarker.Length,
                    StringComparison.Ordinal);
                Assert.True(endIndex >= 0, $"Missing end marker for '{capability}'.");
                result = string.Concat(
                    result.AsSpan(0, startIndex),
                    result.AsSpan(endIndex + endMarker.Length));
            }
        }

        return result;
    }

    private static int CountOccurrences(string source, string token)
    {
        var count = 0;
        var searchIndex = 0;
        while (true)
        {
            var occurrenceIndex = source.IndexOf(
                token,
                searchIndex,
                StringComparison.Ordinal);
            if (occurrenceIndex < 0)
            {
                return count;
            }

            count++;
            searchIndex = occurrenceIndex + token.Length;
        }
    }
}
