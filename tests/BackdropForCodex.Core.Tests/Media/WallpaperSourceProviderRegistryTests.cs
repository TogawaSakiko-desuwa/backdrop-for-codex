using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperSourceProviderRegistryTests
{
    [Fact]
    public void ConstructorCreatesStableDefensiveReadOnlySnapshots()
    {
        var localProvider = new StubProvider(MediaSourceKind.LocalFile);
        var workshopProvider = new StubProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject);
        var source = new List<IWallpaperSourceProvider>
        {
            localProvider,
            workshopProvider,
        };

        var registry = new WallpaperSourceProviderRegistry(source);
        source.Clear();

        Assert.Same(registry.Providers, registry.Providers);
        Assert.Same(registry.SourceKinds, registry.SourceKinds);
        Assert.Equal([localProvider, workshopProvider], registry.Providers);
        Assert.Equal(
            [
                MediaSourceKind.LocalFile,
                MediaSourceKind.WallpaperEngineWorkshopProject,
            ],
            registry.SourceKinds);

        var providers = Assert.IsAssignableFrom<IList<IWallpaperSourceProvider>>(
            registry.Providers);
        var sourceKinds = Assert.IsAssignableFrom<IList<MediaSourceKind>>(
            registry.SourceKinds);
        Assert.True(providers.IsReadOnly);
        Assert.True(sourceKinds.IsReadOnly);
        Assert.Throws<NotSupportedException>(
            () => providers.Add(new StubProvider(
                MediaSourceKind.WallpaperEngineLocalProject)));
        Assert.Throws<NotSupportedException>(
            () => sourceKinds.Add(MediaSourceKind.WallpaperEngineLocalProject));
    }

    [Fact]
    public void ConstructorRejectsNullCollectionsAndEntries()
    {
        Assert.Throws<ArgumentNullException>(
            () => new WallpaperSourceProviderRegistry(null!));
        Assert.Throws<ArgumentException>(
            () => new WallpaperSourceProviderRegistry(
                new IWallpaperSourceProvider[] { null! }));
    }

    [Fact]
    public void ConstructorRejectsUndefinedAndDuplicateSourceKinds()
    {
        Assert.Throws<ArgumentException>(
            () => new WallpaperSourceProviderRegistry(
                [new StubProvider((MediaSourceKind)999)]));
        Assert.Throws<ArgumentException>(
            () => new WallpaperSourceProviderRegistry(
                [
                    new StubProvider(MediaSourceKind.LocalFile),
                    new StubProvider(MediaSourceKind.LocalFile),
                ]));
    }

    [Fact]
    public void TryGetReturnsOnlyRegisteredProviderSnapshots()
    {
        var provider = new StubProvider(MediaSourceKind.LocalFile);
        var registry = new WallpaperSourceProviderRegistry([provider]);

        var found = registry.TryGet(MediaSourceKind.LocalFile, out var registered);
        var missing = registry.TryGet(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            out var absent);
        var undefined = registry.TryGet((MediaSourceKind)999, out var invalid);

        Assert.True(found);
        Assert.Same(provider, registered);
        Assert.False(missing);
        Assert.Null(absent);
        Assert.False(undefined);
        Assert.Null(invalid);
    }

    [Fact]
    public void GetRequiredReturnsRegisteredProviderAndThrowsTypedMissingError()
    {
        var provider = new StubProvider(MediaSourceKind.LocalFile);
        var registry = new WallpaperSourceProviderRegistry([provider]);

        Assert.Same(provider, registry.GetRequired(MediaSourceKind.LocalFile));

        var missing = Assert.Throws<MediaSourceNotSupportedException>(
            () => registry.GetRequired(
                MediaSourceKind.WallpaperEngineWorkshopProject));
        var undefined = Assert.Throws<MediaSourceNotSupportedException>(
            () => registry.GetRequired((MediaSourceKind)999));
        Assert.Equal(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            missing.SourceKind);
        Assert.Equal((MediaSourceKind)999, undefined.SourceKind);
    }

    private sealed class StubProvider(MediaSourceKind sourceKind) : IWallpaperSourceProvider
    {
        public MediaSourceKind SourceKind { get; } = sourceKind;

        public ValueTask<IMediaLease> AcquireLeaseAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
