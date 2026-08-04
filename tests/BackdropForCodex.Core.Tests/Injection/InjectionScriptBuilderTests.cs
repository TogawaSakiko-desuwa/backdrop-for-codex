using BackdropForCodex.Core.Injection;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed class InjectionScriptBuilderTests
{
    [Fact]
    public void BuildInstall_IsNotExposedAsAProbeBypassingPublicApi()
    {
        var publicInstallOverloads = typeof(InjectionScriptBuilder)
            .GetMethods(System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Static)
            .Where(method => method.Name == nameof(InjectionScriptBuilder.BuildInstall))
            .ToArray();

        Assert.Empty(publicInstallOverloads);
    }

    [Fact]
    public void BuildInstall_PreparesOwnedMediaAndFileInput()
    {
        var options = CreateOptions(
            generation: 7,
            mediaKind: WallpaperMediaKind.Image,
            objectFit: WallpaperObjectFit.Contain,
            mediaOpacity: 0.8,
            glass: new GlassEffectOptions(10, 20, 30, 0.42, 18, 1.2),
            composition: new WallpaperCompositionOptions(0.25, 0.75, 0.5, 0.1));

        var script = InjectionScriptBuilder.BuildInstall(options);

        Assert.Contains($"\"rootId\":\"{InjectionScriptBuilder.RootElementId}\"", script, StringComparison.Ordinal);
        Assert.Contains($"\"styleId\":\"{InjectionScriptBuilder.StyleElementId}\"", script, StringComparison.Ordinal);
        Assert.Contains($"\"fileInputId\":\"{InjectionScriptBuilder.FileInputElementId}\"", script, StringComparison.Ordinal);
        Assert.Contains("\"mediaKind\":\"image\"", script, StringComparison.Ordinal);
        Assert.Contains("\"objectFit\":\"contain\"", script, StringComparison.Ordinal);
        Assert.Contains("\"focusX\":0.25", script, StringComparison.Ordinal);
        Assert.Contains("\"focusY\":0.75", script, StringComparison.Ordinal);
        Assert.Contains("\"darkOverlay\":0.5", script, StringComparison.Ordinal);
        Assert.Contains("\"lightOverlay\":0.1", script, StringComparison.Ordinal);
        Assert.Contains("\"expectedContentLength\":1234", script, StringComparison.Ordinal);
        Assert.Contains("\"heartbeatIntervalMs\":2000", script, StringComparison.Ordinal);
        Assert.Contains("\"leaseTimeoutMs\":10000", script, StringComparison.Ordinal);
        Assert.Contains("fileInput.type = \"file\"", script, StringComparison.Ordinal);
        Assert.Contains("fileInput.hidden = true", script, StringComparison.Ordinal);
        Assert.Contains("state.startWatchdog()", script, StringComparison.Ordinal);
        Assert.Contains("style,", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.querySelectorAll(", script, StringComparison.Ordinal);
        Assert.Contains("return fileInput;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("return { prepared: true", script, StringComparison.Ordinal);
        Assert.DoesNotContain("return { applied: true", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WallpaperObjectFit.Cover, "cover")]
    [InlineData(WallpaperObjectFit.Contain, "contain")]
    [InlineData(WallpaperObjectFit.Fill, "fill")]
    public void BuildInstall_MapsEveryObjectFitToCss(
        WallpaperObjectFit objectFit,
        string expectedCss)
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions(objectFit: objectFit));

        Assert.Contains($"\"objectFit\":\"{expectedCss}\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_ConfiguresVideoForSilentLoopingPlayback()
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions(
            generation: 3,
            mediaKind: WallpaperMediaKind.Video));

        Assert.Contains("\"mediaKind\":\"video\"", script, StringComparison.Ordinal);
        Assert.Contains("media.autoplay = true", script, StringComparison.Ordinal);
        Assert.Contains("media.loop = true", script, StringComparison.Ordinal);
        Assert.Contains("media.muted = true", script, StringComparison.Ordinal);
        Assert.Contains("media.playsInline = true", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_DoesNotSerializeSourceOrLocalPath()
    {
        var source = new Uri(
            "https://127.0.0.1:49152/media/secret-source-do-not-serialize.jpg");
        const string LocalPath = @"C:\Wallpapers\secret-path-do-not-serialize.jpg";
        var options = new WallpaperInjectionOptions(
            1,
            source,
            LocalPath,
            1234,
            WallpaperMediaKind.Image);

        var script = InjectionScriptBuilder.BuildInstall(options);

        Assert.Equal(source, options.Source);
        Assert.Equal(LocalPath, options.LocalMediaPath);
        Assert.DoesNotContain(source.AbsoluteUri, script, StringComparison.Ordinal);
        Assert.DoesNotContain(LocalPath, script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cfg.source", script, StringComparison.Ordinal);

        var activate = InjectionScriptBuilder.BuildActivateMedia(options.Generation);
        Assert.DoesNotContain(source.AbsoluteUri, activate, StringComparison.Ordinal);
        Assert.DoesNotContain(LocalPath, activate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildActivateMedia_RequiresExactlyOneExpectedSizeFileBeforeCreatingBlob()
    {
        var script = InjectionScriptBuilder.BuildActivateMedia(17);

        var countCheck = script.IndexOf("files.length !== 1", StringComparison.Ordinal);
        var sizeCheck = script.IndexOf("file.size !== state.expectedContentLength", StringComparison.Ordinal);
        var createBlob = script.IndexOf("URL.createObjectURL(file)", StringComparison.Ordinal);

        Assert.True(countCheck >= 0);
        Assert.True(sizeCheck > countCheck);
        Assert.True(createBlob > sizeCheck);
        Assert.Contains("state.cleanup(\"file-selection-invalid\")", script, StringComparison.Ordinal);
        Assert.Contains("state.cleanup(\"file-size-mismatch\")", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildActivateMedia_RequiresDecodedDimensionsBeforeReportingApplied()
    {
        var script = InjectionScriptBuilder.BuildActivateMedia(17);

        Assert.Contains("\"loadeddata\" : \"load\"", script, StringComparison.Ordinal);
        Assert.Contains("media.readyState >= media.HAVE_CURRENT_DATA", script, StringComparison.Ordinal);
        Assert.Contains("media.videoWidth > 0 && media.videoHeight > 0", script, StringComparison.Ordinal);
        Assert.Contains("media.naturalWidth > 0 && media.naturalHeight > 0", script, StringComparison.Ordinal);
        Assert.Contains("state.mediaReady = true", script, StringComparison.Ordinal);
        Assert.Contains("state.startRuntime()", script, StringComparison.Ordinal);
        Assert.Contains("state.fileInput.value = \"\"", script, StringComparison.Ordinal);
        Assert.Contains("state.fileInput.remove()", script, StringComparison.Ordinal);
        Assert.Contains("state.fileInput = null", script, StringComparison.Ordinal);
        Assert.Contains("return { applied: true", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Scripts_CleanupFailedAndRuntimeMediaAndRevokeBlobUrl()
    {
        var install = InjectionScriptBuilder.BuildInstall(CreateOptions());
        var activate = InjectionScriptBuilder.BuildActivateMedia(1);

        Assert.Contains("media.removeAttribute(\"src\")", install, StringComparison.Ordinal);
        Assert.Contains("media.load()", install, StringComparison.Ordinal);
        Assert.Contains("URL.revokeObjectURL(state.blobUrl)", install, StringComparison.Ordinal);
        Assert.Contains("state.onMediaError = () => state.cleanup(\"media-runtime-error\")", install, StringComparison.Ordinal);
        Assert.Contains("if (root.isConnected) root.remove()", install, StringComparison.Ordinal);
        Assert.Contains("if (style.isConnected) style.remove()", install, StringComparison.Ordinal);
        Assert.Contains("state.cleanup(loadResult.reason)", activate, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_LeavesMainTransparentAndUsesOnlyIntentionalGlassLayers()
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions());

        Assert.Contains("body main {", script, StringComparison.Ordinal);
        Assert.Contains("background: transparent !important", script, StringComparison.Ordinal);
        Assert.Contains("backdrop-filter: none !important", script, StringComparison.Ordinal);
        Assert.Contains("body > #root {", script, StringComparison.Ordinal);
        Assert.DoesNotContain("body > :not(#${cfg.rootId})", script, StringComparison.Ordinal);
        Assert.Contains(
            "aside:not([data-app-shell-focus-area=\"right-panel\"])",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[data-codex-wallpaper-glass]) :is(nav, header) {",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "body :is(aside, .app-header-tint",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("body :is(main, aside, nav, header", script, StringComparison.Ordinal);
        Assert.DoesNotContain("#${cfg.rootId}::after", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_LayersOnlyBodyLevelBrowserHostsAboveTheApp()
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions());
        var compactScript = string.Concat(script.Where(character => !char.IsWhiteSpace(character)));

        Assert.Contains(
            "#${cfg.rootId}{position:fixed;inset:0;z-index:0;",
            compactScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "body>#root{position:relative;z-index:1;",
            compactScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "body>[data-browser-sidebar-webview-host-root]," +
            "body>[data-browser-sidebar-webview][data-app-shell-focus-area]" +
            "{z-index:2!important;}",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[data-browser-sidebar-webview-host-root]webview",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[data-browser-sidebar-webview][data-app-shell-focus-area]webview",
            compactScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_ClearsPluginAndScheduledStickyFades()
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions());
        var compactScript = string.Concat(script.Where(character => !char.IsWhiteSpace(character)));

        foreach (var searchId in new[] { "plugins-page-search", "scheduled-page-search" })
        {
            Assert.Contains(
                "body[class~=\"sticky\"][class~=\"z-30\"]" +
                "[class~=\"bg-token-main-surface-primary\"]" +
                $":has([id=\"{searchId}\"])" +
                "{background-color:transparent!important;" +
                "-webkit-backdrop-filter:none!important;" +
                "backdrop-filter:none!important;" +
                "border-color:transparent;}",
                compactScript,
                StringComparison.Ordinal);
            Assert.Contains(
                "body[class~=\"sticky\"][class~=\"z-30\"]" +
                "[class~=\"bg-token-main-surface-primary\"]" +
                $":has([id=\"{searchId}\"])::after{{" +
                "background-image:none!important;}",
                compactScript,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            "background-image:linear-gradient(tobottom,var(--codex-wallpaper-glass),transparent)!important;",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "body[class~=\"after:bg-linear-to-b\"]" +
            "[class~=\"after:from-token-main-surface-primary\"]" +
            "[class~=\"after:to-transparent\"]::after",
            compactScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_ClearsOnlyTheActiveKeyboardShortcutsSearchSticky()
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions());
        var compactScript = string.Concat(script.Where(character => !char.IsWhiteSpace(character)));
        const string RouteEvidence =
            "body:has([class~=\"app-shell-left-panel\"]" +
            "[data-settings-panel-slug=\"keyboard-shortcuts\"][aria-current=\"page\"])";
        const string SearchSticky =
            "[class~=\"sticky\"][class~=\"z-30\"]" +
            "[class~=\"bg-token-main-surface-primary\"]:has(input[type=\"text\"])";

        Assert.Contains(
            RouteEvidence + SearchSticky +
            "{background-color:transparent!important;" +
            "-webkit-backdrop-filter:none!important;" +
            "backdrop-filter:none!important;" +
            "border-color:transparent;}",
            compactScript,
            StringComparison.Ordinal);
        Assert.Contains(
            RouteEvidence + SearchSticky +
            "::after{background-image:none!important;}",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "body" + SearchSticky + "{",
            compactScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_RemovesOnlyNestedSitesAndPullRequestStickyPseudoFades()
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions());
        var compactScript = string.Concat(script.Where(character => !char.IsWhiteSpace(character)));
        const string ClearDeclaration = "background-image: none !important;";
        var compactClearDeclaration = string.Concat(
            ClearDeclaration.Where(character => !char.IsWhiteSpace(character)));

        Assert.Contains(
            "body[class~=\"flex\"][class~=\"h-full\"][class~=\"min-h-0\"]" +
            "[class~=\"flex-col\"][class~=\"bg-token-main-surface-primary\"]" +
            ":has([id=\"appgen-site-search\"])" +
            "[class~=\"sticky\"][class~=\"z-30\"]" +
            "[class~=\"bg-token-main-surface-primary\"]" +
            ":has([id=\"appgen-site-search\"])::after{" +
            compactClearDeclaration +
            "}",
            compactScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "body[class~=\"flex\"][class~=\"h-full\"][class~=\"min-h-0\"]" +
            "[class~=\"w-full\"][class~=\"flex-col\"]" +
            "[class~=\"bg-token-main-surface-primary\"]" +
            ":has([id=\"pull-request-inbox-search\"])" +
            "[class~=\"sticky\"][class~=\"z-30\"]" +
            "[class~=\"bg-token-main-surface-primary\"]" +
            ":has([id=\"pull-request-inbox-search\"])::after{" +
            compactClearDeclaration +
            "}",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[class~=\"sticky\"]::after",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[class~=\"after:bg-linear-to-b\"]" +
            "[class~=\"after:from-token-main-surface-primary\"]" +
            "[class~=\"after:to-transparent\"]::after",
            compactScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_DoesNotEmitUnanchoredGlobalSurfaceOrGradientSelectors()
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions());
        var compactScript = string.Concat(script.Where(character => !char.IsWhiteSpace(character)));

        Assert.DoesNotContain(
            "bodydiv[class~=\"main-surface\"]{",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "body[class~=\"main-surface\"]{",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "body[class~=\"bg-token-main-surface-primary\"]{",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "body[class*=\"bg-token-\"]",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "body[class~=\"bg-gradient-to-t\"]" +
            "[class~=\"from-token-main-surface-primary\"]",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(":nth-child(", script, StringComparison.Ordinal);
        Assert.DoesNotContain(":nth-of-type(", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_UsesStableShellDataMarkersInsteadOfCssModuleClasses()
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions());

        string[] stableSelectors =
        [
            "header[data-app-shell-application-menu-bar]" +
            "[data-app-shell-header-edge-scroll]",
            "main[data-app-shell-main-surface=\"default\"]",
            "[data-app-shell-main-content-layout]" +
            "[data-app-shell-right-panel-full-width]",
            "[data-app-shell-thread-edge-divider]",
            "[data-app-shell-main-content-top-fade]",
        ];
        string[] supersededCssModuleSelectors =
        [
            ".app-header-tint",
            ".app-shell-main-content-viewport",
            ".app-shell-main-content-frame",
            ".app-shell-main-content-top-fade",
        ];

        Assert.All(
            stableSelectors,
            selector => Assert.Contains(selector, script, StringComparison.Ordinal));
        Assert.All(
            supersededCssModuleSelectors,
            selector => Assert.DoesNotContain(selector, script, StringComparison.Ordinal));
    }

    [Fact]
    public void BuildInstall_AppliesCropFocusAndAddsAnOwnedThemeAwareOverlay()
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions(
            composition: new WallpaperCompositionOptions(0.2, 0.8, 0.55, 0.12)));

        Assert.Contains(
            "cfg.objectFit === \"cover\" ? cfg.focusX * 100 : 50",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "cfg.objectFit === \"cover\" ? cfg.focusY * 100 : 50",
            script,
            StringComparison.Ordinal);
        Assert.Contains("object-position:", script, StringComparison.Ordinal);
        Assert.Contains("light-dark(", script, StringComparison.Ordinal);
        Assert.Contains(
            "--codex-wallpaper-glass: light-dark(",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "var(--color-token-main-surface-primary, rgb(255 255 255))",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            ":root:is(.dark, .electron-dark, [data-theme=\"dark\"])",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            ":root:is(.light, .electron-light, [data-theme=\"light\"])",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "--codex-wallpaper-glass: var(--codex-wallpaper-glass-dark);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "--codex-wallpaper-glass: var(--codex-wallpaper-glass-light);",
            script,
            StringComparison.Ordinal);
        Assert.Contains("overlay.dataset.codexWallpaperOverlay = \"\"", script, StringComparison.Ordinal);
        Assert.Contains("root.append(media, overlay, fileInput)", script, StringComparison.Ordinal);
        Assert.Contains(
            "#${cfg.rootId} > [data-codex-wallpaper-overlay]",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("prefers-color-scheme", script, StringComparison.Ordinal);
        Assert.DoesNotContain("MutationObserver", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_GlassesOnlyTheReviewedRightPanelShellAndAuditedContentShells()
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions());
        var normalizedScript = script.ReplaceLineEndings("\n");
        var compactScript = string.Concat(normalizedScript.Where(character => !char.IsWhiteSpace(character)));
        const string RightPanelTab =
            "body [role=\"tabpanel\"][data-app-shell-tab-panel-controller=\"right\"]";
        var compactRightPanelTab = RightPanelTab.Replace(" ", string.Empty, StringComparison.Ordinal);
        const string RightPanelShell =
            "body aside[data-app-shell-focus-area=\"right-panel\"]";
        var forcedColorsNone = script.IndexOf(
            "@media (forced-colors: none)",
            StringComparison.Ordinal);
        var forcedColorsActive = script.IndexOf(
            "@media (forced-colors: active)",
            StringComparison.Ordinal);
        var rightPanel = script.IndexOf(RightPanelShell, StringComparison.Ordinal);

        Assert.True(forcedColorsNone >= 0);
        Assert.True(rightPanel > forcedColorsNone);
        Assert.True(forcedColorsActive > rightPanel);
        Assert.Contains(RightPanelShell, normalizedScript, StringComparison.Ordinal);
        Assert.Contains(
            "> div:has([role=\"tabpanel\"][data-app-shell-tab-panel-controller=\"right\"])",
            normalizedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "> div[class~=\"bg-token-main-surface-primary\"] {",
            normalizedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{compactRightPanelTab}>[class~=\"bg-token-main-surface-primary\"]",
            compactScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "[class~=\"relative\"][class~=\"rounded-lg\"]" +
            "[class~=\"bg-token-main-surface-primary\"]" +
            ":has(:is(.markdown,[class^=\"_markdownContent_\"],[class*=\"_markdownContent_\"]))",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain($"{RightPanelTab} > :is(div, section)", script, StringComparison.Ordinal);
        Assert.DoesNotContain($"{RightPanelTab} *", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[class*=\"bg-token\"]", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "body :is(aside, .app-header-tint",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".monaco-editor", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[data-diff", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[data-popcorn", script, StringComparison.Ordinal);
        Assert.Contains("@media (forced-colors: active)", script, StringComparison.Ordinal);
        Assert.Contains("display: none !important", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_GlassesOnlyPortalHomeSuggestionCardsWithThemeAwareOpacity()
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions(
            glass: new GlassEffectOptions(opacity: 0.78, blurPixels: 18, saturation: 1.2)));
        var cappedScript = InjectionScriptBuilder.BuildInstall(CreateOptions(
            glass: new GlassEffectOptions(opacity: 0.97, blurPixels: 18, saturation: 1.2)));
        var normalizedScript = string.Join(
            '\n',
            Array.ConvertAll(
                script.ReplaceLineEndings("\n").Split('\n'),
                static line => line.TrimStart()));
        var forcedColorsBlockStart = normalizedScript.IndexOf(
            "@media (forced-colors: none) {",
            StringComparison.Ordinal);
        Assert.True(forcedColorsBlockStart >= 0);
        var nextRuleStart = normalizedScript.IndexOf(
            "\nbody [class~=\"sticky\"][class~=\"z-30\"]" +
            "[class~=\"bg-token-main-surface-primary\"]" +
            ":has([id=\"plugins-page-search\"])",
            forcedColorsBlockStart,
            StringComparison.Ordinal);
        Assert.True(nextRuleStart > forcedColorsBlockStart);
        var forcedColorsBlock = normalizedScript[forcedColorsBlockStart..nextRuleStart];

        Assert.Contains("\"glassOpacity\":0.78", script, StringComparison.Ordinal);
        Assert.Contains("\"homeSuggestionHoverOpacity\":0.86", script, StringComparison.Ordinal);
        Assert.Contains("\"homeSuggestionHoverOpacity\":1", cappedScript, StringComparison.Ordinal);
        Assert.Contains(
            """
            body [role="main"]:has([data-home-ambient-suggestions])
            section[class~="group/home-suggestions"]
            button[type="button"][aria-labelledby] {
            """.ReplaceLineEndings("\n"),
            forcedColorsBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "color-mix(in srgb, var(--color-token-main-surface-primary) " +
            "var(--codex-wallpaper-home-suggestion-opacity), transparent)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "button[type=\"button\"][aria-labelledby]:not(:disabled):is(:hover, :focus-visible) {",
            forcedColorsBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "color-mix(in srgb, var(--color-token-main-surface-primary) " +
            "var(--codex-wallpaper-home-suggestion-hover-opacity), transparent)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) " +
            "saturate(var(--codex-wallpaper-saturation))",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "backdrop-filter: blur(var(--codex-wallpaper-blur)) " +
            "saturate(var(--codex-wallpaper-saturation))",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("body main button", script, StringComparison.Ordinal);
        Assert.DoesNotContain("background-image", forcedColorsBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_AddsReadableConversationBubblesWithoutCoveringMain()
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions());

        Assert.Contains(
            "[data-response-annotation-conversation][data-response-annotation-target]",
            script,
            StringComparison.Ordinal);
        Assert.Contains("[data-user-message-bubble=\"true\"]", script, StringComparison.Ordinal);
        Assert.Contains(
            "--codex-wallpaper-conversation-inline-padding: 16px",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "padding: 12px var(--codex-wallpaper-conversation-inline-padding)",
            script,
            StringComparison.Ordinal);
        Assert.Contains("[data-local-conversation-item-target-ids]", script, StringComparison.Ordinal);
        Assert.Contains(
            "--codex-wallpaper-activity-dark: rgba(16, 18, 24, 0.58)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "--codex-wallpaper-activity-light: color-mix(",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "background-color: var(--codex-wallpaper-activity) !important",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "border: 1px solid var(--codex-wallpaper-activity-border)",
            script,
            StringComparison.Ordinal);
        Assert.Contains("padding: 4px 8px", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_GlassesUnannotatedChatGptAssistantMessagesByAccessibleStructure()
    {
        var script = InjectionScriptBuilder.BuildInstall(CreateOptions());
        var compactScript = string.Concat(script.Where(character => !char.IsWhiteSpace(character)));
        const string Fallback =
            "bodymain" +
            "[data-content-search-unit-key]>" +
            "h4[class~=\"sr-only\"][class~=\"select-none\"]+" +
            "div[class~=\"group\"][class~=\"flex\"][class~=\"min-w-0\"][class~=\"flex-col\"]" +
            ":not([data-response-annotation-target])" +
            ":has(>[data-selected-text-overlay-target])";

        Assert.Contains(
            Fallback + ",",
            compactScript,
            StringComparison.Ordinal);
        Assert.Contains(
            Fallback +
            "{padding:12pxvar(--codex-wallpaper-conversation-inline-padding);}",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bodymain[data-selected-text-overlay-target]",
            compactScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bodymain:has([data-selected-text-overlay-target])",
            compactScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHeartbeat_IsGenerationScopedAndRequiresReadyMedia()
    {
        var script = InjectionScriptBuilder.BuildHeartbeat(42);

        Assert.Contains(InjectionScriptBuilder.StateProperty, script, StringComparison.Ordinal);
        Assert.Contains("state.generation !== 42", script, StringComparison.Ordinal);
        Assert.Contains("!state.mediaReady", script, StringComparison.Ordinal);
        Assert.Contains("!state.root?.isConnected", script, StringComparison.Ordinal);
        Assert.Contains("!state.style?.isConnected", script, StringComparison.Ordinal);
        Assert.Contains("!state.media?.isConnected", script, StringComparison.Ordinal);
        Assert.Contains("!state.overlay?.isConnected", script, StringComparison.Ordinal);
        Assert.Contains(
            $"state.root.id !== \"{InjectionScriptBuilder.RootElementId}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            $"state.style.id !== \"{InjectionScriptBuilder.StyleElementId}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            $"state.root.dataset.codexWallpaperOwner !== \"{InjectionScriptBuilder.Owner}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("state.root.dataset.codexWallpaperGeneration !== \"42\"", script, StringComparison.Ordinal);
        Assert.Contains(
            $"state.style.dataset.codexWallpaperOwner !== \"{InjectionScriptBuilder.Owner}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("state.style.dataset.codexWallpaperGeneration !== \"42\"", script, StringComparison.Ordinal);
        Assert.Contains(
            $"state.media.dataset.codexWallpaperOwner !== \"{InjectionScriptBuilder.Owner}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("state.media.dataset.codexWallpaperGeneration !== \"42\"", script, StringComparison.Ordinal);
        Assert.Contains(
            $"state.overlay.dataset.codexWallpaperOwner !== \"{InjectionScriptBuilder.Owner}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("state.overlay.dataset.codexWallpaperGeneration !== \"42\"", script, StringComparison.Ordinal);
        Assert.Contains("state.media.parentElement !== state.root", script, StringComparison.Ordinal);
        Assert.Contains("state.overlay.parentElement !== state.root", script, StringComparison.Ordinal);
        Assert.Contains("!state.blobUrl", script, StringComparison.Ordinal);
        Assert.Contains("state.media.currentSrc !== state.blobUrl", script, StringComparison.Ordinal);
        Assert.Contains("state.media.error", script, StringComparison.Ordinal);
        Assert.Contains("state.media.readyState >= state.media.HAVE_CURRENT_DATA", script, StringComparison.Ordinal);
        Assert.Contains("state.media.videoWidth > 0 && state.media.videoHeight > 0", script, StringComparison.Ordinal);
        Assert.Contains("state.media.naturalWidth > 0 && state.media.naturalHeight > 0", script, StringComparison.Ordinal);
        Assert.Contains("state.lastHeartbeat = Date.now()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCleanup_IsIdempotentAndCannotRemoveNewerGeneration()
    {
        var script = InjectionScriptBuilder.BuildCleanup(9);

        Assert.Contains("state.generation > 9", script, StringComparison.Ordinal);
        Assert.Contains("state.cleanup(\"host-cleanup\")", script, StringComparison.Ordinal);
        Assert.Contains($"document.getElementById(\"{InjectionScriptBuilder.RootElementId}\")", script, StringComparison.Ordinal);
        Assert.Contains($"document.getElementById(\"{InjectionScriptBuilder.StyleElementId}\")", script, StringComparison.Ordinal);
        Assert.Contains($"document.getElementById(\"{InjectionScriptBuilder.FileInputElementId}\")", script, StringComparison.Ordinal);
        Assert.Contains("media.parentElement !== root", script, StringComparison.Ordinal);
        Assert.Contains("new Set([media.currentSrc, media.getAttribute(\"src\")])", script, StringComparison.Ordinal);
        Assert.Contains("source?.startsWith(\"blob:\")", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.querySelectorAll(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ownedNodes", script, StringComparison.Ordinal);
        Assert.Contains("return true", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Generation_MustBePositive(long generation)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InjectionScriptBuilder.BuildActivateMedia(generation));
        Assert.Throws<ArgumentOutOfRangeException>(() => InjectionScriptBuilder.BuildHeartbeat(generation));
        Assert.Throws<ArgumentOutOfRangeException>(() => InjectionScriptBuilder.BuildCleanup(generation));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WallpaperInjectionOptions(
            generation,
            new Uri("file:///C:/wallpaper.jpg"),
            @"C:\wallpaper.jpg",
            1234,
            WallpaperMediaKind.Image));
    }

    [Fact]
    public void Options_RejectJavascriptSource()
    {
        Assert.Throws<ArgumentException>(() => new WallpaperInjectionOptions(
            1,
            new Uri("javascript:alert(1)"),
            @"C:\wallpaper.jpg",
            1234,
            WallpaperMediaKind.Image));
    }

    [Fact]
    public void Options_RequireFullyQualifiedLocalPath()
    {
        Assert.Throws<ArgumentException>(() => new WallpaperInjectionOptions(
            1,
            new Uri("file:///C:/wallpaper.jpg"),
            @"wallpaper.jpg",
            1234,
            WallpaperMediaKind.Image));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_RequirePositiveExpectedContentLength(long expectedContentLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WallpaperInjectionOptions(
            1,
            new Uri("file:///C:/wallpaper.jpg"),
            @"C:\wallpaper.jpg",
            expectedContentLength,
            WallpaperMediaKind.Image));
    }

    private static WallpaperInjectionOptions CreateOptions(
        long generation = 1,
        WallpaperMediaKind mediaKind = WallpaperMediaKind.Image,
        WallpaperObjectFit objectFit = WallpaperObjectFit.Cover,
        double mediaOpacity = 1,
        GlassEffectOptions? glass = null,
        WallpaperCompositionOptions? composition = null) =>
        new(
            generation,
            new Uri("https://127.0.0.1:49152/media/wallpaper"),
            @"C:\Wallpapers\wallpaper.png",
            1234,
            mediaKind,
            objectFit,
            mediaOpacity,
            glass,
            composition);
}
