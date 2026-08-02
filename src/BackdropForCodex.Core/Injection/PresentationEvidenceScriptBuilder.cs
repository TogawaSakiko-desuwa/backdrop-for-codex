using BackdropForCodex.Core.Codex;
using System.Text.Json;

namespace BackdropForCodex.Core.Injection;

/// <summary>
/// Builds the single version-independent, read-only probe used by presentation contracts.
/// The returned payload contains only booleans and never page, package, or machine identity.
/// </summary>
internal static class PresentationEvidenceScriptBuilder
{
    private const string AppRootSelector = "body > #root";
    private const string MainSelector = "main";
    private const string ShellHeaderSelector =
        "header[data-app-shell-application-menu-bar][data-app-shell-header-edge-scroll]";
    private const string MainViewportSelector =
        "[data-app-shell-main-content-layout][data-app-shell-right-panel-full-width]";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string Build()
    {
        var payload = JsonSerializer.Serialize(
            new ProbeDefinition(
                AppRootSelector,
                MainSelector,
                ShellHeaderSelector,
                MainViewportSelector),
            SerializerOptions);

        return $$"""
            (() => {
              "use strict";
              const probe = Object.freeze({{payload}});
              const root = document.documentElement;
              const body = document.body;
              const appRoot = document.querySelector(probe.appRootSelector);
              const main = appRoot && appRoot.querySelector(probe.mainSelector);
              const globalStructure = Boolean(
                root && body && appRoot && main && appRoot.contains(main));
              const shellHeader =
                appRoot && appRoot.querySelector(probe.shellHeaderSelector);
              const mainViewport =
                main && main.querySelector(probe.mainViewportSelector);
              const shellStructure = globalStructure && Boolean(
                shellHeader && mainViewport);
              const cssApi = globalThis.CSS;
              const backdropFilterSupported = Boolean(
                cssApi && typeof cssApi.supports === "function" &&
                (cssApi.supports("backdrop-filter", "blur(1px)") ||
                 cssApi.supports("-webkit-backdrop-filter", "blur(1px)")));
              const selectorHasSupported = Boolean(
                cssApi && typeof cssApi.supports === "function" &&
                cssApi.supports("selector(:has(*))"));
              return JSON.stringify({
                globalStructure,
                shellStructure,
                backdropFilterSupported,
                selectorHasSupported
              });
            })()
            """;
    }

    internal static PresentationEvidence Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<PresentationEvidence>(json, SerializerOptions)
            ?? throw new JsonException("The presentation probe returned no evidence.");
    }

    private sealed record ProbeDefinition(
        string AppRootSelector,
        string MainSelector,
        string ShellHeaderSelector,
        string MainViewportSelector);
}
