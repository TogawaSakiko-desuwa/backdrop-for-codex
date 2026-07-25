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

public enum DiagnosticCapabilityReason
{
    AvailableFromGlobalBaseline = 0,
    AvailableFromPresentationContract,
    NotImplementedInCurrentRelease,
    NoMatchingPresentationContract,
    AmbiguousPresentationContract,
    StructuralProbeFailed,
    SecurityRejected,
    DisabledForGeneration,
}

public sealed record DiagnosticCapabilitySnapshot(
    DiagnosticCapabilityCode Capability,
    bool IsEnabled,
    DiagnosticCapabilityReason Reason);

public sealed record DiagnosticSecuritySnapshot(
    CodexSecurityStatus Status,
    CodexSecurityStage Stage,
    CodexSecurityFailureCode FailureCode);

public sealed record DiagnosticCompatibilitySnapshot(
    string? CodexVersion,
    DiagnosticSecuritySnapshot Security,
    string? PresentationContractId,
    ContractMatchState ContractMatchState,
    IReadOnlyList<DiagnosticCapabilitySnapshot> Capabilities);

public sealed record DiagnosticRuntimeSnapshot(
    WallpaperRuntimePhase Phase,
    bool IsActive,
    bool IsPaused)
{
    public static DiagnosticRuntimeSnapshot Idle { get; } = new(
        WallpaperRuntimePhase.Idle,
        IsActive: false,
        IsPaused: false);
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

public sealed record DiagnosticReportV2(
    int SchemaVersion,
    DiagnosticEnvironmentSnapshot Environment,
    DiagnosticRuntimeSnapshot Runtime,
    DiagnosticCompatibilitySnapshot Compatibility);

public interface IDiagnosticReportService
{
    DiagnosticRuntimeSnapshot CreateRuntimeSnapshot(
        WallpaperRuntimePhase phase,
        bool isActive,
        bool isPaused);

    DiagnosticCompatibilitySnapshot CreateCompatibilitySnapshot(
        WallpaperCompatibilitySnapshot compatibility);

    DiagnosticReportV2 CreateReport(
        DiagnosticRuntimeSnapshot runtime,
        DiagnosticCompatibilitySnapshot compatibility,
        DiagnosticEnvironmentSnapshot? environment = null);

    string Serialize(DiagnosticReportV2 report);

    Task WriteAsync(
        string destinationPath,
        DiagnosticReportV2 report,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Produces an allow-listed, path-free diagnostic document only when explicitly requested.
/// </summary>
public sealed class DiagnosticReportService : IDiagnosticReportService
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public DiagnosticRuntimeSnapshot CreateRuntimeSnapshot(
        WallpaperRuntimePhase phase,
        bool isActive,
        bool isPaused)
    {
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        return new DiagnosticRuntimeSnapshot(phase, isActive, isPaused);
    }

    public DiagnosticCompatibilitySnapshot CreateCompatibilitySnapshot(
        WallpaperCompatibilitySnapshot compatibility)
    {
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(compatibility.Security);
        ArgumentNullException.ThrowIfNull(compatibility.Presentation);
        ArgumentNullException.ThrowIfNull(compatibility.Capabilities);

        return new DiagnosticCompatibilitySnapshot(
            compatibility.CodexVersion?.ToString(),
            new DiagnosticSecuritySnapshot(
                compatibility.Security.Status,
                compatibility.Security.Stage,
                compatibility.Security.FailureCode),
            AllowListedContractId(compatibility.Presentation.ActiveContractId),
            compatibility.Presentation.MatchState,
            Array.AsReadOnly(
            [
                Map(
                    DiagnosticCapabilityCode.GlobalBackground,
                    compatibility.Capabilities.GlobalBackground),
                Map(
                    DiagnosticCapabilityCode.SemanticRegions,
                    compatibility.Capabilities.RegionRecognition),
                Map(
                    DiagnosticCapabilityCode.GlassStyling,
                    compatibility.Capabilities.GlassStyle),
                Map(
                    DiagnosticCapabilityCode.Audio,
                    compatibility.Capabilities.Audio),
                Map(
                    DiagnosticCapabilityCode.AdvancedSurfaces,
                    compatibility.Capabilities.AdvancedSurfaces),
            ]));
    }

    public DiagnosticReportV2 CreateReport(
        DiagnosticRuntimeSnapshot runtime,
        DiagnosticCompatibilitySnapshot compatibility,
        DiagnosticEnvironmentSnapshot? environment = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(compatibility.Security);
        ArgumentNullException.ThrowIfNull(compatibility.Capabilities);

        var capabilities = compatibility.Capabilities
            .OrderBy(item => item.Capability)
            .ToArray();
        if (capabilities.Select(item => item.Capability).Distinct().Count() !=
            capabilities.Length)
        {
            throw new ArgumentException(
                "A diagnostic capability can appear at most once.",
                nameof(compatibility));
        }

        var compatibilitySnapshot = compatibility with
        {
            PresentationContractId =
                AllowListedContractId(compatibility.PresentationContractId),
            Capabilities = Array.AsReadOnly(capabilities),
        };

        return new DiagnosticReportV2(
            CurrentSchemaVersion,
            environment ?? DiagnosticEnvironmentSnapshot.CreateCurrent(),
            runtime,
            compatibilitySnapshot);
    }

    public string Serialize(DiagnosticReportV2 report)
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
        DiagnosticReportV2 report,
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
        CompatibilityCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var reason = capability.ReasonCode switch
        {
            CompatibilityCapabilityReasonCode.AvailableFromGlobalBaseline =>
                DiagnosticCapabilityReason.AvailableFromGlobalBaseline,
            CompatibilityCapabilityReasonCode.AvailableFromPresentationContract =>
                DiagnosticCapabilityReason.AvailableFromPresentationContract,
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease =>
                DiagnosticCapabilityReason.NotImplementedInCurrentRelease,
            CompatibilityCapabilityReasonCode.NoMatchingPresentationContract =>
                DiagnosticCapabilityReason.NoMatchingPresentationContract,
            CompatibilityCapabilityReasonCode.AmbiguousPresentationContract =>
                DiagnosticCapabilityReason.AmbiguousPresentationContract,
            CompatibilityCapabilityReasonCode.StructuralProbeFailed =>
                DiagnosticCapabilityReason.StructuralProbeFailed,
            CompatibilityCapabilityReasonCode.SecurityRejected =>
                DiagnosticCapabilityReason.SecurityRejected,
            CompatibilityCapabilityReasonCode.DisabledForGeneration =>
                DiagnosticCapabilityReason.DisabledForGeneration,
            _ => throw new ArgumentOutOfRangeException(
                nameof(capability),
                capability.ReasonCode,
                "Unsupported compatibility capability reason."),
        };
        return new DiagnosticCapabilitySnapshot(code, capability.IsAvailable, reason);
    }

    private static string? AllowListedContractId(string? contractId)
    {
        if (string.Equals(
                contractId,
                PresentationContractCatalog.GlobalBaselineId,
                StringComparison.Ordinal))
        {
            return PresentationContractCatalog.GlobalBaselineId;
        }

        return string.Equals(
                contractId,
                PresentationContractCatalog.CodexShellId,
                StringComparison.Ordinal)
            ? PresentationContractCatalog.CodexShellId
            : null;
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
