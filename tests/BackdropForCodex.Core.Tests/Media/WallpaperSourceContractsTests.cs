using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperSourceContractsTests
{
    [Theory]
    [InlineData(
        MediaSourceKind.LocalFile,
        WallpaperContentKind.Image,
        WallpaperDeliveryKind.DirectMedia,
        WallpaperDeliveryCapabilities.None)]
    [InlineData(
        MediaSourceKind.LocalFile,
        WallpaperContentKind.Video,
        WallpaperDeliveryKind.DirectMedia,
        WallpaperDeliveryCapabilities.None)]
    [InlineData(
        MediaSourceKind.WallpaperEngineLocalProject,
        WallpaperContentKind.Scene,
        WallpaperDeliveryKind.WallpaperEngineWindow,
        WallpaperDeliveryCapabilities.DynamicFrames)]
    [InlineData(
        MediaSourceKind.WallpaperEngineWorkshopProject,
        WallpaperContentKind.Web,
        WallpaperDeliveryKind.WallpaperEngineWindow,
        WallpaperDeliveryCapabilities.DynamicFrames)]
    [InlineData(
        MediaSourceKind.WallpaperEngineWorkshopProject,
        WallpaperContentKind.Application,
        WallpaperDeliveryKind.Unsupported,
        WallpaperDeliveryCapabilities.None)]
    [InlineData(
        MediaSourceKind.WallpaperEngineWorkshopProject,
        WallpaperContentKind.Unknown,
        WallpaperDeliveryKind.Unsupported,
        WallpaperDeliveryCapabilities.None)]
    public void DescriptorAcceptsSupportedDeliveryContracts(
        MediaSourceKind sourceKind,
        WallpaperContentKind contentKind,
        WallpaperDeliveryKind deliveryKind,
        WallpaperDeliveryCapabilities requiredCapabilities)
    {
        var identifier = CreateIdentifier(sourceKind);

        var descriptor = new WallpaperSourceDescriptor(
            sourceKind,
            identifier,
            "Test wallpaper",
            contentKind,
            deliveryKind,
            requiredCapabilities);

        Assert.Equal(sourceKind, descriptor.SourceKind);
        Assert.Equal(
            CreateReference(sourceKind, identifier).Snapshot().SourceIdentifier,
            descriptor.SourceIdentifier);
        Assert.Equal("Test wallpaper", descriptor.DisplayName);
        Assert.Equal(contentKind, descriptor.ContentKind);
        Assert.Equal(deliveryKind, descriptor.DeliveryKind);
        Assert.Equal(requiredCapabilities, descriptor.RequiredCapabilities);
    }

    [Fact]
    public void DescriptorSanitizesUntrustedDisplayNamesAtTheProviderBoundary()
    {
        var descriptor = CreateDescriptor(
            displayName: "  壁纸\r\n\u0001\u202e 🌌安全\u0085\u2066  ");

        Assert.Equal("壁纸 🌌安全", descriptor.DisplayName);
    }

    [Fact]
    public void DescriptorPreservesUnicodeJoinersUsedByLegitimateDisplayNames()
    {
        var descriptor = CreateDescriptor(displayName: "开发者 👩\u200d💻");

        Assert.Equal("开发者 👩\u200d💻", descriptor.DisplayName);
    }

    [Fact]
    public void MediaReferenceSnapshotRejectsANameThatSanitizesToEmpty()
    {
        var reference = CreateReference(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            "123456") with
        {
            LastKnownDisplayName = "\u0001\u202e\u2066",
        };

        Assert.Throws<MediaReferenceValidationException>(reference.Snapshot);
    }

    [Fact]
    public void DescriptorRejectsInvalidEnumsAndCapabilityBits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateDescriptor(sourceKind: (MediaSourceKind)999));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateDescriptor(contentKind: (WallpaperContentKind)999));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateDescriptor(deliveryKind: (WallpaperDeliveryKind)999));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateDescriptor(
                requiredCapabilities: (WallpaperDeliveryCapabilities)(1 << 12)));
    }

    [Fact]
    public void DescriptorRejectsMissingOrOversizedText()
    {
        Assert.Throws<ArgumentNullException>(
            () => CreateDescriptor(sourceIdentifier: null!));
        Assert.Throws<ArgumentException>(
            () => CreateDescriptor(sourceIdentifier: "   "));
        Assert.Throws<ArgumentException>(
            () => CreateDescriptor(
                sourceIdentifier: "C:\\" +
                    new string('a', MediaReference.MaximumSourceIdentifierLength)));
        Assert.Throws<ArgumentNullException>(
            () => CreateDescriptor(displayName: null!));
        Assert.Throws<ArgumentException>(
            () => CreateDescriptor(displayName: "\t"));
        Assert.Throws<ArgumentException>(
            () => CreateDescriptor(
                displayName: new string(
                    'a',
                    WallpaperSourceDescriptor.MaximumDisplayNameLength + 1)));
    }

    [Fact]
    public void DescriptorRejectsIdentifiersThatCannotProduceCanonicalSnapshots()
    {
        Assert.Throws<MediaReferenceValidationException>(
            () => CreateDescriptor(sourceIdentifier: @"relative\wallpaper.png"));
        Assert.Throws<MediaReferenceValidationException>(
            () => CreateDescriptor(
                MediaSourceKind.WallpaperEngineLocalProject,
                @"relative\project.json",
                WallpaperContentKind.Scene,
                WallpaperDeliveryKind.WallpaperEngineWindow,
                WallpaperDeliveryCapabilities.DynamicFrames));
        Assert.Throws<MediaReferenceValidationException>(
            () => CreateDescriptor(
                MediaSourceKind.WallpaperEngineWorkshopProject,
                "0",
                WallpaperContentKind.Unknown,
                WallpaperDeliveryKind.Unsupported,
                WallpaperDeliveryCapabilities.None));
    }

    [Theory]
    [MemberData(nameof(InvalidDeliveryContracts))]
    public void DescriptorRejectsImpossibleDeliveryContracts(
        WallpaperContentKind contentKind,
        WallpaperDeliveryKind deliveryKind,
        WallpaperDeliveryCapabilities requiredCapabilities)
    {
        Assert.Throws<WallpaperSourceCapabilityException>(
            () => CreateDescriptor(
                contentKind: contentKind,
                deliveryKind: deliveryKind,
                requiredCapabilities: requiredCapabilities));
    }

    [Fact]
    public void DescriptorEnforcesTheCompleteContentToDeliveryMatrix()
    {
        foreach (var contentKind in Enum.GetValues<WallpaperContentKind>())
        {
            var expectedDeliveryKind = contentKind switch
            {
                WallpaperContentKind.Image or WallpaperContentKind.Video =>
                    WallpaperDeliveryKind.DirectMedia,
                WallpaperContentKind.Scene or WallpaperContentKind.Web =>
                    WallpaperDeliveryKind.WallpaperEngineWindow,
                WallpaperContentKind.Unknown or WallpaperContentKind.Application =>
                    WallpaperDeliveryKind.Unsupported,
                _ => throw new ArgumentOutOfRangeException(nameof(contentKind)),
            };

            foreach (var deliveryKind in Enum.GetValues<WallpaperDeliveryKind>())
            {
                var capabilities = deliveryKind == WallpaperDeliveryKind.WallpaperEngineWindow
                    ? WallpaperDeliveryCapabilities.DynamicFrames
                    : WallpaperDeliveryCapabilities.None;
                if (deliveryKind == expectedDeliveryKind)
                {
                    _ = CreateDescriptor(
                        contentKind: contentKind,
                        deliveryKind: deliveryKind,
                        requiredCapabilities: capabilities);
                }
                else
                {
                    Assert.Throws<WallpaperSourceCapabilityException>(
                        () => CreateDescriptor(
                            contentKind: contentKind,
                            deliveryKind: deliveryKind,
                            requiredCapabilities: capabilities));
                }
            }
        }
    }

    [Theory]
    [InlineData(WallpaperContentKind.Image, WallpaperDeliveryKind.DirectMedia)]
    [InlineData(WallpaperContentKind.Video, WallpaperDeliveryKind.DirectMedia)]
    [InlineData(WallpaperContentKind.Scene, WallpaperDeliveryKind.WallpaperEngineWindow)]
    [InlineData(WallpaperContentKind.Web, WallpaperDeliveryKind.WallpaperEngineWindow)]
    [InlineData(WallpaperContentKind.Application, WallpaperDeliveryKind.Unsupported)]
    [InlineData(WallpaperContentKind.Unknown, WallpaperDeliveryKind.Unsupported)]
    public void ResolutionAcceptsOnlyTheDeliveryKindsRequiredMetadata(
        WallpaperContentKind contentKind,
        WallpaperDeliveryKind deliveryKind)
    {
        var sourceKind = deliveryKind == WallpaperDeliveryKind.DirectMedia
            ? MediaSourceKind.LocalFile
            : MediaSourceKind.WallpaperEngineWorkshopProject;
        var descriptor = CreateDescriptor(
            sourceKind,
            CreateIdentifier(sourceKind),
            contentKind: contentKind,
            deliveryKind: deliveryKind,
            requiredCapabilities: deliveryKind == WallpaperDeliveryKind.WallpaperEngineWindow
                ? WallpaperDeliveryCapabilities.DynamicFrames
                : WallpaperDeliveryCapabilities.None);
        var reference = CreateReference(sourceKind, descriptor.SourceIdentifier);
        var metadata = deliveryKind == WallpaperDeliveryKind.DirectMedia
            ? CreateMetadata(contentKind)
            : null;

        var resolution = new WallpaperSourceResolution(reference, descriptor, metadata);

        Assert.Equal(reference.MediaId, resolution.CanonicalReference.MediaId);
        Assert.Equal(descriptor.ContentKind, resolution.CanonicalReference.LastKnownContentKind);
        Assert.Equal(descriptor.DisplayName, resolution.CanonicalReference.LastKnownDisplayName);
        Assert.Equal(
            contentKind switch
            {
                WallpaperContentKind.Image => MediaKind.Image,
                WallpaperContentKind.Video => MediaKind.Video,
                _ => MediaKind.None,
            },
            resolution.CanonicalReference.LastKnownKind);
        Assert.Same(descriptor, resolution.Descriptor);
        Assert.Same(metadata, resolution.DirectMediaMetadata);
    }

    [Fact]
    public void ResolutionComparesCanonicalIdentifiersRatherThanProviderSpelling()
    {
        var descriptor = CreateDescriptor(
            sourceIdentifier: @"C:\Wallpapers\set\..\wallpaper.png");
        var differentlySpelledReference = CreateReference(
            MediaSourceKind.LocalFile,
            @"c:\WALLPAPERS\wallpaper.png");
        var workshopDescriptor = CreateDescriptor(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            "000123456",
            WallpaperContentKind.Unknown,
            WallpaperDeliveryKind.Unsupported,
            WallpaperDeliveryCapabilities.None);
        var workshopReference = CreateReference(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            "123456");

        var localResolution = new WallpaperSourceResolution(
            differentlySpelledReference,
            descriptor,
            CreateMetadata(WallpaperContentKind.Image));
        var workshopResolution = new WallpaperSourceResolution(
            workshopReference,
            workshopDescriptor,
            directMediaMetadata: null);

        Assert.Equal(
            Path.GetFullPath(@"C:\Wallpapers\wallpaper.png"),
            localResolution.CanonicalReference.SourceIdentifier,
            ignoreCase: true);
        Assert.Equal("123456", workshopResolution.CanonicalReference.SourceIdentifier);
    }

    [Fact]
    public void ResolutionRejectsSourceKindAndIdentifierSubstitution()
    {
        var descriptor = CreateDescriptor();
        var differentSourceKind = CreateReference(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            "123456");
        var differentIdentifier = CreateReference(
            MediaSourceKind.LocalFile,
            @"C:\Wallpapers\different.png");
        var metadata = CreateMetadata(WallpaperContentKind.Image);

        Assert.Throws<ArgumentException>(
            () => new WallpaperSourceResolution(
                differentSourceKind,
                descriptor,
                metadata));
        Assert.Throws<ArgumentException>(
            () => new WallpaperSourceResolution(
                differentIdentifier,
                descriptor,
                metadata));
    }

    [Fact]
    public void ResolutionRejectsMissingOrUnexpectedMetadata()
    {
        var directDescriptor = CreateDescriptor();
        var windowDescriptor = CreateDescriptor(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            "123456",
            WallpaperContentKind.Scene,
            WallpaperDeliveryKind.WallpaperEngineWindow,
            WallpaperDeliveryCapabilities.DynamicFrames);
        var unsupportedDescriptor = CreateDescriptor(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            "123456",
            WallpaperContentKind.Application,
            WallpaperDeliveryKind.Unsupported,
            WallpaperDeliveryCapabilities.None);
        var directReference = CreateReference(
            directDescriptor.SourceKind,
            directDescriptor.SourceIdentifier);
        var projectReference = CreateReference(
            windowDescriptor.SourceKind,
            windowDescriptor.SourceIdentifier);
        var metadata = CreateMetadata(WallpaperContentKind.Image);

        Assert.Throws<WallpaperSourceCapabilityException>(
            () => new WallpaperSourceResolution(
                directReference,
                directDescriptor,
                directMediaMetadata: null));
        Assert.Throws<WallpaperSourceCapabilityException>(
            () => new WallpaperSourceResolution(
                projectReference,
                windowDescriptor,
                metadata));
        Assert.Throws<WallpaperSourceCapabilityException>(
            () => new WallpaperSourceResolution(
                projectReference,
                unsupportedDescriptor,
                metadata));
    }

    [Theory]
    [MemberData(nameof(DisguisedDirectMediaMetadata))]
    public void ResolutionRejectsDisguisedDirectMediaMetadata(
        WallpaperContentKind contentKind,
        MediaFileMetadata metadata)
    {
        var descriptor = CreateDescriptor(contentKind: contentKind);
        var reference = CreateReference(
            descriptor.SourceKind,
            descriptor.SourceIdentifier);

        Assert.Throws<WallpaperSourceCapabilityException>(
            () => new WallpaperSourceResolution(reference, descriptor, metadata));
    }

    [Fact]
    public void ResolutionRejectsNullContracts()
    {
        var descriptor = CreateDescriptor();
        var reference = CreateReference(
            descriptor.SourceKind,
            descriptor.SourceIdentifier);

        Assert.Throws<ArgumentNullException>(
            () => new WallpaperSourceResolution(
                null!,
                descriptor,
                CreateMetadata(WallpaperContentKind.Image)));
        Assert.Throws<ArgumentNullException>(
            () => new WallpaperSourceResolution(
                reference,
                null!,
                CreateMetadata(WallpaperContentKind.Image)));
    }

    public static TheoryData<
        WallpaperContentKind,
        WallpaperDeliveryKind,
        WallpaperDeliveryCapabilities> InvalidDeliveryContracts => new()
    {
        {
            WallpaperContentKind.Unknown,
            WallpaperDeliveryKind.DirectMedia,
            WallpaperDeliveryCapabilities.None
        },
        {
            WallpaperContentKind.Application,
            WallpaperDeliveryKind.WallpaperEngineWindow,
            WallpaperDeliveryCapabilities.DynamicFrames
        },
        {
            WallpaperContentKind.Scene,
            WallpaperDeliveryKind.DirectMedia,
            WallpaperDeliveryCapabilities.None
        },
        {
            WallpaperContentKind.Web,
            WallpaperDeliveryKind.DirectMedia,
            WallpaperDeliveryCapabilities.None
        },
        {
            WallpaperContentKind.Image,
            WallpaperDeliveryKind.WallpaperEngineWindow,
            WallpaperDeliveryCapabilities.DynamicFrames
        },
        {
            WallpaperContentKind.Video,
            WallpaperDeliveryKind.WallpaperEngineWindow,
            WallpaperDeliveryCapabilities.DynamicFrames
        },
        {
            WallpaperContentKind.Scene,
            WallpaperDeliveryKind.WallpaperEngineWindow,
            WallpaperDeliveryCapabilities.None
        },
        {
            WallpaperContentKind.Web,
            WallpaperDeliveryKind.WallpaperEngineWindow,
            WallpaperDeliveryCapabilities.DynamicFrames |
                WallpaperDeliveryCapabilities.Audio
        },
        {
            WallpaperContentKind.Unknown,
            WallpaperDeliveryKind.Unsupported,
            WallpaperDeliveryCapabilities.DynamicFrames
        },
        {
            WallpaperContentKind.Image,
            WallpaperDeliveryKind.Unsupported,
            WallpaperDeliveryCapabilities.None
        },
        {
            WallpaperContentKind.Video,
            WallpaperDeliveryKind.Unsupported,
            WallpaperDeliveryCapabilities.None
        },
        {
            WallpaperContentKind.Scene,
            WallpaperDeliveryKind.Unsupported,
            WallpaperDeliveryCapabilities.None
        },
        {
            WallpaperContentKind.Web,
            WallpaperDeliveryKind.Unsupported,
            WallpaperDeliveryCapabilities.None
        },
        {
            WallpaperContentKind.Image,
            WallpaperDeliveryKind.DirectMedia,
            WallpaperDeliveryCapabilities.DynamicFrames
        },
        {
            WallpaperContentKind.Video,
            WallpaperDeliveryKind.DirectMedia,
            WallpaperDeliveryCapabilities.Audio
        },
    };

    public static TheoryData<WallpaperContentKind, MediaFileMetadata>
        DisguisedDirectMediaMetadata => new()
        {
            {
                WallpaperContentKind.Image,
                MediaFileInspector.CreateMetadata(MediaFormat.Mp4, contentLength: 1)
            },
            {
                WallpaperContentKind.Video,
                MediaFileInspector.CreateMetadata(MediaFormat.Png, contentLength: 1)
            },
            {
                WallpaperContentKind.Image,
                new MediaFileMetadata(
                    MediaFormat.Png,
                    MediaKind.Video,
                    "image/png",
                    ContentLength: 1)
            },
            {
                WallpaperContentKind.Image,
                new MediaFileMetadata(
                    MediaFormat.Png,
                    MediaKind.Image,
                    "video/mp4",
                    ContentLength: 1)
            },
            {
                WallpaperContentKind.Image,
                new MediaFileMetadata(
                    (MediaFormat)999,
                    MediaKind.Image,
                    "image/png",
                    ContentLength: 1)
            },
        };

    private static WallpaperSourceDescriptor CreateDescriptor(
        MediaSourceKind sourceKind = MediaSourceKind.LocalFile,
        string sourceIdentifier = @"C:\Wallpapers\wallpaper.png",
        WallpaperContentKind contentKind = WallpaperContentKind.Image,
        WallpaperDeliveryKind deliveryKind = WallpaperDeliveryKind.DirectMedia,
        WallpaperDeliveryCapabilities requiredCapabilities = WallpaperDeliveryCapabilities.None,
        string displayName = "Test wallpaper") =>
        new(
            sourceKind,
            sourceIdentifier,
            displayName,
            contentKind,
            deliveryKind,
            requiredCapabilities);

    private static MediaReference CreateReference(
        MediaSourceKind sourceKind,
        string sourceIdentifier) => new()
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = sourceKind,
            SourceIdentifier = sourceIdentifier,
            LastKnownKind = MediaKind.None,
        };

    private static string CreateIdentifier(MediaSourceKind sourceKind) => sourceKind switch
    {
        MediaSourceKind.LocalFile => @"C:\Wallpapers\set\..\wallpaper.png",
        MediaSourceKind.WallpaperEngineLocalProject =>
            @"C:\Wallpapers\projects\..\project.json",
        MediaSourceKind.WallpaperEngineWorkshopProject => "000123456",
        _ => throw new ArgumentOutOfRangeException(nameof(sourceKind)),
    };

    private static MediaFileMetadata CreateMetadata(WallpaperContentKind contentKind) =>
        contentKind switch
        {
            WallpaperContentKind.Image =>
                MediaFileInspector.CreateMetadata(MediaFormat.Png, contentLength: 1),
            WallpaperContentKind.Video =>
                MediaFileInspector.CreateMetadata(MediaFormat.Mp4, contentLength: 1),
            _ => throw new ArgumentOutOfRangeException(nameof(contentKind)),
        };
}
