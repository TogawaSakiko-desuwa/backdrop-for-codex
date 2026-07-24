namespace BackdropForCodex.Core.Settings;

public enum SettingsRecoveryReason
{
    InvalidDocument = 0,
    DocumentTooLarge,
    DocumentCouldNotBeRead,
    Version1BackupConflict,
    Version1BackupInvalid,
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

    public sealed record Ready(SettingsV2 Settings, bool MigratedFromVersion1) :
        SettingsLoadResult;

    public sealed record RecoveryRequired(
        SettingsRecoveryReason Reason,
        bool HasVersion1Backup) :
        SettingsLoadResult;

    public sealed record FutureReadOnly(
        int SchemaVersion,
        bool HasVersion1Backup) : SettingsLoadResult;
}

public interface ISettingsRepository : IDisposable
{
    bool HasVersion1Backup { get; }

    Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task<SettingsV2> SaveAsync(
        SettingsV2 settings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly replaces the current document with a V2 migration of the preserved V1 backup.
    /// The V1 backup itself remains untouched.
    /// </summary>
    Task<SettingsLoadResult> RestoreVersion1BackupAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly deletes this repository's settings and V1 backup, then returns fresh defaults.
    /// App-owned recovery exports must be removed by the application as part of the same user action.
    /// </summary>
    Task<SettingsV2> ResetAsync(CancellationToken cancellationToken = default);
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
