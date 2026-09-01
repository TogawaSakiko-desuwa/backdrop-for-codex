using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BackdropForCodex.App.Converters;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

[Collection("Wpf")]
public sealed class MediaThumbnailConverterTests
{
    [Fact]
    public async Task RepeatedWatcherConstructionFailureAdvancesTheMonitoringBackoff()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"BackdropForCodex-missing-thumbnail-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(directory, "source.png");
        var retrySignal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidationCount = 0;

        void OnInvalidated(object? sender, MediaThumbnailSourceInvalidatedEventArgs eventArgs)
        {
            _ = sender;
            if (string.Equals(eventArgs.Path, sourcePath, StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref invalidationCount) >= 2)
            {
                retrySignal.TrySetResult();
            }
        }

        MediaThumbnailInvalidationHub.SourceInvalidated += OnInvalidated;
        try
        {
            _ = MediaThumbnailInvalidationHub.TrackValidatedSource(sourcePath, sourcePath);
            await retrySignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var failedRetryGeneration =
                MediaThumbnailInvalidationHub.TrackValidatedSource(sourcePath, sourcePath);
            var throttledGeneration =
                MediaThumbnailInvalidationHub.TrackValidatedSource(sourcePath, sourcePath);

            Assert.Equal(failedRetryGeneration, throttledGeneration);
        }
        finally
        {
            MediaThumbnailInvalidationHub.SourceInvalidated -= OnInvalidated;
        }
    }

    [Fact]
    public void ConvertCacheHitDoesNotProbeTheFileSystem()
    {
        StaTest.Run(
            () =>
            {
                var preview = new RecordingPreviewService();
                var converter = new MediaThumbnailConverter(preview);
                var directory = Path.Combine(
                    Path.GetTempPath(),
                    $"BackdropForCodex-thumbnail-{Guid.NewGuid():N}");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "source.png");
                try
                {
                    File.WriteAllBytes(path, [0x01]);
                    var first = converter.Convert(
                        path,
                        typeof(ImageSource),
                        parameter: null!,
                        CultureInfo.InvariantCulture);

                    object? second;
                    using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        second = converter.Convert(
                            path,
                            typeof(ImageSource),
                            parameter: null!,
                            CultureInfo.InvariantCulture);
                    }

                    Assert.Same(first, second);
                    Assert.Equal(1, preview.AcquireCount);
                }
                finally
                {
                    File.Delete(path);
                    Directory.Delete(directory);
                }
            });
    }

    [Fact]
    public void InvalidationDuringDecodeLeavesTheNewBitmapStaleForTheNextConversion()
    {
        StaTest.Run(
            () =>
            {
                var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
                var invalidated = false;
                var preview = new RecordingPreviewService(
                    onLoadBitmap: () =>
                    {
                        if (!invalidated)
                        {
                            invalidated = true;
                            MediaThumbnailInvalidationHub.InvalidateSource(path);
                        }
                    });
                var converter = new MediaThumbnailConverter(preview);

                var first = converter.Convert(
                    path,
                    typeof(ImageSource),
                    parameter: null!,
                    CultureInfo.InvariantCulture);
                var second = converter.Convert(
                    path,
                    typeof(ImageSource),
                    parameter: null!,
                    CultureInfo.InvariantCulture);

                Assert.NotSame(first, second);
                Assert.Equal(2, preview.AcquireCount);
            });
    }

    [Fact]
    public void ConvertReturnsCachedImageWhenTheSourceIsTemporarilyLocked()
    {
        StaTest.Run(
            () =>
            {
                var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
                try
                {
                    File.WriteAllBytes(path, [0x01]);
                    var preview = new RecordingPreviewService(openSourceOnAcquire: true);
                    var converter = new MediaThumbnailConverter(preview);
                    var cached = Assert.IsAssignableFrom<ImageSource>(
                        converter.Convert(
                            path,
                            typeof(ImageSource),
                            parameter: null!,
                            CultureInfo.InvariantCulture));

                    object? result;
                    using (var exclusive = new FileStream(
                               path,
                               FileMode.Open,
                               FileAccess.ReadWrite,
                               FileShare.None))
                    {
                        MediaThumbnailInvalidationHub.InvalidateSource(path);
                        result = converter.Convert(
                            path,
                            typeof(ImageSource),
                            parameter: null!,
                            CultureInfo.InvariantCulture);
                        var throttled = converter.Convert(
                            path,
                            typeof(ImageSource),
                            parameter: null!,
                            CultureInfo.InvariantCulture);

                        Assert.Same(cached, result);
                        Assert.Same(cached, throttled);
                        Assert.Equal(2, preview.AcquireAttemptCount);
                        Assert.Equal(1, preview.AcquireCount);
                    }

                    ImageSource? refreshed = null;
                    Assert.True(
                        SpinWait.SpinUntil(
                            () =>
                            {
                                refreshed = converter.Convert(
                                    path,
                                    typeof(ImageSource),
                                    parameter: null!,
                                    CultureInfo.InvariantCulture) as ImageSource;
                                return refreshed is not null &&
                                    !ReferenceEquals(cached, refreshed);
                            },
                            TimeSpan.FromSeconds(5)));
                    Assert.True(preview.AcquireAttemptCount >= 3);
                }
                finally
                {
                    File.Delete(path);
                }
            });
    }

    [Fact]
    public void ConvertReloadsAnImageWhenTheFileAtTheSamePathChanges()
    {
        StaTest.Run(
            () =>
            {
                var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
                try
                {
                    var firstWrite = DateTime.UtcNow.AddMinutes(-2);
                    File.WriteAllBytes(path, [0x01]);
                    File.SetLastWriteTimeUtc(path, firstWrite);
                    var preview = new RecordingPreviewService();
                    var converter = new MediaThumbnailConverter(preview);

                    var first = Assert.IsAssignableFrom<ImageSource>(
                        converter.Convert(
                            path,
                            typeof(ImageSource),
                            parameter: null!,
                            CultureInfo.InvariantCulture));

                    File.WriteAllBytes(path, [0x01, 0x02]);
                    File.SetLastWriteTimeUtc(path, firstWrite.AddMinutes(1));
                    MediaThumbnailInvalidationHub.InvalidateSource(path);
                    var second = Assert.IsAssignableFrom<ImageSource>(
                        converter.Convert(
                            path,
                            typeof(ImageSource),
                            parameter: null!,
                            CultureInfo.InvariantCulture));

                    Assert.True(preview.AcquireCount >= 2);
                    Assert.NotSame(first, second);
                }
                finally
                {
                    File.Delete(path);
                }
            });
    }

    [Fact]
    public void ConvertReloadsWhenContentChangesButLengthAndLastWriteTimeArePreserved()
    {
        StaTest.Run(
            () =>
            {
                var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
                try
                {
                    var preservedWriteTime = DateTime.UtcNow.AddMinutes(-2);
                    File.WriteAllBytes(path, [0x01, 0x02]);
                    File.SetLastWriteTimeUtc(path, preservedWriteTime);
                    var preview = new RecordingPreviewService();
                    var converter = new MediaThumbnailConverter(preview);

                    var first = Assert.IsAssignableFrom<ImageSource>(
                        converter.Convert(
                            path,
                            typeof(ImageSource),
                            parameter: null!,
                            CultureInfo.InvariantCulture));

                    File.WriteAllBytes(path, [0x03, 0x04]);
                    File.SetLastWriteTimeUtc(path, preservedWriteTime);
                    MediaThumbnailInvalidationHub.InvalidateSource(path);
                    var second = Assert.IsAssignableFrom<ImageSource>(
                        converter.Convert(
                            path,
                            typeof(ImageSource),
                            parameter: null!,
                            CultureInfo.InvariantCulture));

                    Assert.True(preview.AcquireCount >= 2);
                    Assert.NotSame(first, second);
                }
                finally
                {
                    File.Delete(path);
                }
            });
    }

    private sealed class RecordingPreviewService(
        bool openSourceOnAcquire = false,
        Action? onLoadBitmap = null)
        : ISafeMediaPreviewService
    {
        public int AcquireCount { get; private set; }

        public int AcquireAttemptCount { get; private set; }

        public ISafeMediaPreviewLease Acquire(MediaReference reference) =>
            Acquire(reference.SourceIdentifier);

        public ISafeMediaPreviewLease Acquire(string mediaPath)
        {
            AcquireAttemptCount++;
            if (openSourceOnAcquire)
            {
                using var handle = File.Open(
                    mediaPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }

            AcquireCount++;
            return new PreviewLease(AcquireCount, onLoadBitmap);
        }

        public bool IsAvailable(MediaReference reference) => true;

        public bool IsAvailable(string mediaPath) => true;
    }

    private sealed class PreviewLease(int generation, Action? onLoadBitmap)
        : ISafeMediaPreviewLease
    {
        public MediaFileMetadata Metadata { get; } =
            new(MediaFormat.Png, MediaKind.Image, "image/png", generation, 1, 1);

        public BitmapSource LoadBitmap(int decodePixelWidth)
        {
            _ = decodePixelWidth;
            onLoadBitmap?.Invoke();
            var bitmap = BitmapSource.Create(
                1,
                1,
                96,
                96,
                PixelFormats.Bgra32,
                palette: null,
                new byte[] { unchecked((byte)generation), 0, 0, 0xFF },
                stride: 4);
            bitmap.Freeze();
            return bitmap;
        }

        public Uri CreateVideoSource() => throw new NotSupportedException();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
