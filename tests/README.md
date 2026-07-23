# Tests

The solution includes the test project, and CI runs the default suite with:

```powershell
dotnet test .\BackdropForCodex.slnx --configuration Release --filter "Category!=Integration"
```

Integration tests are marked with `Category=Integration`. They are reported as skipped unless
their explicit environment opt-in is enabled, so missing prerequisites never appear as passing
tests.

Run the current-machine compatibility checks on a supported Windows 11 machine with the reviewed
Microsoft Store Codex package installed and running:

```powershell
$env:BACKDROP_FOR_CODEX_RUN_MACHINE_TESTS = "1"
dotnet test .\BackdropForCodex.slnx --filter "FullyQualifiedName~CurrentMachineCompatibilityTests"
```

Run the Edge/CDP startup-readiness checks:

```powershell
$env:BACKDROP_FOR_CODEX_RUN_STARTUP_RACE_TESTS = "1"
dotnet test .\BackdropForCodex.slnx --filter "FullyQualifiedName~PuppeteerWallpaperSessionStartupReadinessTests"
```

Run the notification-area lifecycle smoke test from an unlocked Windows 11 desktop after building
the selected configuration:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\tests\Smoke\TrayLifecycle.ps1 `
  -Configuration Debug `
  -ProbeBeforeClose
```

The smoke test launches its own Backdrop for Codex process, closes the main window, and verifies
through Windows UI Automation that the process remains alive and its uniquely named icon is
discoverable in either the visible notification area or the hidden-icons panel. It refuses to take
over an existing matching process and only stops the PID it launched. GitHub-hosted runners do not
provide the interactive Explorer desktop this check requires, so run it locally before a release.
