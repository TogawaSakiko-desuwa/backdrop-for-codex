using System.Text.Json;
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

    private static readonly TimeSpan MediaLoadTimeout = LeaseTimeout;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string BuildInstall(WallpaperInjectionOptions options) =>
        BuildInstall(
            options,
            PresentationContractCatalog.CreateFullySupportedCapabilities());

    internal static string BuildInstall(
        WallpaperInjectionOptions options,
        CompatibilityCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capabilities);
        var styleCapabilities = InjectionStyleScriptModule.Resolve(capabilities);

        var payload = JsonSerializer.Serialize(
            new ScriptPayload(
                Owner,
                RootElementId,
                StyleElementId,
                FileInputElementId,
                StateProperty,
                options.Generation,
                options.ExpectedContentLength,
                options.MediaKind == WallpaperMediaKind.Video ? "video" : "image",
                InjectionMediaScriptModule.ToCss(options.ObjectFit),
                options.MediaOpacity,
                options.Composition.FocusX,
                options.Composition.FocusY,
                options.Composition.DarkOverlay,
                options.Composition.LightOverlay,
                checked((int)HeartbeatInterval.TotalMilliseconds),
                checked((int)LeaseTimeout.TotalMilliseconds),
                checked((int)MediaLoadTimeout.TotalMilliseconds),
                options.Glass.Red,
                options.Glass.Green,
                options.Glass.Blue,
                options.Glass.Opacity,
                Math.Min(options.Glass.Opacity + 0.08, 1),
                options.Glass.BlurPixels,
                options.Glass.Saturation,
                styleCapabilities.GlassEnabled,
                styleCapabilities.AdvancedSurfacesEnabled),
            SerializerOptions);
        var glassBodySelector = styleCapabilities.GlassEnabled
            ? "body"
            : "body[data-codex-wallpaper-glass-disabled]";
        var advancedBodySelector = styleCapabilities.AdvancedSurfacesEnabled
            ? "body"
            : "body[data-codex-wallpaper-advanced-disabled]";

        return $$"""
            (() => {
              "use strict";
              const cfg = {{payload}};
              const globalObject = globalThis;
              const previous = globalObject[cfg.stateProperty];

              if (previous && Number.isSafeInteger(previous.generation) &&
                  previous.generation > cfg.generation) {
                return { prepared: false, reason: "stale-generation", generation: previous.generation };
              }

              if (previous && typeof previous.cleanup === "function") {
                previous.cleanup("superseded");
              } else {
                const isOwnedNode = node => node instanceof Element &&
                  node.dataset.codexWallpaperOwner === cfg.owner &&
                  /^[1-9]\d*$/.test(node.dataset.codexWallpaperGeneration || "");
                const fallbackRoot = document.getElementById(cfg.rootId);
                const fallbackStyle = document.getElementById(cfg.styleId);
                const fallbackInput = document.getElementById(cfg.fileInputId);
                if (isOwnedNode(fallbackInput) && fallbackInput.tagName === "INPUT" &&
                    fallbackInput.type === "file") {
                  fallbackInput.value = "";
                  fallbackInput.remove();
                }
                if (isOwnedNode(fallbackRoot) && fallbackRoot.tagName === "DIV") {
                  const rootGeneration = fallbackRoot.dataset.codexWallpaperGeneration;
                  Array.from(fallbackRoot.children).forEach(media => {
                    const tagName = media.tagName?.toLowerCase();
                    if ((tagName !== "img" && tagName !== "video") ||
                        media.dataset.codexWallpaperOwner !== cfg.owner ||
                        media.dataset.codexWallpaperGeneration !== rootGeneration ||
                        media.parentElement !== fallbackRoot) {
                      return;
                    }
                    const sources = new Set([media.currentSrc, media.getAttribute("src")]);
                    if (tagName === "video") media.pause();
                    media.removeAttribute("src");
                    if (tagName === "video") media.load();
                    sources.forEach(source => {
                      if (source?.startsWith("blob:")) URL.revokeObjectURL(source);
                    });
                  });
                  fallbackRoot.remove();
                }
                if (isOwnedNode(fallbackStyle) && fallbackStyle.tagName === "STYLE") {
                  fallbackStyle.remove();
                }
              }

              const style = document.createElement("style");
              style.id = cfg.styleId;
              style.dataset.codexWallpaperOwner = cfg.owner;
              style.dataset.codexWallpaperGeneration = String(cfg.generation);
              style.textContent = `
                :root {
                  --codex-wallpaper-glass: rgba(${cfg.glassRed}, ${cfg.glassGreen}, ${cfg.glassBlue}, ${cfg.glassOpacity});
                  --codex-wallpaper-home-suggestion-opacity: ${cfg.glassOpacity * 100}%;
                  --codex-wallpaper-home-suggestion-hover-opacity: ${cfg.homeSuggestionHoverOpacity * 100}%;
                  --codex-wallpaper-blur: ${cfg.glassBlurPixels}px;
                  --codex-wallpaper-saturation: ${cfg.glassSaturation};
                  --codex-wallpaper-border: rgb(255 255 255 / 0.14);
                  --codex-wallpaper-radius: 16px;
                  --codex-wallpaper-overlay: light-dark(
                    rgb(255 255 255 / ${cfg.lightOverlay}),
                    rgb(0 0 0 / ${cfg.darkOverlay}));
                }
                :root:is(.dark, .electron-dark, [data-theme="dark"]) {
                  --codex-wallpaper-overlay: rgb(0 0 0 / ${cfg.darkOverlay});
                }
                :root:is(.light, .electron-light, [data-theme="light"]) {
                  --codex-wallpaper-overlay: rgb(255 255 255 / ${cfg.lightOverlay});
                }
                #${cfg.rootId} {
                  position: fixed;
                  inset: 0;
                  z-index: 0;
                  overflow: hidden;
                  pointer-events: none;
                  background: transparent;
                  contain: strict;
                }
                #${cfg.rootId} > img,
                #${cfg.rootId} > video {
                  position: absolute;
                  inset: 0;
                  display: block;
                  width: 100%;
                  height: 100%;
                  object-fit: ${cfg.objectFit};
                  object-position:
                    ${cfg.objectFit === "cover" ? cfg.focusX * 100 : 50}%
                    ${cfg.objectFit === "cover" ? cfg.focusY * 100 : 50}%;
                  opacity: ${cfg.mediaOpacity};
                }
                #${cfg.rootId} > [data-codex-wallpaper-overlay] {
                  position: absolute;
                  inset: 0;
                  background-color: var(--codex-wallpaper-overlay);
                  pointer-events: none;
                }
                @media (forced-colors: none) {
                  html,
                  body {
                    background: transparent !important;
                  }
                  body > #root {
                    position: relative;
                    z-index: 1;
                    background: transparent !important;
                  }
                  body main {
                    background: transparent !important;
                    -webkit-backdrop-filter: none !important;
                    backdrop-filter: none !important;
                  }
                  /* codex-wallpaper-glass:start */
                  {{glassBodySelector}} [role="main"]:has([data-home-ambient-suggestions])
                    section[class~="group/home-suggestions"]
                    button[type="button"][aria-labelledby] {
                    background-color: color-mix(in srgb, var(--color-token-main-surface-primary) var(--codex-wallpaper-home-suggestion-opacity), transparent) !important;
                    -webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                  }
                  {{glassBodySelector}} [role="main"]:has([data-home-ambient-suggestions])
                    section[class~="group/home-suggestions"]
                    button[type="button"][aria-labelledby]:not(:disabled):is(:hover, :focus-visible) {
                    background-color: color-mix(in srgb, var(--color-token-main-surface-primary) var(--codex-wallpaper-home-suggestion-hover-opacity), transparent) !important;
                    -webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                  }
                  /*
                   * Plugin browse owns an opaque sticky search shell. Glass only that
                   * reviewed shell; plugin cards and controls retain their theme surfaces.
                   */
                  {{glassBodySelector}} [class~="sticky"][class~="z-30"][class~="bg-token-main-surface-primary"]:has([id="plugins-page-search"]) {
                    background-color: var(--codex-wallpaper-glass) !important;
                    -webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    border-color: var(--codex-wallpaper-border);
                  }
                  {{glassBodySelector}} [class~="sticky"][class~="z-30"][class~="bg-token-main-surface-primary"]:has([id="plugins-page-search"])::after {
                    background-image: linear-gradient(to bottom, var(--codex-wallpaper-glass), transparent) !important;
                  }
                  /*
                   * Scheduled tasks uses the same page chrome but a distinct stable search
                   * anchor. Keep task rows and their status surfaces outside this rule.
                   */
                  {{glassBodySelector}} [class~="sticky"][class~="z-30"][class~="bg-token-main-surface-primary"]:has([id="scheduled-page-search"]) {
                    background-color: var(--codex-wallpaper-glass) !important;
                    -webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    border-color: var(--codex-wallpaper-border);
                  }
                  {{glassBodySelector}} [class~="sticky"][class~="z-30"][class~="bg-token-main-surface-primary"]:has([id="scheduled-page-search"])::after {
                    background-image: linear-gradient(to bottom, var(--codex-wallpaper-glass), transparent) !important;
                  }
                  /*
                   * Sites has an opaque route root around the shared page chrome. Glass
                   * that root once, then clear only its nested search shell.
                   */
                  {{glassBodySelector}} [class~="flex"][class~="h-full"][class~="min-h-0"][class~="flex-col"][class~="bg-token-main-surface-primary"]:has([id="appgen-site-search"]) {
                    background-color: var(--codex-wallpaper-glass) !important;
                    -webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    border-color: var(--codex-wallpaper-border);
                  }
                  {{glassBodySelector}} [class~="flex"][class~="h-full"][class~="min-h-0"][class~="flex-col"][class~="bg-token-main-surface-primary"]:has([id="appgen-site-search"])
                    [class~="sticky"][class~="z-30"][class~="bg-token-main-surface-primary"]:has([id="appgen-site-search"]) {
                    background-color: transparent !important;
                  }
                  {{glassBodySelector}} [class~="flex"][class~="h-full"][class~="min-h-0"][class~="flex-col"][class~="bg-token-main-surface-primary"]:has([id="appgen-site-search"])
                    [class~="sticky"][class~="z-30"][class~="bg-token-main-surface-primary"]:has([id="appgen-site-search"])::after {
                    background-image: none !important;
                  }
                  /*
                   * Pull requests renders an opaque list plus a persistent detail outlet
                   * inside the reviewed right-panel aside. Glass the list and the outlet's
                   * outer shell once, preserve its divider, and clear only the two full-size
                   * inner primary surfaces. Ordinary right-panel tabs fail the DetailPanel
                   * presence guard; diff, editor, code, and review cards remain surfaced.
                   */
                  {{glassBodySelector}} [class~="flex"][class~="h-full"][class~="min-h-0"][class~="w-full"][class~="flex-col"][class~="bg-token-main-surface-primary"]:has([id="pull-request-inbox-search"]),
                  {{glassBodySelector}} main:has([id="pull-request-inbox-search"])
                    aside[data-app-shell-focus-area="right-panel"]:has(
                      section[class~="h-full"][class~="min-h-0"][class~="min-w-0"][class~="bg-token-main-surface-primary"]
                      > div[class~="@container/app-shell-detail-panel"][class~="flex"][class~="h-full"][class~="min-h-0"][class~="flex-col"][class~="bg-token-main-surface-primary"]
                    )
                    > div[class~="absolute"][class~="inset-0"][class~="min-h-0"][class~="min-w-0"][class~="overflow-hidden"]
                    > div[class~="absolute"][class~="top-0"][class~="bottom-0"][class~="left-0"][class~="min-w-0"][class~="bg-token-main-surface-primary"] {
                    background-color: var(--codex-wallpaper-glass) !important;
                    -webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    border-color: var(--codex-wallpaper-border);
                  }
                  {{glassBodySelector}} main:has([id="pull-request-inbox-search"])
                    aside[data-app-shell-focus-area="right-panel"]:has(
                      section[class~="h-full"][class~="min-h-0"][class~="min-w-0"][class~="bg-token-main-surface-primary"]
                      > div[class~="@container/app-shell-detail-panel"][class~="flex"][class~="h-full"][class~="min-h-0"][class~="flex-col"][class~="bg-token-main-surface-primary"]
                    )
                    > div[class~="absolute"][class~="inset-0"][class~="min-h-0"][class~="min-w-0"][class~="overflow-hidden"]
                    > div[class~="absolute"][class~="top-0"][class~="bottom-0"][class~="left-0"][class~="min-w-0"][class~="bg-token-main-surface-primary"]
                    > div[class~="h-full"][class~="min-h-0"][class~="min-w-0"][class~="overflow-hidden"]
                    > div[class~="h-full"]
                    > section[class~="h-full"][class~="min-h-0"][class~="min-w-0"][class~="bg-token-main-surface-primary"],
                  {{glassBodySelector}} main:has([id="pull-request-inbox-search"])
                    aside[data-app-shell-focus-area="right-panel"]:has(
                      section[class~="h-full"][class~="min-h-0"][class~="min-w-0"][class~="bg-token-main-surface-primary"]
                      > div[class~="@container/app-shell-detail-panel"][class~="flex"][class~="h-full"][class~="min-h-0"][class~="flex-col"][class~="bg-token-main-surface-primary"]
                    )
                    > div[class~="absolute"][class~="inset-0"][class~="min-h-0"][class~="min-w-0"][class~="overflow-hidden"]
                    > div[class~="absolute"][class~="top-0"][class~="bottom-0"][class~="left-0"][class~="min-w-0"][class~="bg-token-main-surface-primary"]
                    > div[class~="h-full"][class~="min-h-0"][class~="min-w-0"][class~="overflow-hidden"]
                    > div[class~="h-full"]
                    > section[class~="h-full"][class~="min-h-0"][class~="min-w-0"][class~="bg-token-main-surface-primary"]
                    > div[class~="@container/app-shell-detail-panel"][class~="flex"][class~="h-full"][class~="min-h-0"][class~="flex-col"][class~="bg-token-main-surface-primary"] {
                    background-color: transparent !important;
                  }
                  {{glassBodySelector}} [class~="flex"][class~="h-full"][class~="min-h-0"][class~="w-full"][class~="flex-col"][class~="bg-token-main-surface-primary"]:has([id="pull-request-inbox-search"])
                    [class~="sticky"][class~="z-30"][class~="bg-token-main-surface-primary"]:has([id="pull-request-inbox-search"]) {
                    background-color: transparent !important;
                  }
                  {{glassBodySelector}} [class~="flex"][class~="h-full"][class~="min-h-0"][class~="w-full"][class~="flex-col"][class~="bg-token-main-surface-primary"]:has([id="pull-request-inbox-search"])
                    [class~="sticky"][class~="z-30"][class~="bg-token-main-surface-primary"]:has([id="pull-request-inbox-search"])::after {
                    background-image: none !important;
                  }
                  /*
                   * Settings now renders its right canvas as a div.main-surface. Anchor
                   * through the settings navigation and exact content slot so unrelated
                   * main surfaces and nested settings cards remain untouched.
                   */
                  {{glassBodySelector}} [class~="flex"][class~="h-full"][class~="min-h-0"]:has([class~="app-shell-left-panel"] [data-settings-panel-slug])
                    > [class~="relative"][class~="isolate"][class~="min-w-0"][class~="flex-1"][class~="overflow-visible"]
                    > div[class~="main-surface"][class~="flex"][class~="h-full"][class~="min-h-0"][class~="flex-col"] {
                    background-color: var(--codex-wallpaper-glass) !important;
                    -webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    border-color: var(--codex-wallpaper-border);
                  }
                  /* codex-wallpaper-glass:end */
                  /*
                   * The reviewed right-panel shell owns the opaque theme surface. Glass that
                   * shell once so the tab strip and every right-side detail share one layer.
                   */
                  /* codex-wallpaper-glass:start */
                  {{glassBodySelector}} aside[data-app-shell-focus-area="right-panel"]
                    > div:has([role="tabpanel"][data-app-shell-tab-panel-controller="right"])
                    > div[class~="bg-token-main-surface-primary"] {
                    background-color: var(--codex-wallpaper-glass) !important;
                    -webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    border-color: var(--codex-wallpaper-border);
                  }
                  /*
                   * Before a right-side tool is opened, the reviewed launcher has no
                   * tab-panel controller. Glass its outer shell once and clear only the
                   * primary chrome nested under the tabs root. Launcher action cards use
                   * other surface tokens and retain their native opaque backgrounds.
                   */
                  {{glassBodySelector}} aside[data-app-shell-focus-area="right-panel"]:not(:has([data-app-shell-tab-panel-controller]))
                    > div
                    > div[class~="bg-token-main-surface-primary"]:has([data-app-shell-tabs="true"]) {
                    background-color: var(--codex-wallpaper-glass) !important;
                    -webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    border-color: var(--codex-wallpaper-border);
                  }
                  {{glassBodySelector}} aside[data-app-shell-focus-area="right-panel"]
                    [data-app-shell-tabs="true"][class~="bg-token-main-surface-primary"]:has([role="tabpanel"][data-app-shell-tab-panel-controller="right"]),
                  {{glassBodySelector}} aside[data-app-shell-focus-area="right-panel"]
                    [data-app-shell-tabs="true"][class~="bg-token-main-surface-primary"]:has([role="tabpanel"][data-app-shell-tab-panel-controller="right"])
                    > [class~="bg-token-main-surface-primary"]:has([data-app-shell-tab-strip-controller="right"]) {
                    background-color: transparent !important;
                  }
                  {{glassBodySelector}} aside[data-app-shell-focus-area="right-panel"]
                    [data-app-shell-tabs="true"][class~="bg-token-main-surface-primary"]:not(:has([data-app-shell-tab-panel-controller])),
                  {{glassBodySelector}} aside[data-app-shell-focus-area="right-panel"]
                    [data-app-shell-tabs="true"]:not(:has([data-app-shell-tab-panel-controller]))
                    [class~="bg-token-main-surface-primary"] {
                    background-color: transparent !important;
                  }
                  /* codex-wallpaper-glass:end */
                  /*
                   * Clear only the audited file-layout and Markdown shells. Editor, diff,
                   * code, table, and Popcorn content surfaces keep their theme backgrounds.
                   */
                  /* codex-wallpaper-advanced:start */
                  {{advancedBodySelector}} [role="tabpanel"][data-app-shell-tab-panel-controller="right"]
                    > [class~="bg-token-main-surface-primary"],
                  {{advancedBodySelector}} [role="tabpanel"][data-app-shell-tab-panel-controller="right"]
                    [class~="relative"][class~="rounded-lg"][class~="bg-token-main-surface-primary"]:has(:is(.markdown, [class^="_markdownContent_"], [class*=" _markdownContent_"])) {
                    background-color: transparent !important;
                  }
                  /* codex-wallpaper-advanced:end */
                  /* codex-wallpaper-glass:start */
                  {{glassBodySelector}} .app-header-tint[data-app-shell-header-edge-scroll] {
                    background: transparent !important;
                    -webkit-backdrop-filter: none !important;
                    backdrop-filter: none !important;
                  }
                  {{glassBodySelector}} .app-header-tint[data-app-shell-header-edge-scroll]
                    > [data-testid="app-shell-header-context-menu-surface"] {
                    background: transparent !important;
                    -webkit-backdrop-filter: none !important;
                    backdrop-filter: none !important;
                    border-color: transparent !important;
                  }
                  {{glassBodySelector}} :is(
                    aside:not([data-app-shell-focus-area="right-panel"]),
                    .app-header-tint:not([data-app-shell-header-edge-scroll]),
                    [role="dialog"],
                    [data-codex-wallpaper-glass]) {
                    background-color: var(--codex-wallpaper-glass) !important;
                    -webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    border-color: var(--codex-wallpaper-border);
                  }
                  {{glassBodySelector}} :is(
                    aside:not([data-app-shell-focus-area="right-panel"]),
                    .app-header-tint:not([data-app-shell-header-edge-scroll]),
                    [role="dialog"],
                    [data-codex-wallpaper-glass]) :is(nav, header) {
                    background: transparent !important;
                    -webkit-backdrop-filter: none !important;
                    backdrop-filter: none !important;
                  }
                  /* codex-wallpaper-glass:end */
                  /* codex-wallpaper-advanced:start */
                  {{advancedBodySelector}} main
                    .app-shell-main-content-top-fade[data-app-shell-main-content-top-fade] {
                    background-image: none !important;
                  }
                  {{advancedBodySelector}} main .thread-scroll-container
                    [class~="bg-gradient-to-t"][class~="from-token-main-surface-primary"][class~="via-token-main-surface-primary"] {
                    background: transparent !important;
                  }
                  /*
                   * The in-progress changed-files summary is rendered above the composer
                   * in a portal with its own primary-to-transparent fade. Remove only that
                   * reviewed fade; the summary button and composer chrome stay surfaced.
                   */
                  {{advancedBodySelector}} main [data-codex-composer-root] [data-above-composer-portal]
                    > [data-in-progress-fixed-content]
                    > [class~="absolute"][class~="inset-x-0"][class~="bottom-1"][class~="flex"][class~="min-h-7"][class~="items-center"][class~="justify-center"][class~="gap-2"][class~="pb-1"]
                    > [class~="pointer-events-none"][class~="absolute"][class~="inset-x-0"][class~="-bottom-1"][class~="h-7"][class~="bg-gradient-to-t"][class~="from-token-main-surface-primary"][class~="to-transparent"] {
                    background-image: none !important;
                  }
                  {{advancedBodySelector}} main [data-response-annotation-conversation][data-response-annotation-target],
                  {{advancedBodySelector}} main [data-user-message-bubble="true"] {
                    background-color: var(--codex-wallpaper-glass) !important;
                    -webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                    border: 1px solid var(--codex-wallpaper-border);
                    border-radius: var(--codex-wallpaper-radius);
                    box-sizing: border-box;
                    box-shadow: 0 8px 28px rgb(0 0 0 / 0.18);
                  }
                  {{advancedBodySelector}} main [data-response-annotation-conversation][data-response-annotation-target] {
                    padding: 12px 16px;
                  }
                  {{advancedBodySelector}} main [data-local-conversation-item-target-ids] {
                    background-color: rgba(16, 18, 24, 0.58) !important;
                    border: 1px solid rgb(255 255 255 / 0.06);
                    border-radius: 10px;
                    box-sizing: border-box;
                    padding: 4px 8px;
                  }
                  /* codex-wallpaper-advanced:end */
                }
                @media (forced-colors: active) {
                  #${cfg.rootId} {
                    display: none !important;
                  }
                }
              `;

              const root = document.createElement("div");
              root.id = cfg.rootId;
              root.setAttribute("aria-hidden", "true");
              root.dataset.codexWallpaperOwner = cfg.owner;
              root.dataset.codexWallpaperGeneration = String(cfg.generation);

              const media = document.createElement(cfg.mediaKind === "video" ? "video" : "img");
              media.dataset.codexWallpaperOwner = cfg.owner;
              media.dataset.codexWallpaperGeneration = String(cfg.generation);
              media.draggable = false;
              if (cfg.mediaKind === "video") {
                media.autoplay = true;
                media.loop = true;
                media.muted = true;
                media.playsInline = true;
                media.preload = "auto";
                media.disablePictureInPicture = true;
              } else {
                media.alt = "";
                media.decoding = "async";
              }

              const overlay = document.createElement("div");
              overlay.setAttribute("aria-hidden", "true");
              overlay.dataset.codexWallpaperOverlay = "";
              overlay.dataset.codexWallpaperOwner = cfg.owner;
              overlay.dataset.codexWallpaperGeneration = String(cfg.generation);

              const fileInput = document.createElement("input");
              fileInput.id = cfg.fileInputId;
              fileInput.type = "file";
              fileInput.tabIndex = -1;
              fileInput.hidden = true;
              fileInput.setAttribute("aria-hidden", "true");
              fileInput.dataset.codexWallpaperOwner = cfg.owner;
              fileInput.dataset.codexWallpaperGeneration = String(cfg.generation);

              root.append(media, overlay, fileInput);
              (document.head || document.documentElement).appendChild(style);
              (document.body || document.documentElement).appendChild(root);

              const state = {
                generation: cfg.generation,
                expectedContentLength: cfg.expectedContentLength,
                mediaKind: cfg.mediaKind,
                lastHeartbeat: 0,
                watchdog: 0,
                hostPaused: false,
                mediaReady: false,
                blobUrl: null,
                cleaned: false,
                activation: 0,
                cancelActivation: null,
                media,
                overlay,
                root,
                style,
                fileInput,
                mediaLoadTimeoutMs: cfg.mediaLoadTimeoutMs,
                glassEnabled: cfg.glassEnabled,
                advancedSurfacesEnabled: cfg.advancedSurfacesEnabled,
                motionQuery: globalObject.matchMedia?.("(prefers-reduced-motion: reduce)") || null,
                onPlaybackPolicyChanged: null,
                onMediaError: null,
                cleanupReason: null,
                updatePlayback() {
                  if (cfg.mediaKind !== "video" || !state.mediaReady) return;
                  const shouldPause = state.hostPaused || document.hidden || state.motionQuery?.matches;
                  if (shouldPause) {
                    media.pause();
                  } else {
                    media.play().catch(() => {});
                  }
                },
                startWatchdog() {
                  if (!state.watchdog) {
                    state.watchdog = setInterval(() => {
                      if (Date.now() - state.lastHeartbeat >= cfg.leaseTimeoutMs) {
                        state.cleanup("lease-expired");
                      }
                    }, cfg.heartbeatIntervalMs);
                  }
                },
                startRuntime() {
                  if (!state.onPlaybackPolicyChanged) {
                    state.onPlaybackPolicyChanged = () => state.updatePlayback();
                    document.addEventListener("visibilitychange", state.onPlaybackPolicyChanged);
                    state.motionQuery?.addEventListener?.("change", state.onPlaybackPolicyChanged);
                  }
                  if (!state.onMediaError) {
                    state.onMediaError = () => state.cleanup("media-runtime-error");
                    media.addEventListener("error", state.onMediaError);
                  }
                  state.updatePlayback();
                  state.startWatchdog();
                },
                cleanup(reason) {
                  const current = globalObject[cfg.stateProperty];
                  if (state.cleaned || (current && current !== state)) {
                    return false;
                  }
                  state.cleaned = true;
                  state.mediaReady = false;
                  state.cleanupReason = reason || "requested";
                  state.activation += 1;
                  state.cancelActivation?.(state.cleanupReason);
                  state.cancelActivation = null;
                  if (state.watchdog) {
                    clearInterval(state.watchdog);
                    state.watchdog = 0;
                  }
                  if (state.onPlaybackPolicyChanged) {
                    document.removeEventListener("visibilitychange", state.onPlaybackPolicyChanged);
                    state.motionQuery?.removeEventListener?.("change", state.onPlaybackPolicyChanged);
                    state.onPlaybackPolicyChanged = null;
                  }
                  if (state.onMediaError) {
                    media.removeEventListener("error", state.onMediaError);
                    state.onMediaError = null;
                  }
                  if (cfg.mediaKind === "video") {
                    media.pause();
                  }
                  if (state.fileInput) {
                    state.fileInput.value = "";
                    if (state.fileInput.isConnected) state.fileInput.remove();
                    state.fileInput = null;
                  }
                  media.removeAttribute("src");
                  if (cfg.mediaKind === "video") {
                    media.load();
                  }
                  if (state.blobUrl) {
                    URL.revokeObjectURL(state.blobUrl);
                    state.blobUrl = null;
                  }
                  if (root.isConnected) root.remove();
                  if (style.isConnected) style.remove();
                  if (globalObject[cfg.stateProperty] === state) {
                    delete globalObject[cfg.stateProperty];
                  }
                  return true;
                }
              };

              globalObject[cfg.stateProperty] = state;
              state.lastHeartbeat = Date.now();
              state.startWatchdog();
              return fileInput;
            })()
            """;
    }

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

    private sealed record ScriptPayload(
        string Owner,
        string RootId,
        string StyleId,
        string FileInputId,
        string StateProperty,
        long Generation,
        long ExpectedContentLength,
        string MediaKind,
        string ObjectFit,
        double MediaOpacity,
        double FocusX,
        double FocusY,
        double DarkOverlay,
        double LightOverlay,
        int HeartbeatIntervalMs,
        int LeaseTimeoutMs,
        int MediaLoadTimeoutMs,
        byte GlassRed,
        byte GlassGreen,
        byte GlassBlue,
        double GlassOpacity,
        double HomeSuggestionHoverOpacity,
        double GlassBlurPixels,
        double GlassSaturation,
        bool GlassEnabled,
        bool AdvancedSurfacesEnabled);
}
