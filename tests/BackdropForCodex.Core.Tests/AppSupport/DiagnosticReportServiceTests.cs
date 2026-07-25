using System.Text.Json;
using BackdropForCodex.App.Services.Diagnostics;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Runtime;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class DiagnosticReportServiceTests
{
    [Fact]
    public void CreateReport_UsesOnlyTypedAllowListedRuntimeAndCompatibilityData()
    {
        const string sensitiveReason =
            "https://localhost:9222/json C:\\private\\wallpaper.png " +
            "main[data-secret] raw DOM and page title";
        var service = new DiagnosticReportService();
        var runtime = service.CreateRuntimeSnapshot(
            WallpaperRuntimePhase.Active,
            isActive: true,
            isPaused: false);
        var compatibility = service.CreateCompatibilitySnapshot(
            new WallpaperCompatibilitySnapshot(
                new Version(26, 805, 14, 3),
                CodexSecurityResult.Rejected(
                    CodexSecurityStage.TargetValidation,
                    CodexSecurityFailureCode.NoVerifiedTarget,
                    sensitiveReason),
                new PresentationContractSnapshot(
                    PresentationContractCatalog.CodexShellId,
                    ContractMatchState.Matched),
                CreateMixedCapabilities()));
        var environment = new DiagnosticEnvironmentSnapshot(
            "1.3.3",
            "10.0.26100.0",
            "X64",
            ".NET 10.0.0");

        var json = service.Serialize(
            service.CreateReport(runtime, compatibility, environment));
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            ["compatibility", "environment", "runtime", "schemaVersion"],
            PropertyNames(document.RootElement));
        Assert.Equal(
            [
                "applicationVersion",
                "frameworkDescription",
                "operatingSystemVersion",
                "processArchitecture",
            ],
            PropertyNames(document.RootElement.GetProperty("environment")));
        Assert.Equal(
            ["isActive", "isPaused", "phase"],
            PropertyNames(document.RootElement.GetProperty("runtime")));
        var compatibilityElement = document.RootElement.GetProperty("compatibility");
        Assert.Equal(
            [
                "capabilities",
                "codexVersion",
                "contractMatchState",
                "presentationContractId",
                "security",
            ],
            PropertyNames(compatibilityElement));
        Assert.Equal(
            ["failureCode", "stage", "status"],
            PropertyNames(compatibilityElement.GetProperty("security")));
        Assert.All(
            compatibilityElement.GetProperty("capabilities").EnumerateArray(),
            capability => Assert.Equal(
                ["capability", "isEnabled", "reason"],
                PropertyNames(capability)));

        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            "Active",
            document.RootElement.GetProperty("runtime").GetProperty("phase").GetString());
        Assert.Equal("26.805.14.3", compatibilityElement.GetProperty("codexVersion").GetString());
        Assert.Equal(
            "Rejected",
            compatibilityElement.GetProperty("security").GetProperty("status").GetString());
        Assert.Equal(
            "TargetValidation",
            compatibilityElement.GetProperty("security").GetProperty("stage").GetString());
        Assert.Equal(
            "NoVerifiedTarget",
            compatibilityElement.GetProperty("security").GetProperty("failureCode").GetString());
        Assert.Equal(
            PresentationContractCatalog.CodexShellId,
            compatibilityElement.GetProperty("presentationContractId").GetString());
        Assert.Equal(
            "Matched",
            compatibilityElement.GetProperty("contractMatchState").GetString());

        Assert.DoesNotContain(sensitiveReason, json, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:9222", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private\\\\wallpaper.png", json, StringComparison.Ordinal);
        Assert.DoesNotContain("data-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("raw DOM", json, StringComparison.Ordinal);
        Assert.DoesNotContain("page title", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCompatibilitySnapshot_MapsIndependentCapabilities()
    {
        var service = new DiagnosticReportService();

        var snapshot = service.CreateCompatibilitySnapshot(
            new WallpaperCompatibilitySnapshot(
                CodexVersion: null,
                CodexSecurityResult.NotEvaluated(),
                PresentationContractSnapshot.NotEvaluated,
                CreateMixedCapabilities()));

        Assert.Collection(
            snapshot.Capabilities,
            global =>
            {
                Assert.Equal(DiagnosticCapabilityCode.GlobalBackground, global.Capability);
                Assert.True(global.IsEnabled);
                Assert.Equal(
                    DiagnosticCapabilityReason.AvailableFromGlobalBaseline,
                    global.Reason);
            },
            regions =>
            {
                Assert.Equal(DiagnosticCapabilityCode.SemanticRegions, regions.Capability);
                Assert.False(regions.IsEnabled);
                Assert.Equal(
                    DiagnosticCapabilityReason.NotImplementedInCurrentRelease,
                    regions.Reason);
            },
            glass =>
            {
                Assert.Equal(DiagnosticCapabilityCode.GlassStyling, glass.Capability);
                Assert.True(glass.IsEnabled);
                Assert.Equal(
                    DiagnosticCapabilityReason.AvailableFromPresentationContract,
                    glass.Reason);
            },
            audio =>
            {
                Assert.Equal(DiagnosticCapabilityCode.Audio, audio.Capability);
                Assert.False(audio.IsEnabled);
                Assert.Equal(
                    DiagnosticCapabilityReason.NotImplementedInCurrentRelease,
                    audio.Reason);
            },
            advanced =>
            {
                Assert.Equal(DiagnosticCapabilityCode.AdvancedSurfaces, advanced.Capability);
                Assert.False(advanced.IsEnabled);
                Assert.Equal(
                    DiagnosticCapabilityReason.StructuralProbeFailed,
                    advanced.Reason);
            });
    }

    [Fact]
    public void CreateCompatibilitySnapshot_AllowListsPresentationContractIds()
    {
        const string untrustedContract =
            "main[data-private] https://localhost C:\\private\\media.png";
        var service = new DiagnosticReportService();
        var compatibility = service.CreateCompatibilitySnapshot(
            WallpaperCompatibilitySnapshot.NotEvaluated with
            {
                Presentation = new PresentationContractSnapshot(
                    untrustedContract,
                    ContractMatchState.Matched),
            });

        Assert.Null(compatibility.PresentationContractId);

        var report = service.CreateReport(
            DiagnosticRuntimeSnapshot.Idle,
            compatibility,
            new DiagnosticEnvironmentSnapshot(
                "1.3.3",
                "10.0.26100.0",
                "X64",
                ".NET 10.0.0"));
        var json = service.Serialize(report);

        Assert.DoesNotContain(untrustedContract, json, StringComparison.Ordinal);
        Assert.DoesNotContain("data-private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private\\\\media.png", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateReport_RejectsDuplicateCapabilityCodes()
    {
        var service = new DiagnosticReportService();
        var duplicate = new DiagnosticCapabilitySnapshot(
            DiagnosticCapabilityCode.GlobalBackground,
            IsEnabled: true,
            DiagnosticCapabilityReason.AvailableFromGlobalBaseline);
        var compatibility = new DiagnosticCompatibilitySnapshot(
            CodexVersion: null,
            new DiagnosticSecuritySnapshot(
                CodexSecurityStatus.NotEvaluated,
                CodexSecurityStage.None,
                CodexSecurityFailureCode.None),
            PresentationContractId: null,
            ContractMatchState.NotEvaluated,
            [duplicate, duplicate]);

        Assert.Throws<ArgumentException>(
            () => service.CreateReport(DiagnosticRuntimeSnapshot.Idle, compatibility));
    }

    [Fact]
    public void CreateCompatibilitySnapshot_NotEvaluatedUsesTypedDisabledReason()
    {
        var service = new DiagnosticReportService();

        var snapshot = service.CreateCompatibilitySnapshot(
            WallpaperCompatibilitySnapshot.NotEvaluated);

        Assert.Null(snapshot.CodexVersion);
        Assert.Equal(CodexSecurityStatus.NotEvaluated, snapshot.Security.Status);
        Assert.Equal(CodexSecurityStage.None, snapshot.Security.Stage);
        Assert.Equal(CodexSecurityFailureCode.None, snapshot.Security.FailureCode);
        Assert.Null(snapshot.PresentationContractId);
        Assert.Equal(ContractMatchState.NotEvaluated, snapshot.ContractMatchState);
        Assert.Equal(5, snapshot.Capabilities.Count);
        Assert.All(
            snapshot.Capabilities,
            item =>
            {
                Assert.False(item.IsEnabled);
                Assert.Equal(
                    DiagnosticCapabilityReason.DisabledForGeneration,
                    item.Reason);
            });
    }

    [Fact]
    public async Task WriteAsync_AtomicallyReplacesAnExistingReport()
    {
        var directory = Directory.CreateTempSubdirectory("backdrop-diagnostics-");
        try
        {
            var destination = Path.Combine(directory.FullName, "diagnostic.json");
            await File.WriteAllTextAsync(destination, "old");
            var service = new DiagnosticReportService();
            var report = service.CreateReport(
                DiagnosticRuntimeSnapshot.Idle,
                service.CreateCompatibilitySnapshot(
                    WallpaperCompatibilitySnapshot.NotEvaluated),
                new DiagnosticEnvironmentSnapshot(
                    "1.3.3",
                    "10.0.26100.0",
                    "X64",
                    ".NET 10.0.0"));

            await service.WriteAsync(destination, report);

            var json = await File.ReadAllTextAsync(destination);
            Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);
            Assert.Empty(directory.GetFiles("*.tmp"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static CompatibilityCapabilities CreateMixedCapabilities() => new(
        new(
            true,
            CompatibilityCapabilityReasonCode.AvailableFromGlobalBaseline),
        new(
            false,
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease),
        new(
            true,
            CompatibilityCapabilityReasonCode.AvailableFromPresentationContract),
        new(
            false,
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease),
        new(
            false,
            CompatibilityCapabilityReasonCode.StructuralProbeFailed));

    private static string[] PropertyNames(JsonElement element) =>
        element
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
