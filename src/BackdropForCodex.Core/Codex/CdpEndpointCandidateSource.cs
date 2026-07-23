using System.Globalization;
using System.Text.RegularExpressions;

namespace BackdropForCodex.Core.Codex;

public interface ICodexProcessSnapshotSource
{
    ValueTask<IReadOnlyList<CodexProcessSnapshot>> GetProcessesAsync(
        CancellationToken cancellationToken = default);
}

public interface ICdpEndpointCandidateSource
{
    ValueTask<IReadOnlyList<CdpEndpointCandidate>> GetCandidatesAsync(
        CodexCompatibilityProfile profile,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Extracts explicit Chromium remote-debugging ports and IPv4 loopback bindings from already
/// trusted packaged processes.
/// It never scans or binds ports and therefore cannot disturb a running Codex instance.
/// </summary>
public sealed partial class CommandLineCdpEndpointCandidateSource : ICdpEndpointCandidateSource
{
    private readonly ICodexProcessSnapshotSource _processSource;

    public CommandLineCdpEndpointCandidateSource(ICodexProcessSnapshotSource processSource)
    {
        _processSource = processSource ?? throw new ArgumentNullException(nameof(processSource));
    }

    public async ValueTask<IReadOnlyList<CdpEndpointCandidate>> GetCandidatesAsync(
        CodexCompatibilityProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var processes = await _processSource.GetProcessesAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<CdpEndpointCandidate>();

        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ProcessId <= 0 ||
                !profile.IsKnownExecutable(process.ExecutableName) ||
                !string.Equals(
                    process.PackageFamilyName,
                    profile.PackageFamilyName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    process.PackageFullName,
                    profile.PackageFullName,
                    StringComparison.Ordinal) ||
                process.StartTimeUtc == default ||
                process.SessionId != WindowsCodexProcessSnapshotSource.CurrentSessionId ||
                string.IsNullOrWhiteSpace(process.CommandLine))
            {
                continue;
            }

            var addressMatches = RemoteDebuggingAddressRegex().Matches(process.CommandLine);
            var portMatches = RemoteDebuggingPortRegex().Matches(process.CommandLine);
            if (addressMatches.Count != 1 ||
                portMatches.Count != 1 ||
                !string.Equals(
                    addressMatches[0].Groups["host"].Value,
                    "127.0.0.1",
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (!ushort.TryParse(
                    portMatches[0].Groups["port"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var port) ||
                port == 0)
            {
                continue;
            }

            candidates.Add(new CdpEndpointCandidate(
                process.ProcessId,
                Path.GetFileName(process.ExecutableName),
                process.PackageFamilyName,
                process.PackageFullName,
                process.StartTimeUtc,
                process.SessionId,
                new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute)));
        }

        return candidates
            .DistinctBy(candidate => (candidate.ProcessId, candidate.BaseUri.Port))
            .OrderBy(candidate => candidate.ProcessId)
            .ThenBy(candidate => candidate.BaseUri.Port)
            .ToArray();
    }

    [GeneratedRegex(
        @"(?:^|\s)--remote-debugging-port(?:=|\s+)(?<port>\d{1,5})(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteDebuggingPortRegex();

    [GeneratedRegex(
        @"(?:^|\s)--remote-debugging-address(?:=|\s+)(?<host>[^\s\""']+)(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteDebuggingAddressRegex();
}
