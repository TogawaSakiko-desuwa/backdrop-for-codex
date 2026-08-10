(() => {
  "use strict";
  const cfg = __BACKDROP_FOR_CODEX_PAYLOAD_JSON__;
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
  style.textContent = cfg.styleSheet;

  const root = document.createElement("div");
  root.id = cfg.rootId;
  root.setAttribute("aria-hidden", "true");
  root.dataset.codexWallpaperOwner = cfg.owner;
  root.dataset.codexWallpaperGeneration = String(cfg.generation);
  if (!cfg.glassEnabled) {
    root.dataset.codexWallpaperContrastFallback = "true";
  }

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
