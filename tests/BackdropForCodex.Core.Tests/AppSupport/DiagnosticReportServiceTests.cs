using System.Text.Json;
using BackdropForCodex.App.Services.Diagnostics;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Runtime;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class DiagnosticReportServiceTests
{
    [Fact]
    public void CreateReport_UsesOnlyTypedAllowListedRuntimeData()
    {
        var service = new DiagnosticReportService();
        var runtime = new DiagnosticRuntimeSnapshot(
            WallpaperRuntimePhase.Active,
            IsActive: true,
            IsPaused: false,
            [
                new(
                    DiagnosticCapabilityCode.GlassStyling,
                    DiagnosticCapabilityState.Degraded,
                    DiagnosticCapabilityReason.StructuralProbeFailed),
                new(
                    DiagnosticCapabilityCode.GlobalBackground,
                    DiagnosticCapabilityState.Available,
                    DiagnosticCapabilityReason.AvailableFromExactProbePackage),
            ]);
        var environment = new DiagnosticEnvironmentSnapshot(
            "1.3.0",
            "10.0.26100.0",
            "X64",
            ".NET 10.0.0");

        var json = service.Serialize(service.CreateReport(runtime, environment));
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            ["environment", "runtime", "schemaVersion"],
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
            ["capabilities", "isActive", "isPaused", "phase"],
            PropertyNames(document.RootElement.GetProperty("runtime")));
        Assert.All(
            document.RootElement
                .GetProperty("runtime")
                .GetProperty("capabilities")
                .EnumerateArray(),
            capability => Assert.Equal(
                ["capability", "reason", "state"],
                PropertyNames(capability)));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            "Active",
            document.RootElement.GetProperty("runtime").GetProperty("phase").GetString());
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("title", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("targetId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mediaId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profileId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateReport_RejectsDuplicateCapabilityCodes()
    {
        var service = new DiagnosticReportService();
        var duplicate = new DiagnosticCapabilitySnapshot(
            DiagnosticCapabilityCode.GlobalBackground,
            DiagnosticCapabilityState.Available,
            DiagnosticCapabilityReason.AvailableFromExactProbePackage);
        var runtime = new DiagnosticRuntimeSnapshot(
            WallpaperRuntimePhase.Active,
            IsActive: true,
            IsPaused: false,
            [duplicate, duplicate]);

        Assert.Throws<ArgumentException>(() => service.CreateReport(runtime));
    }

    [Fact]
    public void CreateRuntimeSnapshot_MapsIndependentCapabilitiesWithoutSensitiveStrings()
    {
        var service = new DiagnosticReportService();
        var capabilities = new CompatibilityCapabilities(
            new(
                true,
                CompatibilityCapabilityReasonCode.AvailableFromGenericProbePackage),
            new(
                false,
                CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease),
            new(
                false,
                CompatibilityCapabilityReasonCode.StructuralProbeFailed),
            new(
                false,
                CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease),
            new(
                true,
                CompatibilityCapabilityReasonCode.AvailableFromGenericProbePackage));

        var snapshot = service.CreateRuntimeSnapshot(
            WallpaperRuntimePhase.Active,
            isActive: true,
            isPaused: false,
            capabilities);

        Assert.Collection(
            snapshot.Capabilities,
            global =>
            {
                Assert.Equal(DiagnosticCapabilityCode.GlobalBackground, global.Capability);
                Assert.Equal(DiagnosticCapabilityState.Available, global.State);
                Assert.Equal(
                    DiagnosticCapabilityReason.AvailableFromGenericProbePackage,
                    global.Reason);
            },
            regions => Assert.Equal(DiagnosticCapabilityState.Unavailable, regions.State),
            glass =>
            {
                Assert.Equal(DiagnosticCapabilityState.Degraded, glass.State);
                Assert.Equal(DiagnosticCapabilityReason.StructuralProbeFailed, glass.Reason);
            },
            audio => Assert.Equal(DiagnosticCapabilityState.Unavailable, audio.State),
            advanced => Assert.Equal(DiagnosticCapabilityState.Available, advanced.State));
    }

    [Fact]
    public void CreateRuntimeSnapshot_WithoutCapabilitySource_UsesTypedDependencyReason()
    {
        var service = new DiagnosticReportService();

        var snapshot = service.CreateRuntimeSnapshot(
            WallpaperRuntimePhase.Idle,
            isActive: false,
            isPaused: false,
            capabilities: null);

        Assert.Equal(5, snapshot.Capabilities.Count);
        Assert.All(
            snapshot.Capabilities,
            item =>
            {
                Assert.Equal(DiagnosticCapabilityState.Unavailable, item.State);
                Assert.Equal(DiagnosticCapabilityReason.DependencyUnavailable, item.Reason);
            });
    }

    [Fact]
    public void CreateRuntimeSnapshot_ReportsReviewedBandCapabilityEvidence()
    {
        var service = new DiagnosticReportService();
        var available = new CompatibilityCapability(
            true,
            CompatibilityCapabilityReasonCode.AvailableFromReviewedBandProbePackage);
        var unavailable = new CompatibilityCapability(
            false,
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease);
        var capabilities = new CompatibilityCapabilities(
            available,
            unavailable,
            available,
            unavailable,
            available);

        var snapshot = service.CreateRuntimeSnapshot(
            WallpaperRuntimePhase.Active,
            isActive: true,
            isPaused: false,
            capabilities);

        Assert.Equal(
            DiagnosticCapabilityReason.AvailableFromReviewedBandProbePackage,
            snapshot.Capabilities.Single(
                capability =>
                    capability.Capability == DiagnosticCapabilityCode.GlobalBackground).Reason);
        Assert.Equal(
            DiagnosticCapabilityReason.AvailableFromReviewedBandProbePackage,
            snapshot.Capabilities.Single(
                capability =>
                    capability.Capability == DiagnosticCapabilityCode.GlassStyling).Reason);
        Assert.Equal(
            DiagnosticCapabilityReason.AvailableFromReviewedBandProbePackage,
            snapshot.Capabilities.Single(
                capability =>
                    capability.Capability == DiagnosticCapabilityCode.AdvancedSurfaces).Reason);
    }

    [Fact]
    public void CreateRuntimeSnapshot_DistinguishesGenericPolicyFromUnimplementedFeatures()
    {
        var service = new DiagnosticReportService();
        var capabilities = BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests
            .GetProfile(new Version(26, 722, 0, 0))
            .Capabilities;

        var snapshot = service.CreateRuntimeSnapshot(
            WallpaperRuntimePhase.Active,
            isActive: true,
            isPaused: false,
            capabilities);

        Assert.Equal(
            DiagnosticCapabilityReason.NotImplemented,
            snapshot.Capabilities.Single(
                capability =>
                    capability.Capability == DiagnosticCapabilityCode.SemanticRegions).Reason);
        Assert.Equal(
            DiagnosticCapabilityReason.UnavailableForGenericProbePackage,
            snapshot.Capabilities.Single(
                capability =>
                    capability.Capability == DiagnosticCapabilityCode.GlassStyling).Reason);
        Assert.Equal(
            DiagnosticCapabilityReason.NotImplemented,
            snapshot.Capabilities.Single(
                capability => capability.Capability == DiagnosticCapabilityCode.Audio).Reason);
        Assert.Equal(
            DiagnosticCapabilityReason.UnavailableForGenericProbePackage,
            snapshot.Capabilities.Single(
                capability =>
                    capability.Capability == DiagnosticCapabilityCode.AdvancedSurfaces).Reason);
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
                new DiagnosticEnvironmentSnapshot(
                    "1.3.0",
                    "10.0.26100.0",
                    "X64",
                    ".NET 10.0.0"));

            await service.WriteAsync(destination, report);

            var json = await File.ReadAllTextAsync(destination);
            Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
            Assert.Empty(directory.GetFiles("*.tmp"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string[] PropertyNames(JsonElement element) =>
        element
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
