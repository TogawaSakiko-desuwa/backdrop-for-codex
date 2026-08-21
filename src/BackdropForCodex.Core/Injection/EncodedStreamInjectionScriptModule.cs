using System.Text.Json;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Dynamic;

namespace BackdropForCodex.Core.Injection;

internal static class EncodedStreamInjectionScriptModule
{
    private const double RetainedHistorySeconds = 12;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string BuildPrepare(
        DynamicWallpaperInjectionOptions options,
        EncodedWallpaperStreamDescriptor descriptor,
        CompatibilityCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (options.Generation != descriptor.Generation)
        {
            throw new ArgumentException(
                "The stream descriptor and visual options must share one generation.",
                nameof(descriptor));
        }

        var payload = JsonSerializer.Serialize(
            new PreparePayload(
                InjectionOwnershipContract.Owner,
                InjectionOwnershipContract.RootElementId,
                InjectionOwnershipContract.StyleElementId,
                InjectionOwnershipContract.StateProperty,
                InjectionOwnershipContract.PendingStreamStateProperty,
                descriptor.Generation,
                descriptor.MimeType,
                descriptor.Width,
                descriptor.Height,
                descriptor.FrameRate,
                checked((int)InjectionLifecycleScriptModule.HeartbeatInterval.TotalMilliseconds),
                checked((int)InjectionLifecycleScriptModule.LeaseTimeout.TotalMilliseconds),
                InjectionInstallScriptModule.BuildStyleSheet(options, capabilities)),
            SerializerOptions);

        return $$"""
            (async () => {
              "use strict";
              const cfg = {{payload}};
              const globalObject = globalThis;
              const active = globalObject[cfg.stateProperty];
              if (active && Number.isSafeInteger(active.generation) &&
                  active.generation >= cfg.generation) {
                return {
                  prepared: false,
                  reason: active.generation === cfg.generation
                    ? "generation-already-active"
                    : "stale-generation",
                  generation: active.generation
                };
              }

              const earlierPending = globalObject[cfg.pendingStateProperty];
              if (earlierPending && Number.isSafeInteger(earlierPending.generation) &&
                  earlierPending.generation > cfg.generation) {
                return {
                  prepared: false,
                  reason: "stale-generation",
                  generation: earlierPending.generation
                };
              }
              earlierPending?.cleanup?.("superseded-pending-stream");

              if (typeof globalObject.MediaSource !== "function" ||
                  typeof globalObject.MediaSource.isTypeSupported !== "function" ||
                  !globalObject.MediaSource.isTypeSupported(cfg.mimeType)) {
                throw new DOMException(
                  "The encoded wallpaper MIME type is unavailable.",
                  "NotSupportedError");
              }

              const generationText = String(cfg.generation);
              const markOwned = element => {
                element.dataset.codexWallpaperOwner = cfg.owner;
                element.dataset.codexWallpaperGeneration = generationText;
              };
              const style = document.createElement("style");
              style.id = cfg.styleId;
              markOwned(style);
              style.textContent = cfg.styleSheet;

              const root = document.createElement("div");
              root.id = cfg.rootId;
              root.setAttribute("aria-hidden", "true");
              root.dataset.codexWallpaperStreamReady = "false";
              markOwned(root);

              const media = document.createElement("video");
              markOwned(media);
              media.autoplay = true;
              media.loop = false;
              media.muted = true;
              media.playsInline = true;
              media.preload = "auto";
              media.disablePictureInPicture = true;

              const overlay = document.createElement("div");
              overlay.setAttribute("aria-hidden", "true");
              overlay.dataset.codexWallpaperOverlay = "";
              markOwned(overlay);
              root.append(media, overlay);

              const mediaSource = new globalObject.MediaSource();
              const blobUrl = URL.createObjectURL(mediaSource);
              media.src = blobUrl;

              const state = {
                generation: cfg.generation,
                mediaKind: "video",
                width: cfg.width,
                height: cfg.height,
                frameRate: cfg.frameRate,
                lastHeartbeat: 0,
                watchdog: 0,
                hostPaused: false,
                mediaReady: false,
                blobUrl,
                cleaned: false,
                published: false,
                initializationAppended: false,
                firstKeyFrameAppended: false,
                lastSequence: -1,
                media,
                mediaSource,
                sourceBuffer: null,
                overlay,
                root,
                style,
                motionQuery: globalObject.matchMedia?.("(prefers-reduced-motion: reduce)") || null,
                onPlaybackPolicyChanged: null,
                onMediaError: null,
                cleanupReason: null,
                updatePlayback() {
                  if (!state.mediaReady) return;
                  const shouldPause = state.hostPaused || state.motionQuery?.matches;
                  if (shouldPause) {
                    media.pause();
                  } else {
                    media.play().catch(() => {});
                  }
                },
                startRuntime() {
                  if (!state.onPlaybackPolicyChanged) {
                    state.onPlaybackPolicyChanged = () => state.updatePlayback();
                    state.motionQuery?.addEventListener?.(
                      "change",
                      state.onPlaybackPolicyChanged);
                  }
                  if (!state.onMediaError) {
                    state.onMediaError = () => state.cleanup("media-runtime-error");
                    media.addEventListener("error", state.onMediaError);
                  }
                  state.lastHeartbeat = Date.now();
                  state.watchdog = setInterval(() => {
                    if (Date.now() - state.lastHeartbeat >= cfg.leaseTimeoutMs) {
                      state.cleanup("lease-expired");
                    }
                  }, cfg.heartbeatIntervalMs);
                  state.updatePlayback();
                },
                cleanup(reason) {
                  if (state.cleaned) return false;
                  state.cleaned = true;
                  state.mediaReady = false;
                  state.cleanupReason = reason || "requested";
                  if (state.watchdog) {
                    clearInterval(state.watchdog);
                    state.watchdog = 0;
                  }
                  if (state.onPlaybackPolicyChanged) {
                    state.motionQuery?.removeEventListener?.(
                      "change",
                      state.onPlaybackPolicyChanged);
                    state.onPlaybackPolicyChanged = null;
                  }
                  if (state.onMediaError) {
                    media.removeEventListener("error", state.onMediaError);
                    state.onMediaError = null;
                  }
                  try {
                    if (state.sourceBuffer?.updating) state.sourceBuffer.abort();
                  } catch {}
                  try {
                    if (state.mediaSource.readyState === "open") {
                      state.mediaSource.endOfStream();
                    }
                  } catch {}
                  media.pause();
                  media.removeAttribute("src");
                  media.load();
                  URL.revokeObjectURL(state.blobUrl);
                  if (root.isConnected) root.remove();
                  if (style.isConnected) style.remove();
                  if (globalObject[cfg.pendingStateProperty] === state) {
                    delete globalObject[cfg.pendingStateProperty];
                  }
                  if (globalObject[cfg.stateProperty] === state) {
                    delete globalObject[cfg.stateProperty];
                  }
                  return true;
                },
                publish() {
                  if (state.cleaned || state.published ||
                      !state.initializationAppended || !state.firstKeyFrameAppended) {
                    return false;
                  }
                  const current = globalObject[cfg.stateProperty];
                  if (current && Number.isSafeInteger(current.generation) &&
                      current.generation > cfg.generation) {
                    state.cleanup("stale-before-publish");
                    return false;
                  }
                  current?.cleanup?.("superseded");

                  const existingRoot = document.getElementById(cfg.rootId);
                  const existingStyle = document.getElementById(cfg.styleId);
                  const existingGeneration = Number(
                    existingRoot?.dataset.codexWallpaperGeneration || "0");
                  if (Number.isSafeInteger(existingGeneration) &&
                      existingGeneration > cfg.generation) {
                    state.cleanup("stale-owned-dom");
                    return false;
                  }
                  if (existingRoot?.dataset.codexWallpaperOwner === cfg.owner) {
                    existingRoot.remove();
                  }
                  if (existingStyle?.dataset.codexWallpaperOwner === cfg.owner) {
                    existingStyle.remove();
                  }

                  (document.head || document.documentElement).appendChild(style);
                  (document.body || document.documentElement).appendChild(root);
                  root.dataset.codexWallpaperStreamReady = "true";
                  state.published = true;
                  state.mediaReady = true;
                  globalObject[cfg.stateProperty] = state;
                  if (globalObject[cfg.pendingStateProperty] === state) {
                    delete globalObject[cfg.pendingStateProperty];
                  }
                  state.startRuntime();
                  return true;
                }
              };

              globalObject[cfg.pendingStateProperty] = state;
              try {
                await new Promise((resolve, reject) => {
                  if (mediaSource.readyState === "open") {
                    resolve();
                    return;
                  }
                  const timeout = setTimeout(() => {
                    cleanup();
                    reject(new DOMException(
                      "MediaSource did not open before the startup timeout.",
                      "TimeoutError"));
                  }, cfg.leaseTimeoutMs);
                  const cleanup = () => {
                    clearTimeout(timeout);
                    mediaSource.removeEventListener("sourceopen", onOpen);
                    mediaSource.removeEventListener("error", onError);
                  };
                  const onOpen = () => { cleanup(); resolve(); };
                  const onError = () => {
                    cleanup();
                    reject(new DOMException(
                      "MediaSource failed during startup.",
                      "InvalidStateError"));
                  };
                  mediaSource.addEventListener("sourceopen", onOpen, { once: true });
                  mediaSource.addEventListener("error", onError, { once: true });
                });
                if (globalObject[cfg.pendingStateProperty] !== state || state.cleaned) {
                  throw new DOMException(
                    "The pending wallpaper stream lost ownership.",
                    "AbortError");
                }
                state.sourceBuffer = mediaSource.addSourceBuffer(cfg.mimeType);
                return { prepared: true, reason: "prepared", generation: cfg.generation };
              } catch (error) {
                state.cleanup("stream-prepare-failed");
                throw error;
              }
            })()
            """;
    }

