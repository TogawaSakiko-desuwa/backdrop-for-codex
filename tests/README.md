# Tests

Backdrop for Codex 1.4.3 keeps environment-independent verification separate from checks that require a real Codex installation, Edge/CDP, an unlocked Explorer desktop, or UI Automation.

The current 1.4.3 line contains **540 non-integration automated cases**. That number describes suite coverage; it is not evidence that a particular checkout passed. Report a check as passed only after the command actually completes successfully. If an environment-dependent check is not run or a prerequisite is unavailable, record it explicitly as **not verified**—never as passed or as part of the passing test count.

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
  --filter "Category!=Integration"
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

## 1.4.3 coverage

The non-integration suite covers these release contracts:

- schema 2 golden compatibility with 1.3.5 readers/writers; no new serialized fields or Settings V3;
- deep immutable `SettingsV2` snapshots, durable equality, UI-dirty equality, and runtime equivalence;
- `Draft`/`SavedDesired`/`ActiveSnapshot` state separation, multi-profile CRUD, confirmed delete/rebinding, hidden region preservation, shared/orphaned media references, and Official empty profiles;
- V1 migration, exact raw-backup recovery, future-schema protection, and deprecated-marker passthrough; normal Workspace/Application/runtime interfaces do not use `SettingsV1`;
- one-owner latest-wins scheduling with one running and at most one pending Apply, pending replacement, cancellation, serialized independent writes, and exclusive reset/restore/Official/Dispose barriers;
- controllable actor-boundary checkpoints around preflight, before/after durable save, runtime entry, and cancellation, plus runtime-stage checkpoints around lease acquisition, Codex/CDP validation, injection, playback transfer, and cleanup;
- the persistence commit-point rule: a successful atomic save updates `SavedDesired` even when that revision is superseded immediately afterward, while the stale request cannot enter runtime or report success;
- independent activation revisions and injection generations, stale progress/health/capability filtering, and runtime-equivalent snapshot promotion without reinjection;
- playback ownership tokens, conditional release, pending-lease disposal, and the guarantee that stale cleanup cannot release a newer active lease;
- typed outcomes (`MediaActive`, `Official`, `SavedButNotActivated`, `Superseded`, `Canceled`, `Failed`) and typed surfaces (`Official`, `MediaActive`, `Faulted`, `Disconnected`);
- strict package/process/session/listener/IPv4 loopback/browser/socket/target/unique-page identity order, zero DOM probes after safety failure, zero/multiple-page rejection, baseline failure, and version-independent structure contracts;
- stable `data-app-shell-*` presentation evidence for the Codex 26.727 CSS Modules shell, with conservative global-baseline fallback when reviewed markers are absent;
- editing and resubmitting during activation, stale-revision UI filtering, profile cards changing only `Draft`, empty profiles skipping CDP risk confirmation, Saved ≠ Active rendering, temporary Official, dirty-draft confirmation, the 959/960 px breakpoint, and critical accessibility behavior.
- the 16:9 preview canvas across normal, maximized, and minimum layouts; uniform scaling and pointer-coordinate inversion; one shared image/video backdrop sample; and blur containment within the five rounded simulated glass surfaces.

The concurrency stress scenario submits 100 rapid Apply requests. Its final `SavedDesired` and `ActiveSnapshot` must match the last snapshot, at most one lease may be active, and every other pending lease must be disposed. Intermediate atomic commits may temporarily become `SavedDesired`, but may not overwrite a later commit or publish a success state after supersession.

## Environment-dependent checks

Integration tests use `Category=Integration`. They are skipped unless their explicit environment opt-in is enabled. A skipped test or missing prerequisite is **not verified**, not passed.

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

Record cold start, unique/zero/multiple targets, CSP-native media loading, generation-scoped cleanup, and version-independent DOM-contract observations that were actually exercised. If Edge, the reviewed package, or the required desktop/session state is absent, list the affected cases as not verified.

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
