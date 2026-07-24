using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Runtime;

namespace BackdropForCodex.App.Services.Diagnostics;

public enum DiagnosticCapabilityCode
{
    GlobalBackground = 0,
    SemanticRegions,
    GlassStyling,
    Audio,
    AdvancedSurfaces,
}

public enum DiagnosticCapabilityState
{
    Available = 0,
    Unavailable,
    Degraded,
}

public enum DiagnosticCapabilityReason
{
    AvailableFromExactProbePackage = 0,
    AvailableFromGenericProbePackage,
    DisabledByExactProbePackage,
    NotImplemented,
    StructuralProbeFailed,
    DependencyUnavailable,
    SecurityRejected,
    DisabledForGeneration,
    AvailableFromReviewedBandProbePackage,
    UnavailableForGenericProbePackage,
}

public sealed record DiagnosticCapabilitySnapshot(
    DiagnosticCapabilityCode Capability,
    DiagnosticCapabilityState State,
    DiagnosticCapabilityReason Reason);

public sealed record DiagnosticRuntimeSnapshot(
    WallpaperRuntimePhase Phase,
    bool IsActive,
    bool IsPaused,
    IReadOnlyList<DiagnosticCapabilitySnapshot> Capabilities)
{
    public static DiagnosticRuntimeSnapshot Idle { get; } = new(
        WallpaperRuntimePhase.Idle,
        IsActive: false,
        IsPaused: false,
        Array.Empty<DiagnosticCapabilitySnapshot>());
}

public sealed record DiagnosticEnvironmentSnapshot(
    string ApplicationVersion,
    string OperatingSystemVersion,
    string ProcessArchitecture,
    string FrameworkDescription)
{
    public static DiagnosticEnvironmentSnapshot CreateCurrent()
    {
        var version = typeof(DiagnosticEnvironmentSnapshot).Assembly
            .GetName()
            .Version?
            .ToString(3) ?? "0.0.0";

        return new DiagnosticEnvironmentSnapshot(
            version,
            Environment.OSVersion.Version.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription);
    }
}

public sealed record DiagnosticReportV1(
    int SchemaVersion,
    DiagnosticEnvironmentSnapshot Environment,
    DiagnosticRuntimeSnapshot Runtime);

public interface IDiagnosticReportService
{
    DiagnosticRuntimeSnapshot CreateRuntimeSnapshot(
        WallpaperRuntimePhase phase,
        bool isActive,
        bool isPaused,
        CompatibilityCapabilities? capabilities);

    DiagnosticReportV1 CreateReport(
        DiagnosticRuntimeSnapshot runtime,
        DiagnosticEnvironmentSnapshot? environment = null);

    string Serialize(DiagnosticReportV1 report);

    Task WriteAsync(
        string destinationPath,
        DiagnosticReportV1 report,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Produces an allow-listed, path-free diagnostic document only when explicitly requested.
/// </summary>
public sealed class DiagnosticReportService : IDiagnosticReportService
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public DiagnosticRuntimeSnapshot CreateRuntimeSnapshot(
        WallpaperRuntimePhase phase,
        bool isActive,
        bool isPaused,
        CompatibilityCapabilities? capabilities)
    {
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (capabilities is null)
        {
            return new DiagnosticRuntimeSnapshot(
                phase,
                isActive,
                isPaused,
                Enum.GetValues<DiagnosticCapabilityCode>()
                    .Select(code => new DiagnosticCapabilitySnapshot(
                        code,
                        DiagnosticCapabilityState.Unavailable,
                        DiagnosticCapabilityReason.DependencyUnavailable))
                    .ToArray());
        }

        var globalAvailable = capabilities.GlobalBackground.IsAvailable;
        return new DiagnosticRuntimeSnapshot(
            phase,
            isActive,
            isPaused,
            [
                Map(
                    DiagnosticCapabilityCode.GlobalBackground,
                    capabilities.GlobalBackground,
                    globalAvailable),
                Map(
                    DiagnosticCapabilityCode.SemanticRegions,
                    capabilities.RegionRecognition,
                    globalAvailable),
                Map(
                    DiagnosticCapabilityCode.GlassStyling,
                    capabilities.GlassStyle,
                    globalAvailable),
                Map(
                    DiagnosticCapabilityCode.Audio,
                    capabilities.Audio,
                    globalAvailable),
                Map(
                    DiagnosticCapabilityCode.AdvancedSurfaces,
                    capabilities.AdvancedSurfaces,
                    globalAvailable),
            ]);
    }

    public DiagnosticReportV1 CreateReport(
        DiagnosticRuntimeSnapshot runtime,
        DiagnosticEnvironmentSnapshot? environment = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(runtime.Capabilities);

        var capabilities = runtime.Capabilities
            .OrderBy(item => item.Capability)
            .ToArray();
        if (capabilities.Select(item => item.Capability).Distinct().Count() != capabilities.Length)
        {
            throw new ArgumentException(
                "A diagnostic capability can appear at most once.",
                nameof(runtime));
        }

        var snapshot = runtime with
        {
            Capabilities = Array.AsReadOnly(capabilities),
        };

        return new DiagnosticReportV1(
            CurrentSchemaVersion,
            environment ?? DiagnosticEnvironmentSnapshot.CreateCurrent(),
            snapshot);
    }

    public string Serialize(DiagnosticReportV1 report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException(
                $"Diagnostic schema version must be {CurrentSchemaVersion}.",
                nameof(report));
        }

        return JsonSerializer.Serialize(report, SerializerOptions);
    }

