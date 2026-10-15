namespace BackdropForCodex.Core.Injection;

/// <summary>
/// Completes the verified file-input handoff inside the page without hosting the selected media
/// over HTTP.
/// </summary>
internal static class InjectionMediaScriptModule
{
    // The expected byte length rejects a selection that no longer matches the prepared metadata.
    // The activation counter prevents an older asynchronous load from claiming readiness later.
    internal static string BuildActivateMedia(long generation)
    {
        EnsureGeneration(generation);
        return $$"""
            (async () => {
              "use strict";
              const state = globalThis[{{System.Text.Json.JsonSerializer.Serialize(InjectionOwnershipContract.StateProperty)}}];
              if (!state || state.generation !== {{generation}} || state.cleaned) {
                return { applied: false, reason: "state-missing", generation: {{generation}} };
              }
              if (state.mediaReady && state.blobUrl) {
                return { applied: true, generation: {{generation}}, mediaKind: state.mediaKind };
              }
              if (state.activationPending && state.blobUrl) {
                return { applied: true, pending: true, generation: {{generation}}, mediaKind: state.mediaKind };
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
              state.activationPending = true;
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

              const load = new Promise(resolve => {
                let settled = false;
                let timeout = 0;
                let remaining = state.mediaLoadTimeoutMs;
                let visibleSince = 0;
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
                  document.removeEventListener("visibilitychange", updateTimeout);
                  if (state.cancelActivation === cancelActivation) {
                    state.cancelActivation = null;
                  }
                  resolve({ ok, reason });
                };
                const onLoaded = () => finish(ready(), ready() ? null : "media-dimensions-invalid");
                const onError = () => finish(false, "media-load-error");
                const cancelActivation = reason => finish(false, reason || "activation-cancelled");
                const updateTimeout = () => {
                  if (timeout) {
                    clearTimeout(timeout);
                    timeout = 0;
                    remaining -= performance.now() - visibleSince;
                  }
                  // Chromium may defer the entire video pipeline while the window is hidden.
                  // Waiting there is normal; enforce the decode deadline only while visible.
                  if (settled || (state.mediaKind === "video" && document.hidden)) return;
                  visibleSince = performance.now();
                  timeout = setTimeout(() => finish(false, "media-load-timeout"), Math.max(0, remaining));
                };
                state.cancelActivation = cancelActivation;
                media.addEventListener(
                  state.mediaKind === "video" ? "loadeddata" : "load",
                  onLoaded);
                media.addEventListener("error", onError);
                document.addEventListener("visibilitychange", updateTimeout);
                updateTimeout();
                media.src = blobUrl;
                if (state.mediaKind === "video") {
                  media.load();
                }
              });

              const activationTask = load.then(loadResult => {
                if (!loadResult.ok) {
                  if (globalThis[{{System.Text.Json.JsonSerializer.Serialize(InjectionOwnershipContract.StateProperty)}}] === state &&
                      state.activation === activation && !state.cleaned) {
                    state.cleanup(loadResult.reason);
                  }
                  return { applied: false, reason: loadResult.reason, generation: {{generation}} };
                }

                if (globalThis[{{System.Text.Json.JsonSerializer.Serialize(InjectionOwnershipContract.StateProperty)}}] !== state ||
                    state.activation !== activation || state.cleaned || state.blobUrl !== blobUrl) {
                  return { applied: false, reason: "activation-superseded", generation: {{generation}} };
                }

                state.activationPending = false;
                state.mediaReady = true;
                state.lastHeartbeat = Date.now();
                state.startRuntime();
                return { applied: true, generation: {{generation}}, mediaKind: state.mediaKind };
              });
              state.activationTask = activationTask;
              state.lastHeartbeat = Date.now();
              state.startRuntime();
              if (state.mediaKind !== "video") return await activationTask;

              // Transfer the installed generation to host maintenance while decoding is deferred.
              // Its load/error listeners remain live and finish automatically when Codex is shown.
              let onHidden;
              const deferred = new Promise(resolve => {
                onHidden = () => {
                  if (document.hidden) resolve({ applied: true, pending: true, generation: {{generation}}, mediaKind: state.mediaKind });
                };
                document.addEventListener("visibilitychange", onHidden);
                onHidden();
              });
              try {
                return await Promise.race([activationTask, deferred]);
              } finally {
                document.removeEventListener("visibilitychange", onHidden);
              }
            })()
            """;
    }

    internal static string ToCss(WallpaperObjectFit objectFit) => objectFit switch
    {
        WallpaperObjectFit.Cover => "cover",
        WallpaperObjectFit.Contain => "contain",
        WallpaperObjectFit.Fill => "fill",
        _ => throw new ArgumentOutOfRangeException(nameof(objectFit)),
    };

    internal static void EnsureGeneration(long generation)
    {
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "Generation must be positive.");
        }
    }
}
