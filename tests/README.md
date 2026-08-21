# Tests

Environment-independent verification stays separate from checks that require a real Codex installation, Edge/CDP, an unlocked Explorer desktop, or UI Automation.

The suite's case count changes over time and is not evidence that a particular checkout passed. Report the count emitted by the actual test command. If an environment-dependent check is not run or a prerequisite is unavailable, record it explicitly as **not verified**—never as passed or as part of the passing test count.

## Required environment-independent verification

Run the release gate in this order:

```powershell
dotnet restore .\BackdropForCodex.slnx --locked-mode
dotnet format .\BackdropForCodex.slnx --verify-no-changes --no-restore
dotnet build .\BackdropForCodex.slnx --configuration Release --no-restore
dotnet test .\BackdropForCodex.slnx `
  --configuration Release `
  --no-build `
  --no-restore `
  --filter "Category!=Integration&Category!=BrowserContract"
```

CI runs the same sequence with .NET SDK `10.0.301` and `10.0.302`. Do not infer that one SDK passed because the other did; record both matrix legs.

Verify the win-x64 self-contained, single-file shape with:

```powershell
$publishDir = Join-Path $PWD "artifacts\ci-publish"
dotnet publish .\src\BackdropForCodex.App\BackdropForCodex.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --no-restore `
  --output $publishDir `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugSymbols=false `
  -p:DebugType=None
```

The shape check succeeds only when the output contains exactly one top-level `BackdropForCodex.exe` and no subdirectories. Use a clean, dedicated publish directory when checking this locally.

## Coverage

The non-integration suite covers these release contracts:

- schema 3 strict serialization, deep immutable `SettingsV3` snapshots, durable/UI-dirty/runtime equality, and explicit rejection of unsupported source/content combinations;
- metadata-only V1/V2 → V3 migration, exact raw V1/V2 backups, future-schema protection, backup conflicts, deprecated-marker passthrough, and last-known content/display metadata; migration does not access providers or media files;
- `Draft`/`SavedDesired`/`ActiveSnapshot` state separation, multi-profile CRUD, confirmed delete/rebinding, hidden region preservation, shared/orphaned media references, and Official empty profiles;
- normal Workspace/Application/runtime interfaces use canonical `SettingsV3`; `SettingsV1` and `SettingsV2` remain limited to read-only migration, backup compatibility, and their fixtures;
- one-owner latest-wins scheduling with one running and at most one pending Apply, pending replacement, cancellation, serialized independent writes, and exclusive reset/restore/Official/Dispose barriers;
- controllable actor-boundary checkpoints around preflight, before/after durable save, runtime entry, and cancellation, plus runtime-stage checkpoints around lease acquisition, Codex/CDP validation, injection, playback transfer, and cleanup;
- the persistence commit-point rule: a successful atomic save updates `SavedDesired` even when that revision is superseded immediately afterward, while the stale request cannot enter runtime or report success;
- independent activation revisions and injection generations, stale progress/health/capability filtering, and runtime-equivalent snapshot promotion without reinjection;
- playback ownership tokens, conditional release, pending-lease disposal, and the guarantee that stale cleanup cannot release a newer active lease;
- bounded fake Steam registry/VDF/ACF and `project.json` fixtures covering multiple libraries, non-default drives, duplicate installs/PublishedFileIds, invalid JSON, path traversal/reparse rejection, Local/Workshop disappearance and revival, Image/Video direct leases, Scene package ambiguity, static thumbnails, and permanent Application/Unknown rejection;
- Wallpaper Engine window-level command allowlisting, exact helper kill-and-await on timeout/cancellation, owned-window exact-title/HWND/PID/start/path proof, missing-identity rejection before snapshot capture, raw title/HWND observations that block false absence without gaining authorization, reported-project validation, 1px non-activating pop-out placement and rollback/disarm, crash-recovery journal retention, exact-window close followed by mandatory global same-name absence proof, and a separately tested optional audio-isolation adapter that production does not enable;
- dynamic Direct/Scene/Web lease orchestration with a 1080p/30fps → 720p/15fps → 960×540/5fps recovery ladder, bounded 2-segment/8 MiB lossless encoder-batch delivery, deterministic multi-`moof` finalized-file splitting, transactional first-keyframe publication, local pipeline restart, transient page receipt/transport recovery, terminal page-identity/cleanup proof, latest-generation cleanup, WGC/MF typed failures, and page-owned MSE lifecycle;
- typed outcomes (`MediaActive`, `Official`, `SavedButNotActivated`, `Superseded`, `Canceled`, `Failed`) and typed surfaces (`Official`, `MediaActive`, `Faulted`, `Disconnected`);
- strict package/process/session/listener/IPv4 loopback/browser/socket/target/unique-page identity order, zero DOM probes after safety failure, zero/multiple-page rejection, baseline failure, and version-independent structure contracts;
- stable `data-app-shell-*` presentation evidence for the Codex 26.727 CSS Modules shell, with a bounded unique-target readiness window for Direct and Scene/Web before conservative initial Global fallback; Scene/Web proves readiness before starting any window/capture/encoder/page stream and revalidates the same target plus the current Global baseline before mutation. Runtime structural misses use per-capability three-observation confirmation; positive evidence resets only the affected pending streak, while explicit non-structural failures still downgrade immediately;
- static injection resource, owner, generation, capability-block, and key reviewed-selector anchors; native selector matching and computed-style behavior belong to the browser-contract gate below;
- editing and resubmitting during activation, stale-revision UI filtering, profile cards changing only `Draft`, empty profiles skipping CDP risk confirmation, Saved ≠ Active rendering, temporary Official, dirty-draft confirmation, the 959/960 px breakpoint, and critical accessibility behavior.
- the 16:9 preview canvas across normal, maximized, and minimum layouts; uniform scaling and pointer-coordinate inversion; one shared image/video backdrop sample; and blur containment within the five rounded simulated glass surfaces.

The concurrency stress scenario submits 100 rapid Apply requests. Its final `SavedDesired` and `ActiveSnapshot` must match the last snapshot, at most one lease may be active, and every other pending lease must be disposed. Intermediate atomic commits may temporarily become `SavedDesired`, but may not overwrite a later commit or publish a success state after supersession.

## Environment-dependent checks

Integration tests use `Category=Integration`. They are skipped unless their explicit environment opt-in is enabled. A skipped test or missing prerequisite is **not verified**, not passed.

### Reviewed selector browser contracts

Run the reviewed selector contracts against a real headless Microsoft Edge instance:

```powershell
$env:BACKDROP_FOR_CODEX_RUN_BROWSER_CONTRACTS = "1"
dotnet test .\tests\BackdropForCodex.Core.Tests\BackdropForCodex.Core.Tests.csproj `
  --filter "Category=BrowserContract"
```

