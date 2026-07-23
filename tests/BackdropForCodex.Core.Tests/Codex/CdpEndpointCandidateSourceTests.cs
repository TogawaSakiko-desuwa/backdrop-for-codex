using System.Net;
using BackdropForCodex.Core.Codex;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class CdpEndpointCandidateSourceTests
{
    [Fact]
    public async Task GetCandidatesAsync_UsesOnlyOfficialProcessesWithExplicitLoopbackPort()
    {
        var profile = CodexCompatibilityTests.GetProfile();
        var processSource = new StubProcessSource(
        [
            Process(31, "ChatGPT.exe", "--remote-debugging-port=9222 --remote-debugging-address=127.0.0.1"),
            Process(32, "ChatGPT.exe", "--remote-debugging-port 9333 --remote-debugging-address 127.0.0.1"),
            Process(33, "other.exe", "--remote-debugging-port=9444"),
            Process(34, "ChatGPT.exe", "--remote-debugging-port=9555", "OpenAI.Codex_attacker"),
            Process(35, "ChatGPT.exe", "--remote-debugging-port=9666 --remote-debugging-address=0.0.0.0"),
            Process(36, "ChatGPT.exe", "--no-remote-debugging"),
        ]);
        var source = new CommandLineCdpEndpointCandidateSource(processSource);

        var candidates = await source.GetCandidatesAsync(profile);

        Assert.Collection(
            candidates,
            candidate =>
            {
                Assert.Equal(31, candidate.ProcessId);
                Assert.Equal(new Uri("http://127.0.0.1:9222/"), candidate.BaseUri);
            },
            candidate =>
            {
                Assert.Equal(32, candidate.ProcessId);
                Assert.Equal(new Uri("http://127.0.0.1:9333/"), candidate.BaseUri);
            });
    }

    [Theory]
    [InlineData("--remote-debugging-port=9222")]
    [InlineData("--remote-debugging-port=9222 --remote-debugging-address=localhost")]
    [InlineData("--remote-debugging-port=9222 --remote-debugging-address=::1")]
    [InlineData("--remote-debugging-port=9222 --remote-debugging-address=127.0.0.1 --remote-debugging-address=0.0.0.0")]
    [InlineData("--remote-debugging-port=9222 --remote-debugging-port=9333 --remote-debugging-address=127.0.0.1")]
    public async Task GetCandidatesAsync_RequiresOneExplicitIpv4LoopbackBinding(string commandLine)
    {
        var source = new CommandLineCdpEndpointCandidateSource(
            new StubProcessSource([Process(42, "ChatGPT.exe", commandLine)]));

        var candidates = await source.GetCandidatesAsync(CodexCompatibilityTests.GetProfile());

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task TcpCandidateSource_AcceptsOnlyExactIpv4LoopbackListener()
    {
        var processes = new StubProcessSource(
            [Process(51, "ChatGPT.exe", string.Empty)]);
        var listeners = new StubListenerSource(
        [
            new TcpListenerSnapshot(51, IPAddress.Loopback, 9222),
            new TcpListenerSnapshot(51, IPAddress.IPv6Loopback, 9333),
            new TcpListenerSnapshot(51, IPAddress.Parse("127.0.0.2"), 9444),
            new TcpListenerSnapshot(51, IPAddress.Any, 9555),
        ]);
        var source = new LoopbackTcpCdpEndpointCandidateSource(processes, listeners);

        var candidate = Assert.Single(
            await source.GetCandidatesAsync(CodexCompatibilityTests.GetProfile()));

        Assert.Equal(new Uri("http://127.0.0.1:9222/"), candidate.BaseUri);
    }

    [Theory]
    [InlineData("--remote-debugging-port=0")]
    [InlineData("--remote-debugging-port=65536")]
    [InlineData("--remote-debugging-port=not-a-port")]
    [InlineData("--remote-debugging-port=-1")]
    public async Task GetCandidatesAsync_RejectsInvalidPorts(string commandLine)
    {
        var source = new CommandLineCdpEndpointCandidateSource(
            new StubProcessSource([Process(
                42,
                "ChatGPT.exe",
                $"{commandLine} --remote-debugging-address=127.0.0.1")]));

        var candidates = await source.GetCandidatesAsync(CodexCompatibilityTests.GetProfile());

        Assert.Empty(candidates);
    }

    private static CodexProcessSnapshot Process(
        int processId,
        string executable,
        string commandLine,
        string? family = null) => new(
        processId,
        executable,
        family ?? CodexCompatibilityCatalog.OfficialPackageFamilyName,
        CodexCompatibilityCatalog.SupportedPackageFullName,
        new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
        WindowsCodexProcessSnapshotSource.CurrentSessionId,
        commandLine);

    private sealed class StubProcessSource(IReadOnlyList<CodexProcessSnapshot> processes)
        : ICodexProcessSnapshotSource
    {
        public ValueTask<IReadOnlyList<CodexProcessSnapshot>> GetProcessesAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(processes);
        }
    }

    private sealed class StubListenerSource(IReadOnlyList<TcpListenerSnapshot> listeners)
        : ITcpListenerSnapshotSource
    {
        public IReadOnlyList<TcpListenerSnapshot> GetListeners() => listeners;
    }
}