    internal static string BuildAppend(EncodedWallpaperSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var payload = JsonSerializer.Serialize(
            new AppendPayload(
                InjectionOwnershipContract.StateProperty,
                InjectionOwnershipContract.PendingStreamStateProperty,
                segment.Generation,
                segment.Sequence,
                segment.Kind == EncodedWallpaperSegmentKind.Initialization
                    ? "initialization"
                    : "media",
                segment.IsKeyFrame,
                Convert.ToBase64String(segment.Payload.Span),
                checked((int)InjectionLifecycleScriptModule.LeaseTimeout.TotalMilliseconds),
                RetainedHistorySeconds),
            SerializerOptions);

        return $$"""
            (async () => {
              "use strict";
              const cfg = {{payload}};
              const pending = globalThis[cfg.pendingStateProperty];
              const active = globalThis[cfg.stateProperty];
              const state = pending?.generation === cfg.generation
                ? pending
                : active?.generation === cfg.generation
                  ? active
                  : null;
              if (!state || state.cleaned) {
                return {
                  appended: false,
                  published: false,
                  reason: "generation-not-owned",
                  sequence: cfg.sequence
                };
              }

              try {
                if (!state.sourceBuffer || state.sourceBuffer.updating ||
                    cfg.sequence <= state.lastSequence) {
                  throw new DOMException(
                    "The encoded segment cannot be appended in the current state.",
                    "InvalidStateError");
                }
                if (cfg.kind === "initialization") {
                  if (state.initializationAppended || cfg.sequence !== 0 || cfg.isKeyFrame) {
                    throw new DOMException(
                      "The initialization segment violates the stream contract.",
                      "DataError");
                  }
                } else if (!state.initializationAppended ||
                           (!state.firstKeyFrameAppended && !cfg.isKeyFrame)) {
                  throw new DOMException(
                    "A key frame is required before media publication.",
                    "DataError");
                }

                const binary = atob(cfg.payloadBase64);
                const bytes = new Uint8Array(binary.length);
                for (let index = 0; index < binary.length; index += 1) {
                  bytes[index] = binary.charCodeAt(index);
                }
                const mutateSourceBuffer = (mutation, timeoutMessage) =>
                  new Promise((resolve, reject) => {
                  const sourceBuffer = state.sourceBuffer;
                  const timeout = setTimeout(() => {
                    cleanup();
                    reject(new DOMException(
                      timeoutMessage,
                      "TimeoutError"));
                  }, cfg.appendTimeoutMs);
                  const cleanup = () => {
                    clearTimeout(timeout);
                    sourceBuffer.removeEventListener("updateend", onUpdateEnd);
                    sourceBuffer.removeEventListener("error", onError);
                    sourceBuffer.removeEventListener("abort", onAbort);
                  };
                  const onUpdateEnd = () => { cleanup(); resolve(); };
                  const onError = () => {
                    cleanup();
                    reject(new DOMException(
                      "SourceBuffer rejected the encoded segment.",
                      "DataError"));
                  };
                  const onAbort = () => {
                    cleanup();
                    reject(new DOMException(
                      "SourceBuffer append was aborted.",
                      "AbortError"));
                  };
                  sourceBuffer.addEventListener("updateend", onUpdateEnd, { once: true });
                  sourceBuffer.addEventListener("error", onError, { once: true });
                  sourceBuffer.addEventListener("abort", onAbort, { once: true });
                  try {
                    mutation(sourceBuffer);
                  } catch (error) {
                    cleanup();
                    reject(error);
                  }
                });

                const trimBufferedHistory = async () => {
                  const sourceBuffer = state.sourceBuffer;
                  const currentTime = Number(state.media.currentTime);
                  if (!sourceBuffer || sourceBuffer.updating ||
                      !Number.isFinite(currentTime) ||
                      currentTime <= cfg.retainedHistorySeconds ||
                      sourceBuffer.buffered.length === 0) {
                    return false;
                  }

                  let earliestStart = Number.POSITIVE_INFINITY;
                  for (let index = 0; index < sourceBuffer.buffered.length; index += 1) {
                    earliestStart = Math.min(
                      earliestStart,
                      sourceBuffer.buffered.start(index));
                  }
                  const removeBefore = currentTime - cfg.retainedHistorySeconds;
                  if (!Number.isFinite(earliestStart) ||
                      removeBefore <= earliestStart + 0.05) {
                    return false;
                  }

                  await mutateSourceBuffer(
                    buffer => buffer.remove(earliestStart, removeBefore),
                    "SourceBuffer did not acknowledge history removal in time.");
                  return true;
                };

                await trimBufferedHistory();
                try {
                  await mutateSourceBuffer(
                    sourceBuffer => sourceBuffer.appendBuffer(bytes),
                    "SourceBuffer did not acknowledge the segment in time.");
                } catch (error) {
                  if (error?.name !== "QuotaExceededError" ||
                      !await trimBufferedHistory()) {
                    throw error;
                  }
                  await mutateSourceBuffer(
                    sourceBuffer => sourceBuffer.appendBuffer(bytes),
                    "SourceBuffer did not acknowledge the retried segment in time.");
                }
                await trimBufferedHistory();

                if (state.cleaned ||
                    (globalThis[cfg.pendingStateProperty] !== state &&
                     globalThis[cfg.stateProperty] !== state)) {
                  return {
                    appended: false,
                    published: false,
                    reason: "generation-not-owned",
                    sequence: cfg.sequence
                  };
                }
                state.lastSequence = cfg.sequence;
                if (cfg.kind === "initialization") {
                  state.initializationAppended = true;
                } else if (cfg.isKeyFrame) {
                  state.firstKeyFrameAppended = true;
                }
                const publishedNow = state.publish();
                return {
                  appended: true,
                  published: state.published,
                  reason: publishedNow ? "published" : "appended",
                  sequence: cfg.sequence
                };
              } catch (error) {
                state.cleanup("stream-append-failed");
                throw error;
              }
            })()
            """;
    }

