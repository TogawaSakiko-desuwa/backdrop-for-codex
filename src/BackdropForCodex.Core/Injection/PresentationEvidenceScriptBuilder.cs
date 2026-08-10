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
    private const string ShellMainSelector =
        "main[data-app-shell-main-surface]";
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
                ShellMainSelector,
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
              const mains = appRoot
                ? Array.from(appRoot.querySelectorAll(probe.mainSelector))
                : [];
              const main = mains[0] || null;
              const globalStructure = Boolean(
                root && body && appRoot && main && appRoot.contains(main));
              const ownsSignal = (candidate, selector) =>
                Array.from(candidate.querySelectorAll(selector)).some(
                  signal => signal.closest(probe.mainSelector) === candidate);
              const shellEvidence = globalStructure
                ? mains
                  .filter(candidate =>
                    !candidate.parentElement?.closest(probe.mainSelector))
                  .map(candidate => {
                    const typedMain = candidate.matches(probe.shellMainSelector);
                    const shellHeader = ownsSignal(
                      candidate,
                      probe.shellHeaderSelector);
                    const mainViewport = ownsSignal(
                      candidate,
                      probe.mainViewportSelector);
                    const signalCount =
                      Number(typedMain) +
                      Number(shellHeader) +
                      Number(mainViewport);
                    return Object.freeze({
                      typedMain,
                      shellHeader,
                      mainViewport,
                      signalCount
                    });
                  })
                : [];
              const shellCandidates = shellEvidence.filter(
                evidence => evidence.signalCount >= 2);
              const shellStructure =
                globalStructure && shellCandidates.length === 1;
              const typedShellMainPresent = shellEvidence.some(
                evidence => evidence.typedMain);
              const shellHeaderPresent = shellEvidence.some(
                evidence => evidence.shellHeader);
              const mainViewportPresent = shellEvidence.some(
                evidence => evidence.mainViewport);
              const shellCandidateAmbiguous = shellCandidates.length > 1;
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
                typedShellMainPresent,
                shellHeaderPresent,
                mainViewportPresent,
                shellCandidateAmbiguous,
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
        string ShellMainSelector,
        string ShellHeaderSelector,
        string MainViewportSelector);
}
