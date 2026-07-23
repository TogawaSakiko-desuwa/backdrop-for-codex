using System.Runtime.InteropServices;
using BackdropForCodex.Core.Codex;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class ApplicationActivationManagerTests
{
    [Fact]
    public void Activate_UsesProfileAumidAndReturnsPid()
    {
        var backend = new StubBackend { ProcessId = 4242 };
        var manager = new WindowsApplicationActivationManager(backend);
        var profile = CodexCompatibilityTests.GetProfile();

        var result = manager.Activate(
            profile,
            "--from-wallpaper",
            ApplicationActivationOptions.NoErrorUi | ApplicationActivationOptions.NoSplashScreen);

        Assert.Equal(4242u, result.ProcessId);
        Assert.Equal(profile.AppUserModelId, backend.AppUserModelId);
        Assert.Equal("--from-wallpaper", backend.Arguments);
        Assert.Equal(
            ApplicationActivationOptions.NoErrorUi | ApplicationActivationOptions.NoSplashScreen,
            backend.Options);
    }

    [Fact]
    public void Activate_PropagatesFailedHresult()
    {
        var backend = new StubBackend { HResult = unchecked((int)0x80004005), ProcessId = 1 };
        var manager = new WindowsApplicationActivationManager(backend);

        Assert.Throws<COMException>(() => manager.Activate(CodexCompatibilityTests.GetProfile()));
    }

    [Fact]
    public void Activate_RejectsSuccessfulCallWithoutPid()
    {
        var manager = new WindowsApplicationActivationManager(new StubBackend());

        Assert.Throws<InvalidOperationException>(
            () => manager.Activate(CodexCompatibilityTests.GetProfile()));
    }

    private sealed class StubBackend : IApplicationActivationBackend
    {
        public int HResult { get; init; }

        public uint ProcessId { get; init; }

        public string? AppUserModelId { get; private set; }

        public string? Arguments { get; private set; }

        public ApplicationActivationOptions Options { get; private set; }

        public int ActivateApplication(
            string appUserModelId,
            string arguments,
            ApplicationActivationOptions options,
            out uint processId)
        {
            AppUserModelId = appUserModelId;
            Arguments = arguments;
            Options = options;
            processId = ProcessId;
            return HResult;
        }
    }
}