    internal static string BuildCleanup(long generation)
    {
        InjectionLifecycleScriptModule.EnsureGeneration(generation);
        var stateKey = JsonSerializer.Serialize(InjectionOwnershipContract.StateProperty);
        var pendingKey = JsonSerializer.Serialize(
            InjectionOwnershipContract.PendingStreamStateProperty);
        var owner = JsonSerializer.Serialize(InjectionOwnershipContract.Owner);
        return $$"""
            (() => {
              "use strict";
              const generation = {{generation}};
              const generationText = String(generation);
              const owner = {{owner}};
              const pending = globalThis[{{pendingKey}}];
              const active = globalThis[{{stateKey}}];
              if (pending?.generation === generation &&
                  typeof pending.cleanup === "function") {
                pending.cleanup("host-cleanup");
              }
              if (active?.generation === generation &&
                  typeof active.cleanup === "function") {
                active.cleanup("host-cleanup");
              }

              const remainingPending = globalThis[{{pendingKey}}];
              const remainingActive = globalThis[{{stateKey}}];
              const states = [remainingPending, remainingActive]
                .filter(state => state !== null && state !== undefined);
              if (states.some(state => !Number.isSafeInteger(state.generation)) ||
                  states.some(state => state.generation <= generation)) {
                return false;
              }

              const exactOwnedDomRemains = Array.from(document.querySelectorAll(
                "[data-codex-wallpaper-owner][data-codex-wallpaper-generation]"))
                .some(node =>
                  node.dataset.codexWallpaperOwner === owner &&
                  node.dataset.codexWallpaperGeneration === generationText);
              if (exactOwnedDomRemains) {
                return false;
              }

              return true;
            })()
            """;
    }