These contracts use the browser's CSSOM, `querySelectorAll`, and computed styles for reviewed positive, near-miss, protected-surface, Glass-downgrade, and Advanced-downgrade behavior. Set `BACKDROP_FOR_CODEX_EDGE_PATH` only when `msedge.exe` is outside the standard installation paths. CI runs this category explicitly on the .NET 10.0.301 Windows leg; a missing Edge executable fails that leg.

### Current-machine Codex identity

On a supported Windows 11 x64 machine with the reviewed official Microsoft Store/MSIX Codex package installed and, where required, running:

```powershell
$env:BACKDROP_FOR_CODEX_RUN_MACHINE_TESTS = "1"
dotnet test .\BackdropForCodex.slnx `
  --filter "FullyQualifiedName~CurrentMachineCompatibilityTests"
```

Record the installed package identity, running-process identity, and live presentation-contract checks separately. Do not replace any of them with a unit-test result.

### Edge/CDP startup and injection

Run the Edge/CDP startup-readiness checks only on a machine where their browser prerequisites are available:

```powershell
$env:BACKDROP_FOR_CODEX_RUN_STARTUP_RACE_TESTS = "1"
dotnet test .\BackdropForCodex.slnx `
  --filter "FullyQualifiedName~PuppeteerWallpaperSessionStartupReadinessTests"
```

Record cold start, unique/zero/multiple targets, CSP-native media loading, generation-scoped cleanup, adaptive Markdown wide-table glass in LTR/RTL at 900, 960, and 1280 px (single glass owner, matching scroller bounds, reachable horizontal scrolling, and no document-level overflow), and version-independent DOM-contract observations that were actually exercised. If Edge, the reviewed package, or the required desktop/session state is absent, list the affected cases as not verified.

### Wallpaper Engine and real dynamic media

Wallpaper Engine integration is always explicit opt-in and uses only a user-owned, already installed project. Start the verified official Steam installation of Wallpaper Engine yourself before running the window-level test; the test does not prove safe on-demand startup and must not be described as doing so.

```powershell
$env:BACKDROP_FOR_CODEX_RUN_WALLPAPER_ENGINE_TESTS = "1"
$env:BACKDROP_FOR_CODEX_WALLPAPER_ENGINE_PROJECT = "C:\path\to\an\installed\scene.pkg-or-index.html"
dotnet test .\tests\BackdropForCodex.Core.Tests\BackdropForCodex.Core.Tests.csproj `
  --filter "FullyQualifiedName~WallpaperEnginePlatformIntegrationTests"
