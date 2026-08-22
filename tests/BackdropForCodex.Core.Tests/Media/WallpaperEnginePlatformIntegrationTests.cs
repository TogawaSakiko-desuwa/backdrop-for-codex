using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Tests.Infrastructure;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperEnginePlatformIntegrationTests
{
    private const string OptInVariable =
        "BACKDROP_FOR_CODEX_RUN_WALLPAPER_ENGINE_TESTS";
    private const string ProjectPathVariable =
        "BACKDROP_FOR_CODEX_WALLPAPER_ENGINE_PROJECT";
    private const string CoreAudioOptInVariable =
        "BACKDROP_FOR_CODEX_RUN_CORE_AUDIO_TESTS";

    [IntegrationFact(CoreAudioOptInVariable)]
    [Trait("Category", "Integration")]
    public async Task CurrentCoreAudioSessionsCanBeEnumeratedAndReleasedWhenOptedIn()
    {
        var sessions = await new WindowsCoreAudioSessionSource().CaptureAsync(
            CancellationToken.None);
        try
        {
            Assert.NotEmpty(sessions);
            Assert.All(
                sessions,
                session => Assert.DoesNotContain(
                    session.ProcessPath,
                    session.ToString(),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var session in sessions)
            {
                await session.DisposeAsync();
            }
        }
    }

    [IntegrationFact(OptInVariable)]
    [Trait("Category", "Integration")]
    public async Task OwnedPopOutPassesWindowAndOptionalLocationGatesWhenOptedIn()
    {
        var projectPath = Environment.GetEnvironmentVariable(ProjectPathVariable);
        if (string.IsNullOrWhiteSpace(projectPath) ||
            !Path.IsPathFullyQualified(projectPath) ||
            !File.Exists(projectPath))
        {
            throw new InvalidOperationException(
                $"Set {ProjectPathVariable} to one user-owned installed Scene or Web entry point.");
        }

        var installation = await new WallpaperEngineInstallationLocator().LocateAsync();
        var control = new WallpaperEngineControlClient();
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier();
        var name = WallpaperEngineOwnedWindowName.Create(1);
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);
        IWallpaperEnginePopOutPlacementLease? placementLease = null;
        var openAttempted = false;
        try
        {
            await control.EnsureRunningAsync(installation, CancellationToken.None);
            openAttempted = true;
            await control.OpenWindowAsync(
                installation,
                projectPath,
                name,
                new WallpaperEngineWindowOptions(640, 360),
                new WallpaperEngineWindowPlacement(-32000, -32000),
                CancellationToken.None);
            var window = await verifier.WaitForOwnedWindowAsync(
                installation,
                name,
                baseline,
                CancellationToken.None);
            placementLease = await new WindowsWallpaperEnginePopOutPlacement()
                .PlaceAsync(
                    window,
                    new WallpaperEngineWindowOptions(640, 360),
                    CancellationToken.None);
            var reportedPath = await control.QueryWindowWallpaperAsync(
                installation,
                name,
                CancellationToken.None);
            Assert.True(reportedPath is null || PathsEqual(projectPath, reportedPath));
        }
        finally
        {
            if (openAttempted)
            {
                await control.CloseWindowAsync(
                    installation,
                    name,
                    CancellationToken.None);
                if (placementLease is not null)
                {
                    await placementLease.MarkClosedAsync(CancellationToken.None);
                }

                await verifier.ConfirmOwnedWindowAbsentAsync(
                    installation,
                    name,
                    baseline,
                    CancellationToken.None);
                if (placementLease is not null)
                {
                    await placementLease.DisposeAsync();
                }
            }
        }
    }

    private static bool PathsEqual(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual) || !Path.IsPathFullyQualified(actual))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(expected),
            Path.GetFullPath(actual),
            StringComparison.OrdinalIgnoreCase);
    }
}
