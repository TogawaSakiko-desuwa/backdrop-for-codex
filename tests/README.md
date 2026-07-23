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
