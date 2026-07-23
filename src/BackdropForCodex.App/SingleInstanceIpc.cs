using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace BackdropForCodex.App;

internal enum SingleInstanceCommand : byte
{
    Show = 1,
    Launch = 2,
}

internal sealed record SingleInstanceIdentity(string MutexName, string PipeName)
{
    public static SingleInstanceIdentity ForCurrentUserSession()
    {
        using var windowsIdentity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var userIdentity = windowsIdentity.User?.Value;
        if (string.IsNullOrWhiteSpace(userIdentity))
        {
            userIdentity = Environment.UserName;
        }

        var identityBytes = Encoding.UTF8.GetBytes($"{userIdentity}|{process.SessionId}");
        var suffix = Convert.ToHexString(SHA256.HashData(identityBytes))[..24];
        // Keep these names stable across display-name changes so old and new builds cannot
        // run side by side and compete for the same Codex page.
        return new SingleInstanceIdentity(
            $"Local\\CodexWallpaper.Singleton.{suffix}",
            $"CodexWallpaper.Commands.{suffix}");
    }
}

internal sealed class SingleInstanceCommandServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Action<SingleInstanceCommand> _commandReceived;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _listenTask;
    private int _disposed;

    public SingleInstanceCommandServer(
        string pipeName,
        Action<SingleInstanceCommand> commandReceived)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _commandReceived = commandReceived ?? throw new ArgumentNullException(nameof(commandReceived));
        _listenTask = ListenAsync(_shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _listenTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var commandBuffer = new byte[1];
                var bytesRead = await pipe.ReadAsync(commandBuffer, cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 1 && Enum.IsDefined((SingleInstanceCommand)commandBuffer[0]))
                {
                    _commandReceived((SingleInstanceCommand)commandBuffer[0]);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A malformed or abruptly disconnected local client must not stop future commands.
            }
        }
    }
}

internal static class SingleInstanceCommandClient
{
    private const int ConnectionTimeoutMilliseconds = 2_000;

    public static bool TrySend(string pipeName, SingleInstanceCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (!Enum.IsDefined(command))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        try
        {
            using var pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.None);
            pipe.Connect(ConnectionTimeoutMilliseconds);
            pipe.WriteByte((byte)command);
            pipe.Flush();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