    public async Task WriteAsync(
        string destinationPath,
        DiagnosticReportV1 report,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        _ = Serialize(report);
        var fullPath = Path.GetFullPath(destinationPath);
        var directoryPath = Path.GetDirectoryName(fullPath)
            ?? throw new IOException("The diagnostic destination has no parent directory.");
        Directory.CreateDirectory(directoryPath);

        var temporaryPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    report,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullPath))
            {
                File.Replace(
                    temporaryPath,
                    fullPath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }

            temporaryPath = string.Empty;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath))
            {
                TryDelete(temporaryPath);
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static DiagnosticCapabilitySnapshot Map(
        DiagnosticCapabilityCode code,
        CompatibilityCapability capability,
        bool globalAvailable)
    {
        var state = capability.IsAvailable
            ? DiagnosticCapabilityState.Available
            : capability.ReasonCode is
              CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease or
              CompatibilityCapabilityReasonCode.UnavailableForGenericProbePackage
                ? DiagnosticCapabilityState.Unavailable
            : globalAvailable && code != DiagnosticCapabilityCode.GlobalBackground
                ? DiagnosticCapabilityState.Degraded
                : DiagnosticCapabilityState.Unavailable;
        var reason = capability.ReasonCode switch
        {
            CompatibilityCapabilityReasonCode.AvailableFromExactProbePackage =>
                DiagnosticCapabilityReason.AvailableFromExactProbePackage,
            CompatibilityCapabilityReasonCode.AvailableFromGenericProbePackage =>
                DiagnosticCapabilityReason.AvailableFromGenericProbePackage,
            CompatibilityCapabilityReasonCode.AvailableFromReviewedBandProbePackage =>
                DiagnosticCapabilityReason.AvailableFromReviewedBandProbePackage,
            CompatibilityCapabilityReasonCode.DisabledByExactProbePackage =>
                DiagnosticCapabilityReason.DisabledByExactProbePackage,
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease =>
                DiagnosticCapabilityReason.NotImplemented,
            CompatibilityCapabilityReasonCode.UnavailableForGenericProbePackage =>
                DiagnosticCapabilityReason.UnavailableForGenericProbePackage,
            CompatibilityCapabilityReasonCode.StructuralProbeFailed =>
                DiagnosticCapabilityReason.StructuralProbeFailed,
            CompatibilityCapabilityReasonCode.SecurityRejected =>
                DiagnosticCapabilityReason.SecurityRejected,
            CompatibilityCapabilityReasonCode.DisabledForGeneration or
            CompatibilityCapabilityReasonCode.None =>
                DiagnosticCapabilityReason.DisabledForGeneration,
            _ => throw new ArgumentOutOfRangeException(
                nameof(capability),
                capability.ReasonCode,
                "Unsupported compatibility capability reason."),
        };
        return new DiagnosticCapabilitySnapshot(code, state, reason);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of an unpublished private temporary file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of an unpublished private temporary file.
        }
    }
}
