using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace BackdropForCodex.Core.Media;

public interface IWallpaperSourceProviderRegistry
{
    IReadOnlyList<IWallpaperSourceProvider> Providers { get; }

    IReadOnlyList<MediaSourceKind> SourceKinds { get; }

    bool TryGet(
        MediaSourceKind sourceKind,
        [NotNullWhen(true)] out IWallpaperSourceProvider? provider);

    IWallpaperSourceProvider GetRequired(MediaSourceKind sourceKind);
}

/// <summary>
/// An immutable snapshot of wallpaper source providers keyed by their declared source kind.
/// Provider ordering is preserved for deterministic discovery and presentation.
/// </summary>
public sealed class WallpaperSourceProviderRegistry : IWallpaperSourceProviderRegistry
{
    private readonly ReadOnlyDictionary<MediaSourceKind, IWallpaperSourceProvider> _providersByKind;

    public WallpaperSourceProviderRegistry(IEnumerable<IWallpaperSourceProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var providerSnapshot = providers.ToArray();
        var providersByKind = new Dictionary<MediaSourceKind, IWallpaperSourceProvider>();
        var sourceKinds = new MediaSourceKind[providerSnapshot.Length];
        for (var index = 0; index < providerSnapshot.Length; index++)
        {
            var provider = providerSnapshot[index];
            if (provider is null)
            {
                throw new ArgumentException(
                    "The provider collection cannot contain null entries.",
                    nameof(providers));
            }

            var sourceKind = provider.SourceKind;
            if (!Enum.IsDefined(sourceKind))
            {
                throw new ArgumentException(
                    "A wallpaper source provider declared an unsupported source kind.",
                    nameof(providers));
            }

            if (!providersByKind.TryAdd(sourceKind, provider))
            {
                throw new ArgumentException(
                    $"A wallpaper source provider is already registered for {sourceKind}.",
                    nameof(providers));
            }

            sourceKinds[index] = sourceKind;
        }

        Providers = Array.AsReadOnly(providerSnapshot);
        SourceKinds = Array.AsReadOnly(sourceKinds);
        _providersByKind = new ReadOnlyDictionary<MediaSourceKind, IWallpaperSourceProvider>(
            providersByKind);
    }

    public IReadOnlyList<IWallpaperSourceProvider> Providers { get; }

    public IReadOnlyList<MediaSourceKind> SourceKinds { get; }

    public bool TryGet(
        MediaSourceKind sourceKind,
        [NotNullWhen(true)] out IWallpaperSourceProvider? provider) =>
        _providersByKind.TryGetValue(sourceKind, out provider);

    public IWallpaperSourceProvider GetRequired(MediaSourceKind sourceKind) =>
        TryGet(sourceKind, out var provider)
            ? provider
            : throw new MediaSourceNotSupportedException(sourceKind);
}
