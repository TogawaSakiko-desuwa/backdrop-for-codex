using System.Text.Json;

namespace BackdropForCodex.Core.Injection;

internal static class InjectionLifecycleScriptModule
{
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(10);

    internal static string BuildHeartbeat(long generation)
    {
        EnsureGeneration(generation);
        return $$"""
            (() => {
              "use strict";
              const state = globalThis[{{JsonSerializer.Serialize(InjectionOwnershipContract.StateProperty)}}];
              if (!state || state.generation !== {{generation}} || !state.mediaReady ||
                  !state.root?.isConnected || !state.style?.isConnected ||
                  !state.media?.isConnected || !state.overlay?.isConnected ||
                  state.root.id !== {{JsonSerializer.Serialize(InjectionOwnershipContract.RootElementId)}} ||
                  state.style.id !== {{JsonSerializer.Serialize(InjectionOwnershipContract.StyleElementId)}} ||
                  document.getElementById({{JsonSerializer.Serialize(InjectionOwnershipContract.RootElementId)}}) !== state.root ||
                  document.getElementById({{JsonSerializer.Serialize(InjectionOwnershipContract.StyleElementId)}}) !== state.style ||
                  state.root.dataset.codexWallpaperOwner !== {{JsonSerializer.Serialize(InjectionOwnershipContract.Owner)}} ||
                  state.root.dataset.codexWallpaperGeneration !== {{JsonSerializer.Serialize(generation.ToString(System.Globalization.CultureInfo.InvariantCulture))}} ||
                  state.style.dataset.codexWallpaperOwner !== {{JsonSerializer.Serialize(InjectionOwnershipContract.Owner)}} ||
                  state.style.dataset.codexWallpaperGeneration !== {{JsonSerializer.Serialize(generation.ToString(System.Globalization.CultureInfo.InvariantCulture))}} ||
                  state.media.dataset.codexWallpaperOwner !== {{JsonSerializer.Serialize(InjectionOwnershipContract.Owner)}} ||
                  state.media.dataset.codexWallpaperGeneration !== {{JsonSerializer.Serialize(generation.ToString(System.Globalization.CultureInfo.InvariantCulture))}} ||
                  state.overlay.dataset.codexWallpaperOwner !== {{JsonSerializer.Serialize(InjectionOwnershipContract.Owner)}} ||
                  state.overlay.dataset.codexWallpaperGeneration !== {{JsonSerializer.Serialize(generation.ToString(System.Globalization.CultureInfo.InvariantCulture))}} ||
                  state.media.parentElement !== state.root ||
                  state.overlay.parentElement !== state.root || !state.blobUrl ||
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

    internal static string BuildSetPaused(long generation, bool paused)
    {
        EnsureGeneration(generation);
        return $$"""
            (() => {
              "use strict";
              const state = globalThis[{{JsonSerializer.Serialize(InjectionOwnershipContract.StateProperty)}}];
              if (!state || state.generation !== {{generation}}) {
                return false;
              }
              state.hostPaused = {{(paused ? "true" : "false")}};
              state.updatePlayback?.();
              return true;
            })()
            """;
    }

    internal static string BuildCleanup(long generation)
    {
        EnsureGeneration(generation);
        return $$"""
            (() => {
              "use strict";
              const key = {{JsonSerializer.Serialize(InjectionOwnershipContract.StateProperty)}};
              const owner = {{JsonSerializer.Serialize(InjectionOwnershipContract.Owner)}};
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
              const root = document.getElementById({{JsonSerializer.Serialize(InjectionOwnershipContract.RootElementId)}});
              const style = document.getElementById({{JsonSerializer.Serialize(InjectionOwnershipContract.StyleElementId)}});
              const fileInput = document.getElementById({{JsonSerializer.Serialize(InjectionOwnershipContract.FileInputElementId)}});
              if (isExactOwned(fileInput, {{JsonSerializer.Serialize(InjectionOwnershipContract.FileInputElementId)}}, "INPUT") &&
                  fileInput.type === "file") {
                fileInput.value = "";
                fileInput.remove();
              }
              if (isExactOwned(root, {{JsonSerializer.Serialize(InjectionOwnershipContract.RootElementId)}}, "DIV")) {
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
              if (isExactOwned(style, {{JsonSerializer.Serialize(InjectionOwnershipContract.StyleElementId)}}, "STYLE")) {
                style.remove();
              }
              if (globalThis[key] === state) delete globalThis[key];
              return true;
            })()
            """;
    }

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
