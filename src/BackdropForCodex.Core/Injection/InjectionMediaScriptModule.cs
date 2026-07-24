namespace BackdropForCodex.Core.Injection;

internal static class InjectionMediaScriptModule
{
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

              state.mediaReady = true;
              state.lastHeartbeat = Date.now();
              state.startRuntime();
              return { applied: true, generation: {{generation}}, mediaKind: state.mediaKind };
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
