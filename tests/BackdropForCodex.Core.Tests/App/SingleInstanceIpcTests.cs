using BackdropForCodex.App;
using Xunit;

namespace BackdropForCodex.Core.Tests.App;

public sealed class SingleInstanceIpcTests
{
    [Theory]
    [InlineData((byte)SingleInstanceCommand.Show)]
    [InlineData((byte)SingleInstanceCommand.Launch)]
    public async Task ClientForwardsOneCommandToCurrentUserPipe(byte commandValue)
    {
        var command = (SingleInstanceCommand)commandValue;
        var pipeName = $"BackdropForCodex.Tests.{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<SingleInstanceCommand>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new SingleInstanceCommandServer(
            pipeName,
            value => received.TrySetResult(value));

        var sent = SingleInstanceCommandClient.TrySend(pipeName, command);

        Assert.True(sent);
        Assert.Equal(command, await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void IdentityIsStableAndScopedToCurrentSession()
    {
        var first = SingleInstanceIdentity.ForCurrentUserSession();
        var second = SingleInstanceIdentity.ForCurrentUserSession();

        Assert.Equal(first, second);
        Assert.StartsWith("Local\\CodexWallpaper.Singleton.", first.MutexName, StringComparison.Ordinal);
        Assert.StartsWith("CodexWallpaper.Commands.", first.PipeName, StringComparison.Ordinal);
        Assert.Matches("^[A-F0-9]{24}$", first.PipeName["CodexWallpaper.Commands.".Length..]);
    }
}
