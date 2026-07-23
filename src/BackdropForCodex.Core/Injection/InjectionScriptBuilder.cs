using System.Text.Json;

namespace BackdropForCodex.Core.Injection;

/// <summary>
/// Produces self-contained expressions suitable for Runtime.evaluate or PuppeteerSharp's
/// EvaluateExpressionAsync. The expressions own only nodes marked with this component's owner id.
/// </summary>
public static class InjectionScriptBuilder
{
    // Stable page-cleanup ABI shared with earlier local builds. These identifiers must not
    // follow display branding or an older process may leave owned DOM resources behind.
    public const string Owner = "codex-wallpaper";
    public const string RootElementId = "codex-wallpaper-owned-root";
    public const string StyleElementId = "codex-wallpaper-owned-style";
    public const string FileInputElementId = "codex-wallpaper-owned-file-input";
    public const string StateProperty = "__codexWallpaperOwnedState_v1";

    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan MediaLoadTimeout = LeaseTimeout;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string BuildInstall(WallpaperInjectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
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
                ToCss(options.ObjectFit),
                options.MediaOpacity,
                checked((int)HeartbeatInterval.TotalMilliseconds),
                checked((int)LeaseTimeout.TotalMilliseconds),
                checked((int)MediaLoadTimeout.TotalMilliseconds),
                options.Glass.Red,
                options.Glass.Green,
                options.Glass.Blue,
                options.Glass.Opacity,
                options.Glass.BlurPixels,
                options.Glass.Saturation),
            SerializerOptions);

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
                  --codex-wallpaper-blur: ${cfg.glassBlurPixels}px;
                  --codex-wallpaper-saturation: ${cfg.glassSaturation};
                  --codex-wallpaper-border: rgb(255 255 255 / 0.14);
                  --codex-wallpaper-radius: 16px;
                }
                html,
                body {
                  background: transparent !important;
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
                  opacity: ${cfg.mediaOpacity};
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
                body :is(aside, .app-header-tint, [role="dialog"], [data-codex-wallpaper-glass]) {
                  background-color: var(--codex-wallpaper-glass) !important;
                  -webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                  backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                  border-color: var(--codex-wallpaper-border);
                }
                body :is(aside, .app-header-tint, [role="dialog"], [data-codex-wallpaper-glass]) :is(nav, header) {
                  background: transparent !important;
                  -webkit-backdrop-filter: none !important;
                  backdrop-filter: none !important;
                }
                body main [data-response-annotation-conversation][data-response-annotation-target],
                body main [data-user-message-bubble="true"] {
                  background-color: var(--codex-wallpaper-glass) !important;
                  -webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                  backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation));
                  border: 1px solid var(--codex-wallpaper-border);
                  border-radius: var(--codex-wallpaper-radius);
                  box-sizing: border-box;
                  box-shadow: 0 8px 28px rgb(0 0 0 / 0.18);
                }
                body main [data-response-annotation-conversation][data-response-annotation-target] {
                  padding: 12px 16px;
                }
                body main [data-local-conversation-item-target-ids] {
                  background-color: rgba(16, 18, 24, 0.58) !important;
                  border: 1px solid rgb(255 255 255 / 0.06);
                  border-radius: 10px;
                  box-sizing: border-box;
                  padding: 4px 8px;
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

              const fileInput = document.createElement("input");
              fileInput.id = cfg.fileInputId;
              fileInput.type = "file";
              fileInput.tabIndex = -1;
              fileInput.hidden = true;
              fileInput.setAttribute("aria-hidden", "true");
              fileInput.dataset.codexWallpaperOwner = cfg.owner;
              fileInput.dataset.codexWallpaperGeneration = String(cfg.generation);

              root.append(media, fileInput);
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
                root,
                style,
                fileInput,
                mediaLoadTimeoutMs: cfg.mediaLoadTimeoutMs,
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
              return { prepared: true, generation: cfg.generation, mediaKind: cfg.mediaKind };
            })()
            """;
    }

    public static string BuildActivateMedia(long generation)
    {
        EnsureGeneration(generation);
        return $$"""
            (async () => {
              "use strict";
              const state = globalThis[{{JsonSerializer.Serialize(StateProperty)}}];
              if (!state || state.generation !== {{generation}} || state.cleaned) {
                return { applied: false, reason: "state-missing", generation: {{generation}} };
              }
              if (state.mediaReady && state.blobUrl) {
                return { applied: true, generation: {{generation}}, mediaKind: state.mediaKind };
              }

              const files = state.fileInput?.files;
              if (!files || files.length !== 1) {
                state.cleanup("file-selection-invalid");
                return { applied: false, reason: "file-selection-invalid", generation: {{generation}} };
              }
              const file = files[0];
              if (file.size !== state.expectedContentLength) {
                state.cleanup("file-size-mismatch");
                return { applied: false, reason: "file-size-mismatch", generation: {{generation}} };
              }

              const media = state.media;
              const activation = ++state.activation;
              state.mediaReady = false;
              let blobUrl;
              try {
                blobUrl = URL.createObjectURL(file);
              } catch {
                state.cleanup("blob-url-error");
                return { applied: false, reason: "blob-url-error", generation: {{generation}} };
              }
              state.blobUrl = blobUrl;
              state.fileInput.value = "";
              state.fileInput.remove();
              state.fileInput = null;

              if (state.mediaKind === "video") {
                media.pause();
              }
              media.removeAttribute("src");
              if (state.mediaKind === "video") {
                media.load();
              }

              const loadResult = await new Promise(resolve => {
                let settled = false;
                let timeout = 0;
                const ready = () => state.mediaKind === "video"
                  ? media.readyState >= media.HAVE_CURRENT_DATA &&
                    media.videoWidth > 0 && media.videoHeight > 0
                  : media.naturalWidth > 0 && media.naturalHeight > 0;
                const finish = (ok, reason) => {
                  if (settled) return;
                  settled = true;
                  if (timeout) clearTimeout(timeout);
                  media.removeEventListener(
                    state.mediaKind === "video" ? "loadeddata" : "load",
                    onLoaded);
                  media.removeEventListener("error", onError);
                  if (state.cancelActivation === cancelActivation) {
                    state.cancelActivation = null;
                  }
                  resolve({ ok, reason });
                };
                const onLoaded = () => finish(ready(), ready() ? null : "media-dimensions-invalid");
                const onError = () => finish(false, "media-load-error");
                const cancelActivation = reason => finish(false, reason || "activation-cancelled");
                state.cancelActivation = cancelActivation;
                media.addEventListener(
                  state.mediaKind === "video" ? "loadeddata" : "load",
                  onLoaded);
                media.addEventListener("error", onError);
                timeout = setTimeout(
                  () => finish(false, "media-load-timeout"),
                  state.mediaLoadTimeoutMs);
                media.src = blobUrl;
                if (state.mediaKind === "video") {
                  media.load();
                }
              });

              if (!loadResult.ok) {
                if (globalThis[{{JsonSerializer.Serialize(StateProperty)}}] === state &&
                    state.activation === activation && !state.cleaned) {
                  state.cleanup(loadResult.reason);
                }
                return { applied: false, reason: loadResult.reason, generation: {{generation}} };
              }

              if (globalThis[{{JsonSerializer.Serialize(StateProperty)}}] !== state ||
                  state.activation !== activation || state.cleaned || state.blobUrl !== blobUrl) {
                return { applied: false, reason: "activation-superseded", generation: {{generation}} };
              }

              state.mediaReady = true;
              state.lastHeartbeat = Date.now();
              state.startRuntime();
              return { applied: true, generation: {{generation}}, mediaKind: state.mediaKind };
            })()
            """;
    }

    public static string BuildHeartbeat(long generation)
    {
        EnsureGeneration(generation);
        return $$"""
            (() => {
              "use strict";
              const state = globalThis[{{JsonSerializer.Serialize(StateProperty)}}];
              if (!state || state.generation !== {{generation}} || !state.mediaReady ||
                  !state.root?.isConnected || !state.style?.isConnected ||
                  !state.media?.isConnected ||
                  state.root.id !== {{JsonSerializer.Serialize(RootElementId)}} ||
                  state.style.id !== {{JsonSerializer.Serialize(StyleElementId)}} ||
                  document.getElementById({{JsonSerializer.Serialize(RootElementId)}}) !== state.root ||
                  document.getElementById({{JsonSerializer.Serialize(StyleElementId)}}) !== state.style ||
                  state.root.dataset.codexWallpaperOwner !== {{JsonSerializer.Serialize(Owner)}} ||
                  state.root.dataset.codexWallpaperGeneration !== {{JsonSerializer.Serialize(generation.ToString(System.Globalization.CultureInfo.InvariantCulture))}} ||
                  state.style.dataset.codexWallpaperOwner !== {{JsonSerializer.Serialize(Owner)}} ||
                  state.style.dataset.codexWallpaperGeneration !== {{JsonSerializer.Serialize(generation.ToString(System.Globalization.CultureInfo.InvariantCulture))}} ||
                  state.media.dataset.codexWallpaperOwner !== {{JsonSerializer.Serialize(Owner)}} ||
                  state.media.dataset.codexWallpaperGeneration !== {{JsonSerializer.Serialize(generation.ToString(System.Globalization.CultureInfo.InvariantCulture))}} ||
                  state.media.parentElement !== state.root || !state.blobUrl ||
                  state.media.currentSrc !== state.blobUrl || state.media.error) {
                return false;
              }
              const dimensionsReady = state.mediaKind === "video"
                ? state.media.readyState >= state.media.HAVE_CURRENT_DATA &&
                  state.media.videoWidth > 0 && state.media.videoHeight > 0
                : state.media.naturalWidth > 0 && state.media.naturalHeight > 0;
              if (!dimensionsReady) {
                return false;
              }
              state.lastHeartbeat = Date.now();
              return true;
            })()
            """;
    }

    public static string BuildSetPaused(long generation, bool paused)
    {
        EnsureGeneration(generation);
        return $$"""
            (() => {
              "use strict";
              const state = globalThis[{{JsonSerializer.Serialize(StateProperty)}}];
              if (!state || state.generation !== {{generation}}) {
                return false;
              }
              state.hostPaused = {{(paused ? "true" : "false")}};
              state.updatePlayback?.();
              return true;
            })()
            """;
    }

    public static string BuildCleanup(long generation)
    {
        EnsureGeneration(generation);
        return $$"""
            (() => {
              "use strict";
              const key = {{JsonSerializer.Serialize(StateProperty)}};
              const owner = {{JsonSerializer.Serialize(Owner)}};
              const generation = {{JsonSerializer.Serialize(generation.ToString(System.Globalization.CultureInfo.InvariantCulture))}};
              const state = globalThis[key];
              if (state && state.generation > {{generation}}) {
                return false;
              }
              if (state && typeof state.cleanup === "function") {
                return state.cleanup("host-cleanup");
              }
              const isExactOwned = (node, id, tagName) =>
                node?.id === id && node.tagName === tagName &&
                node.dataset.codexWallpaperOwner === owner &&
                node.dataset.codexWallpaperGeneration === generation;
              const root = document.getElementById({{JsonSerializer.Serialize(RootElementId)}});
              const style = document.getElementById({{JsonSerializer.Serialize(StyleElementId)}});
              const fileInput = document.getElementById({{JsonSerializer.Serialize(FileInputElementId)}});
              if (isExactOwned(fileInput, {{JsonSerializer.Serialize(FileInputElementId)}}, "INPUT") &&
                  fileInput.type === "file") {
                fileInput.value = "";
                fileInput.remove();
              }
              if (isExactOwned(root, {{JsonSerializer.Serialize(RootElementId)}}, "DIV")) {
                Array.from(root.children).forEach(media => {
                  const tagName = media.tagName?.toLowerCase();
                  if ((tagName !== "img" && tagName !== "video") ||
                      media.parentElement !== root ||
                      media.dataset.codexWallpaperOwner !== owner ||
                      media.dataset.codexWallpaperGeneration !== generation) {
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
                root.remove();
              }
              if (isExactOwned(style, {{JsonSerializer.Serialize(StyleElementId)}}, "STYLE")) {
                style.remove();
              }
              if (globalThis[key] === state) delete globalThis[key];
              return true;
            })()
            """;
    }

    private static string ToCss(WallpaperObjectFit objectFit) => objectFit switch
    {
        WallpaperObjectFit.Cover => "cover",
        WallpaperObjectFit.Contain => "contain",
        WallpaperObjectFit.Fill => "fill",
        _ => throw new ArgumentOutOfRangeException(nameof(objectFit)),
    };

    private static void EnsureGeneration(long generation)
    {
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), "Generation must be positive.");
        }
    }

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
        int HeartbeatIntervalMs,
        int LeaseTimeoutMs,
        int MediaLoadTimeoutMs,
        byte GlassRed,
        byte GlassGreen,
        byte GlassBlue,
        double GlassOpacity,
        double GlassBlurPixels,
        double GlassSaturation);
}
