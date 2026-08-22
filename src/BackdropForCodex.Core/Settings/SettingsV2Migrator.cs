using System.Collections.ObjectModel;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.Core.Settings;

/// <summary>
/// Pure, metadata-only migration from the frozen V2 document into the V3 runtime contract.
/// Source files, Steam libraries, and providers are deliberately not accessed during migration.
/// </summary>
internal static class SettingsV2Migrator
{
    internal static SettingsV3 Migrate(SettingsV2 version2)
    {
        ArgumentNullException.ThrowIfNull(version2);
        var snapshot = version2.Snapshot();
        var mediaCatalog = snapshot.MediaCatalog
            .Select(SettingsV3.CreateVersion3MediaSnapshot)
            .ToArray();

        return new SettingsV3
        {
            Profiles = new ReadOnlyCollection<WallpaperProfile>(
                snapshot.Profiles.Select(profile => profile.Snapshot()).ToArray()),
            MediaCatalog = new ReadOnlyCollection<MediaReference>(mediaCatalog),
            RecentMediaIds = new ReadOnlyCollection<Guid>(snapshot.RecentMediaIds.ToArray()),
            RegionBindings = new ReadOnlyDictionary<SemanticRegion, Guid>(
                snapshot.RegionBindings.ToDictionary(
                    binding => binding.Key,
                    binding => binding.Value)),
            AcceptedCdpRisk = snapshot.AcceptedCdpRisk,
#pragma warning disable CS0618 // Preserve the deprecated value during schema migration.
            LastCompatibilityProfileId = snapshot.LastCompatibilityProfileId,
#pragma warning restore CS0618
        }.Snapshot();
    }
}