```

This opt-in validates only the real installation/process boundary, uniquely named pop-out, reported project, 1px non-activating placement, exact close plus global same-name absence proof, and the separate optional audio-isolation adapter cases implemented by that test. Production Scene/Web leaves Wallpaper Engine audio unchanged. The test uses the user's existing installation and content; the repository contains no Wallpaper Engine binary, Workshop item, or user path. A skipped test, missing project, or Wallpaper Engine not already running is **not verified**.

The real WGC → Media Foundation H.264/fMP4 → Edge MSE machine checks use a separate opt-in and a synthetic window owned by the test process:

```powershell
$env:BACKDROP_RUN_REAL_WGC_MF = "1"
dotnet test .\tests\BackdropForCodex.Core.Tests\BackdropForCodex.Core.Tests.csproj `
  --filter "FullyQualifiedName~WindowsGraphicsCaptureMediaFoundationMachineTests"
```

Record primary-tier throughput, fallback-tier MSE decode, GPU/codec prerequisites, and any skipped case separately. Neither opt-in by itself proves the complete Scene/Web chain, 30-minute Wallpaper Engine stability, desktop non-interference, pause/resume, device-loss recovery, or every close/reopen path; those remain **not verified** unless the corresponding end-to-end test was actually run.

The bounded 30-minute synthetic-window fallback soak is a separate machine gate:

```powershell
$env:BACKDROP_RUN_REAL_WGC_MF_SOAK = "1"
dotnet test .\tests\BackdropForCodex.Core.Tests\BackdropForCodex.Core.Tests.csproj `
  --filter "FullyQualifiedName~WindowsGraphicsCaptureMediaFoundationMachineTests.FallbackTierKeepsMemoryBoundedForThirtyMinutes"
```

On 2026-08-11 the reviewed Windows 11 x64 machine completed the synthetic-window fallback gates without starting Wallpaper Engine. The 8-minute run encoded 7,195 frames (14.989 diagnostic fps), with 137,170,944 bytes peak private growth and 46,144 bytes managed growth. The 30-minute run encoded 26,934 frames (14.963 diagnostic fps), with 140,775,424 bytes peak private growth, 47,960 bytes managed growth, and no native/COM crash. Both remained below the 256 MiB private and 64 MiB managed limits. These direct WGC/MF soak tests prove bounded encoder memory and no native crash; fps is diagnostic, not an exact gate. Production uses latest-frame pacing and a 30→15→5 fps render ladder. Performance, transient capture/page transport, heartbeat, and stream-backpressure failures request a profile downgrade or generation-local restart with bounded backoff; they do not complete the public health channel or trigger global cleanup. The lowest tier re-anchors isolated cadence lag and can replay its retained frame while capture is briefly quiet. Ownership ambiguity and resource-cleanup failures remain fail-closed. Three short machine checks after this change recorded 14.126, 14.566, and 13.969 nominal fps without a typed failure, and the paired real Edge MSE checks decoded 1280×720 successfully. The long soak does not traverse the production pacer/latest-frame/page chain; that state machine is covered by deterministic unit and composition tests but still lacks a complete real Scene/Web activation run. None of these tests proves Wallpaper Engine startup, pop-out ownership, or the complete Scene/Web chain.

### Notification-area lifecycle

Run the notification-area smoke test from an unlocked Windows 11 desktop after building the selected configuration:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\tests\Smoke\TrayLifecycle.ps1 `
  -Configuration Debug `
  -ProbeBeforeClose
```

The script launches its own Backdrop for Codex process, closes the main window, and verifies through Windows UI Automation that the process remains alive and its uniquely named icon appears in either the visible notification area or hidden-icons panel. It refuses to take over an existing matching process and stops only the PID it launched.

GitHub-hosted runners do not provide the interactive Explorer desktop this check requires. If it is not run locally, report **notification-area lifecycle: not verified**.

### UI and accessibility smoke

On an unlocked interactive desktop, manually or through the repository's UI Automation coverage verify keyboard profile selection, context-menu actions, Automation Name/selection state, focus restoration, high contrast, reduced motion, 125%–200% scaling, the 959/960 px layout boundary, and the 640×520 minimum window. Record each unavailable display/accessibility configuration as not verified.

## Reporting results

A release or pull-request verification note should list:

1. the exact commit and SDK version;
2. each restore/format/build/test/publish command and its exit result;
3. the non-integration passed/failed/skipped count from the actual run;
4. each environment-dependent group as passed, failed, or not verified, with the reason;
5. any difference from the expected command order or publish shape.

Do not collapse skipped or unavailable environment tests into “all tests passed.” Do not claim a real Codex, Edge/CDP, notification-area, or accessibility smoke result from mocks or static inspection.
