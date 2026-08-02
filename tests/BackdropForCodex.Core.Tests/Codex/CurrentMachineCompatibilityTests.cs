using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Tests.Infrastructure;
using PuppeteerSharp;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class CurrentMachineCompatibilityTests
{
    private const string OptInVariable = "BACKDROP_FOR_CODEX_RUN_MACHINE_TESTS";

    [IntegrationFact(OptInVariable)]
    [Trait("Category", "Integration")]
    public void InstalledStorePackage_PassesSecurityIdentityValidation_WhenOptedIn()
    {
        var package = new InstalledCodexPackageLocator().Locate();
        var result = CodexSecurityValidator.Validate(
            package.Descriptor,
            CodexRuntimeDescriptor.Current);

        Assert.True(result.IsVerified, result.Reason);
        Assert.Equal(result.Identity!.PackageFullName, package.PackageFullName);
        Assert.Equal(
            CodexSecurityValidator.OfficialPackageFamilyName,
            package.Descriptor.FamilyName);
        Assert.Equal("ChatGPT.exe", Path.GetFileName(package.ExecutablePath));
        Assert.True(File.Exists(package.ExecutablePath));
    }

    [IntegrationFact(OptInVariable)]
    [Trait("Category", "Integration")]
    public async Task RunningCodexProcesses_AreBoundToOfficialPackage_WhenOptedIn()
    {
        var package = new InstalledCodexPackageLocator().Locate();
        var security = CodexSecurityValidator.Validate(
            package.Descriptor,
            CodexRuntimeDescriptor.Current);
        Assert.True(security.IsVerified, security.Reason);
        var identity = security.Identity!;
        var processes = await new WindowsCodexProcessSnapshotSource().GetProcessesAsync();

        Assert.Contains(
            processes,
            process =>
                string.Equals(process.ExecutableName, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    process.PackageFamilyName,
                    identity.PackageFamilyName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    process.PackageFullName,
                    identity.PackageFullName,
                    StringComparison.Ordinal) &&
                process.StartTimeUtc != default &&
                process.SessionId == WindowsCodexProcessSnapshotSource.CurrentSessionId);
    }

    [IntegrationFact(OptInVariable)]
    [Trait("Category", "Integration")]
    public async Task RunningCodexPage_MatchesReviewedPresentationContract_WhenOptedIn()
    {
        var package = new InstalledCodexPackageLocator().Locate();
        var security = CodexSecurityValidator.Validate(
            package.Descriptor,
            CodexRuntimeDescriptor.Current);
        Assert.True(security.IsVerified, security.Reason);

        var processes = new WindowsCodexProcessSnapshotSource();
        var candidates = new LoopbackTcpCdpEndpointCandidateSource(
            processes,
            new WindowsTcpListenerSnapshotSource());
        using var transport = new HttpCdpJsonTransport(
            requestTimeout: TimeSpan.FromMilliseconds(750));
        var discovery = new CdpEndpointDiscovery(candidates, transport);
        var result = await discovery.DiscoverAsync(security.Identity!);
        var endpoint = Assert.Single(result.Endpoints);
        var reviewedTarget = Assert.Single(endpoint.InjectableTargets);

        var browser = await Puppeteer.ConnectAsync(new ConnectOptions
        {
            BrowserWSEndpoint = endpoint.BrowserWebSocketUri.AbsoluteUri,
            DefaultViewport = null,
            ProtocolTimeout = 5_000,
            AcceptInsecureCerts = false,
            NetworkEnabled = false,
        });
        try
        {
            var pages = await browser.PagesAsync(includeAll: true);
            var page = Assert.Single(
                pages,
                candidate =>
                    !candidate.IsClosed &&
                    Uri.TryCreate(candidate.Url, UriKind.Absolute, out var candidateUri) &&
                    VerifiedCodexPageSelector.IsSameReviewedDocument(
                        candidateUri,
                        reviewedTarget.Url));
            var evidenceJson = await page.EvaluateExpressionAsync<string>(
                PresentationEvidenceScriptBuilder.Build());
            var evidence = PresentationEvidenceScriptBuilder.Parse(evidenceJson);
            var decision = PresentationContractCatalog.Match(
                evidence,
                finalizeBaselineFallback: true);

            Assert.True(evidence.GlobalStructure);
            Assert.True(evidence.ShellStructure);
            Assert.Equal(ContractMatchState.Matched, decision.Snapshot.MatchState);
            Assert.Equal(
                PresentationContractCatalog.CodexShellId,
                decision.Snapshot.ActiveContractId);
            Assert.True(decision.Capabilities.Glass.IsAvailable);
            Assert.True(decision.Capabilities.Advanced.IsAvailable);
        }
        finally
        {
            browser.Disconnect();
        }
    }
}
