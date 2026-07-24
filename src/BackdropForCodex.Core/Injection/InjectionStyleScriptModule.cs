using BackdropForCodex.Core.Codex;
using System.Text.Json;

namespace BackdropForCodex.Core.Injection;

internal static class InjectionStyleScriptModule
{
    private const string GlassStartMarker = "/* codex-wallpaper-glass:start */";
    private const string GlassEndMarker = "/* codex-wallpaper-glass:end */";
    private const string AdvancedStartMarker = "/* codex-wallpaper-advanced:start */";
    private const string AdvancedEndMarker = "/* codex-wallpaper-advanced:end */";

    internal static InjectionStyleCapabilities Resolve(
        CompatibilityCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!capabilities.Global.IsAvailable)
        {
            throw new ArgumentException(
                "Global wallpaper capability is required to build the base style.",
                nameof(capabilities));
        }

        return new InjectionStyleCapabilities(
            capabilities.Glass.IsAvailable,
            capabilities.Advanced.IsAvailable);
    }

    internal static string BuildCapabilityDowngrade(
        long generation,
        CompatibilityCapabilities capabilities)
    {
        InjectionMediaScriptModule.EnsureGeneration(generation);
        ArgumentNullException.ThrowIfNull(capabilities);
        return $$"""
            (() => {
              "use strict";
              const state = globalThis[{{JsonSerializer.Serialize(InjectionOwnershipContract.StateProperty)}}];
              if (!state || state.cleaned || state.generation !== {{generation}} ||
                  !state.style?.isConnected ||
                  state.style.id !== {{JsonSerializer.Serialize(InjectionOwnershipContract.StyleElementId)}} ||
                  state.style.dataset.codexWallpaperOwner !== {{JsonSerializer.Serialize(InjectionOwnershipContract.Owner)}} ||
                  state.style.dataset.codexWallpaperGeneration !== {{JsonSerializer.Serialize(generation.ToString(System.Globalization.CultureInfo.InvariantCulture))}}) {
                return false;
              }
              const removeBlocks = (css, start, end) => {
                let result = css;
                while (true) {
                  const startIndex = result.indexOf(start);
                  if (startIndex < 0) return result;
                  const endIndex = result.indexOf(end, startIndex + start.length);
                  if (endIndex < 0) return null;
                  result = result.slice(0, startIndex) +
                    result.slice(endIndex + end.length);
                }
              };
              let css = state.style.textContent || "";
              if (!{{(capabilities.Glass.IsAvailable ? "true" : "false")}} &&
                  state.glassEnabled) {
                css = removeBlocks(
                  css,
                  {{JsonSerializer.Serialize(GlassStartMarker)}},
                  {{JsonSerializer.Serialize(GlassEndMarker)}});
                if (css === null) return false;
                state.glassEnabled = false;
              }
              if (!{{(capabilities.Advanced.IsAvailable ? "true" : "false")}} &&
                  state.advancedSurfacesEnabled) {
                css = removeBlocks(
                  css,
                  {{JsonSerializer.Serialize(AdvancedStartMarker)}},
                  {{JsonSerializer.Serialize(AdvancedEndMarker)}});
                if (css === null) return false;
                state.advancedSurfacesEnabled = false;
              }
              state.style.textContent = css;
              return true;
            })()
            """;
    }
}

internal readonly record struct InjectionStyleCapabilities(
    bool GlassEnabled,
    bool AdvancedSurfacesEnabled);
