using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;

namespace BackdropForCodex.App.ViewModels;

/// <summary>
/// A path-minimizing projection of a durable wallpaper profile for the profile strip.
/// The full local path is retained only for the safe thumbnail converter and is never
/// included in accessible names or other user-facing text.
/// </summary>
public sealed record WallpaperProfileCardItem
{
    internal WallpaperProfileCardItem(
        Guid profileId,
        string name,
        Guid? mediaId,
        string? previewPath,
        MediaKind mediaKind,
        bool isMissing,
        string mediaDisplayName,
        string subtitle,
        string automationName,
        string actionsAutomationName)
    {
        ProfileId = profileId;
        Name = name;
        MediaId = mediaId;
        PreviewPath = previewPath;
        MediaKind = mediaKind;
        IsMissing = isMissing;
        MediaDisplayName = mediaDisplayName;
        Subtitle = subtitle;
        AutomationName = automationName;
        ActionsAutomationName = actionsAutomationName;
    }

    public Guid ProfileId { get; }

    public string Name { get; }

    public Guid? MediaId { get; }

    /// <summary>
    /// Local path consumed only by <see cref="Converters.MediaThumbnailConverter"/>.
    /// </summary>
    public string? PreviewPath { get; }

    public MediaKind MediaKind { get; }

    public bool IsOfficial => MediaId is null;

    public bool IsImagePreviewAvailable =>
        !IsMissing &&
        PreviewPath is not null &&
        MediaKind == MediaKind.Image;

    public bool IsVideo => !IsMissing && MediaKind == MediaKind.Video;

    public bool IsMissing { get; }

    public string MediaDisplayName { get; }

    public string Subtitle { get; }

    public string AutomationName { get; }

    public string ActionsAutomationName { get; }
}

/// <summary>
/// Converts a validated Settings V2 snapshot into immutable profile-strip items.
/// Runtime and editing state deliberately remain outside this projection.
/// </summary>
public sealed class WallpaperProfileCardProjection
{
    private readonly IAppTextProvider _text;
    private readonly ISafeMediaPreviewService _previewMedia;

    public WallpaperProfileCardProjection(
        IAppTextProvider text,
        ISafeMediaPreviewService? previewMedia = null)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _previewMedia = previewMedia ?? SafeMediaPreviewService.Shared;
    }

    public IReadOnlyList<WallpaperProfileCardItem> CreateItems(SettingsV2 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.CreateSnapshot();

        var mediaById = snapshot.MediaCatalog.ToDictionary(media => media.MediaId);
        var availabilityByMediaId = new Dictionary<Guid, bool>();
        var items = snapshot.Profiles
            .Select(
                profile => CreateItem(
                    profile,
                    mediaById,
                    availabilityByMediaId))
            .ToArray();
        return new ReadOnlyCollection<WallpaperProfileCardItem>(items);
    }

    private WallpaperProfileCardItem CreateItem(
        WallpaperProfile profile,
        Dictionary<Guid, MediaReference> mediaById,
        Dictionary<Guid, bool> availabilityByMediaId)
    {
        if (profile.MediaId is not { } mediaId)
        {
            var official = Text("Profile_Official", "Official background");
            return CreateCard(
                profile,
                mediaId: null,
                previewPath: null,
                mediaKind: MediaKind.None,
                isMissing: false,
                mediaDisplayName: official,
                subtitle: official);
        }

        var media = mediaById[mediaId];
        var previewPath = media.SourceKind == MediaSourceKind.LocalFile
            ? media.SourceIdentifier
            : null;
        var isMissing =
            previewPath is not null &&
            !IsAvailable(mediaId, previewPath, availabilityByMediaId);
        var subtitle = isMissing
            ? Text("Profile_MediaMissing", "Media missing")
            : media.LastKnownKind switch
            {
                MediaKind.Image => Text("Media_Image", "Image"),
                MediaKind.Video => Text("Media_Video", "Video"),
                _ => Text("Profile_Media", "Media"),
            };
        var mediaDisplayName = previewPath is null
            ? subtitle
            : Path.GetFileName(previewPath);

        return CreateCard(
            profile,
            mediaId,
            previewPath,
            media.LastKnownKind,
            isMissing,
            mediaDisplayName,
            subtitle);
    }

    private bool IsAvailable(
        Guid mediaId,
        string previewPath,
        Dictionary<Guid, bool> availabilityByMediaId)
    {
        if (availabilityByMediaId.TryGetValue(mediaId, out var isAvailable))
        {
            return isAvailable;
        }

        isAvailable = _previewMedia.IsAvailable(previewPath);
        availabilityByMediaId.Add(mediaId, isAvailable);
        return isAvailable;
    }

    private WallpaperProfileCardItem CreateCard(
        WallpaperProfile profile,
        Guid? mediaId,
        string? previewPath,
        MediaKind mediaKind,
        bool isMissing,
        string mediaDisplayName,
        string subtitle)
    {
        var automationName = Format(
            "Profile_AutomationName",
            "{0}, {1}",
            profile.Name,
            subtitle);
        var actionsAutomationName = Format(
            "Profile_ActionsAutomationName",
            "More actions for {0}",
            profile.Name);

        return new WallpaperProfileCardItem(
            profile.ProfileId,
            profile.Name,
            mediaId,
            previewPath,
            mediaKind,
            isMissing,
            mediaDisplayName,
            subtitle,
            automationName,
            actionsAutomationName);
    }

    private string Text(string key, string fallback)
    {
        var value = _text.GetString(key);
        return string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private string Format(string key, string fallback, params object[] arguments) =>
        string.Format(
            CultureInfo.CurrentCulture,
            Text(key, fallback),
            arguments);
}