    internal static string BuildSetPaused(long generation, bool paused)
    {
        InjectionLifecycleScriptModule.EnsureGeneration(generation);
        var stateKey = JsonSerializer.Serialize(InjectionOwnershipContract.StateProperty);
        var pausedLiteral = paused ? "true" : "false";
        return $$"""
            (() => {
              "use strict";
              const state = globalThis[{{stateKey}}];
              if (!state || state.cleaned || !state.published ||
                  state.generation !== {{generation}} ||
                  typeof state.updatePlayback !== "function") {
                return false;
              }
              state.hostPaused = {{pausedLiteral}};
              state.updatePlayback();
              return true;
            })()
            """;
    }

    private sealed record PreparePayload(
        string Owner,
        string RootId,
        string StyleId,
        string StateProperty,
        string PendingStateProperty,
        long Generation,
        string MimeType,
        int Width,
        int Height,
        double FrameRate,
        int HeartbeatIntervalMs,
        int LeaseTimeoutMs,
        string StyleSheet);

    private sealed record AppendPayload(
        string StateProperty,
        string PendingStateProperty,
        long Generation,
        long Sequence,
        string Kind,
        bool IsKeyFrame,
        string PayloadBase64,
        int AppendTimeoutMs,
        double RetainedHistorySeconds);
}
