using BackdropForCodex.App.Services.Wallpaper;
using BackdropForCodex.Core.Runtime;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class WallpaperApplicationServiceTests
{
    [Theory]
    [InlineData(RuntimeActivationOutcome.MediaActive, true)]
    [InlineData(RuntimeActivationOutcome.Official, false)]
    [InlineData(RuntimeActivationOutcome.SavedButNotActivated, false)]
    [InlineData(RuntimeActivationOutcome.Superseded, false)]
    [InlineData(RuntimeActivationOutcome.Canceled, false)]
    [InlineData(RuntimeActivationOutcome.Failed, false)]
    public void ShortcutCreationRequiresAnActiveMediaOutcome(
        RuntimeActivationOutcome outcome,
        bool expected)
    {
        Assert.Equal(
            expected,
            WallpaperApplicationService.ShouldCreateShortcut(outcome));
    }
}
