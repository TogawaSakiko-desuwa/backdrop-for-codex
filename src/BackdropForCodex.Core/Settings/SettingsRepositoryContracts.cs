namespace BackdropForCodex.Core.Settings;

public enum SettingsRecoveryReason
{
    InvalidDocument = 0,
    DocumentTooLarge,
    DocumentCouldNotBeRead,
    Version1BackupConflict,
    Version1BackupInvalid,
    Version2BackupConflict,
    MigrationFailed,
}

/// <summary>
/// A load result deliberately separates usable settings from user-directed recovery states.
/// Neither recovery nor future-schema results may be saved over automatically.
/// </summary>
public abstract record SettingsLoadResult
{
    private SettingsLoadResult()
    {
    }

    public sealed record Ready(
        SettingsV3 Settings,
        bool MigratedFromVersion1,
        bool MigratedFromVersion2 = false) :
        SettingsLoadResult;

    public sealed record RecoveryRequired(
        SettingsRecoveryReason Reason,
        bool HasVersion1Backup,
        bool HasVersion2Backup = false) :
        SettingsLoadResult;

    public sealed record FutureReadOnly(
        int SchemaVersion,
        bool HasVersion1Backup,
        bool HasVersion2Backup = false) : SettingsLoadResult;
}

/// <summary>
/// Owns serialized access to the settings document. Recovery-required and future-schema
/// documents remain read-only until an explicit restore or reset, while successful V3
/// publications complete only after same-directory atomic replacement.
/// </summary>
public interface ISettingsRepository : IDisposable
{
    bool HasVersion1Backup { get; }

    bool HasVersion2Backup { get; }

    /// <summary>
    /// Loads or migrates the document without replacing invalid, unreadable, or future-schema
    /// content. V1/V2 migration first preserves the original backup and publishes V3 atomically.
    /// </summary>
    Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Canonicalizes and atomically publishes V3 settings. An existing document must still be
    /// the same valid V3 document inspected before publication; recovery and future versions
    /// are refused rather than overwritten. Successful return is the persistence commit point.
    /// </summary>
    Task<SettingsV3> SaveAsync(
        SettingsV3 settings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly replaces the current document with a V3 migration of the preserved V1 backup.
    /// The V1 backup itself remains untouched, and a Ready result is returned only after atomic
    /// publication; validation or migration failure remains an explicit recovery result.
    /// </summary>
    Task<SettingsLoadResult> RestoreVersion1BackupAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly deletes this repository's settings and V1 backup, then returns fresh defaults.
    /// App-owned recovery exports must be removed by the application as part of the same user action.
    /// </summary>
    Task<SettingsV3> ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class SettingsRepositoryException : IOException
{
    public SettingsRepositoryException(string message)
        : base(message)
    {
    }

    public SettingsRepositoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
