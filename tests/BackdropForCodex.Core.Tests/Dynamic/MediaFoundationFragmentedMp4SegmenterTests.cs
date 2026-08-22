using System.Buffers.Binary;
using System.Text;
using BackdropForCodex.Core.Dynamic;
using Xunit;

namespace BackdropForCodex.Core.Tests.Dynamic;

public sealed class MediaFoundationFragmentedMp4SegmenterTests
{
    [Fact]
    public void SplitMakesEachMediaFragmentIndependentAndMarksAnIdrCleanPoint()
    {
        var segmenter = new MediaFoundationFragmentedMp4Segmenter(generation: 41);

        var segments = segmenter.Split(CreateFinalizedFile(
            sampleDuration: 1_000,
            sampleFlags: 0,
            nalType: 5));

        var initialization = Assert.Single(
            segments,
            segment => segment.Kind == EncodedWallpaperSegmentKind.Initialization);
        var media = Assert.Single(
            segments,
            segment => segment.Kind == EncodedWallpaperSegmentKind.Media);
        Assert.Equal(0, initialization.Sequence);
        Assert.Equal(1, media.Sequence);
        Assert.True(media.IsKeyFrame);

        var mediaBytes = media.Payload.Span;
        var moofSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(mediaBytes));
        Assert.Equal("moof", Encoding.ASCII.GetString(mediaBytes.Slice(4, 4)));
        Assert.Equal("mdat", Encoding.ASCII.GetString(mediaBytes.Slice(moofSize + 4, 4)));

        var tfhd = FindBox(mediaBytes[..moofSize], "tfhd");
        var tfhdFlags = ReadFullBoxFlags(mediaBytes[tfhd.Offset..]);
        Assert.Equal(0x020000, tfhdFlags);
        Assert.Equal(16, tfhd.Size);

        var tfdt = FindBox(mediaBytes[..moofSize], "tfdt");
        Assert.Equal(1, mediaBytes[tfdt.Offset + 8]);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64BigEndian(
            mediaBytes.Slice(tfdt.Offset + 12, 8)));

        var trun = FindBox(mediaBytes[..moofSize], "trun");
        Assert.Equal(
            moofSize + 8,
            BinaryPrimitives.ReadInt32BigEndian(mediaBytes.Slice(trun.Offset + 16, 4)));
    }

    [Fact]
    public void SplitCarriesDecodeTimeAcrossFreshMediaFoundationFiles()
    {
        var segmenter = new MediaFoundationFragmentedMp4Segmenter(generation: 43);
        _ = segmenter.Split(CreateFinalizedFile(1_000, sampleFlags: 0, nalType: 5));
        Assert.True(segmenter.TryCommitPendingBatch(
            EncodedWallpaperWriteResult.Accepted));

        var second = segmenter.Split(CreateFinalizedFile(
            sampleDuration: 1_000,
            sampleFlags: 0x00010000,
            nalType: 1));

        var media = Assert.Single(second);
        Assert.Equal(2, media.Sequence);
        Assert.False(media.IsKeyFrame);
        var tfdt = FindBox(media.Payload.Span, "tfdt");
        Assert.Equal(1_000UL, BinaryPrimitives.ReadUInt64BigEndian(
            media.Payload.Span.Slice(tfdt.Offset + 12, 8)));
    }

    [Fact]
    public void SplitReturnsEveryFragmentFromOneFinalizedFileAsASinglePendingBatch()
    {
        var segmenter = new MediaFoundationFragmentedMp4Segmenter(generation: 42);
        var finalized = CreateFinalizedFileWithTwoFragments();

        var segments = segmenter.Split(finalized);

        Assert.Collection(
            segments,
            initialization =>
            {
                Assert.Equal(EncodedWallpaperSegmentKind.Initialization, initialization.Kind);
                Assert.Equal(0, initialization.Sequence);
            },
            firstMedia =>
            {
                Assert.Equal(EncodedWallpaperSegmentKind.Media, firstMedia.Kind);
                Assert.Equal(1, firstMedia.Sequence);
                Assert.True(firstMedia.IsKeyFrame);
                AssertMediaTimeline(firstMedia, fragmentSequence: 1, decodeTime: 0);
            },
            secondMedia =>
            {
                Assert.Equal(EncodedWallpaperSegmentKind.Media, secondMedia.Kind);
                Assert.Equal(2, secondMedia.Sequence);
                Assert.False(secondMedia.IsKeyFrame);
                AssertMediaTimeline(secondMedia, fragmentSequence: 2, decodeTime: 900);
            });

        Assert.Throws<InvalidOperationException>(() => segmenter.Split(finalized));
        Assert.True(segmenter.TryCommitPendingBatch(EncodedWallpaperWriteResult.Accepted));

        var nextMedia = Assert.Single(segmenter.Split(CreateFinalizedFile(
            sampleDuration: 500,
            sampleFlags: 0x00010000,
            nalType: 1)));
        Assert.Equal(3, nextMedia.Sequence);
        AssertMediaTimeline(nextMedia, fragmentSequence: 3, decodeTime: 2_000);
    }

    [Fact]
    public void DiscardedUnpublishedBatchDoesNotAdvanceSequenceOrDecodeTime()
    {
        var segmenter = new MediaFoundationFragmentedMp4Segmenter(generation: 44);
        var finalized = CreateFinalizedFile(1_000, sampleFlags: 0, nalType: 5);

        var abandoned = segmenter.Split(finalized);
        segmenter.DiscardPendingBatch();
        var retried = segmenter.Split(finalized);

        Assert.Equal(
            abandoned.Select(segment => segment.Sequence),
            retried.Select(segment => segment.Sequence));
        var retriedMedia = Assert.Single(
            retried,
            segment => segment.Kind == EncodedWallpaperSegmentKind.Media);
        var retriedTfdt = FindBox(retriedMedia.Payload.Span, "tfdt");
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64BigEndian(
            retriedMedia.Payload.Span.Slice(retriedTfdt.Offset + 12, 8)));

        Assert.True(segmenter.TryCommitPendingBatch(
            EncodedWallpaperWriteResult.RecoveredAfterQueueDrain));
        var next = segmenter.Split(CreateFinalizedFile(
            sampleDuration: 1_000,
            sampleFlags: 0x00010000,
            nalType: 1));
        var nextMedia = Assert.Single(next);
        Assert.Equal(2, nextMedia.Sequence);
        var nextTfdt = FindBox(nextMedia.Payload.Span, "tfdt");
        Assert.Equal(1_000UL, BinaryPrimitives.ReadUInt64BigEndian(
            nextMedia.Payload.Span.Slice(nextTfdt.Offset + 12, 8)));
    }

    [Theory]
    [InlineData(EncodedWallpaperWriteResult.DroppedUntilKeyFrame)]
    [InlineData(EncodedWallpaperWriteResult.Closed)]
    public void IncompletePublicationDoesNotAdvanceSequenceOrDecodeTime(
        EncodedWallpaperWriteResult result)
    {
        var segmenter = new MediaFoundationFragmentedMp4Segmenter(generation: 46);
        var finalized = CreateFinalizedFile(1_000, sampleFlags: 0, nalType: 5);
        var unpublished = segmenter.Split(finalized);

        Assert.False(segmenter.TryCommitPendingBatch(result));
        var retried = segmenter.Split(finalized);

        Assert.Equal(
            unpublished.Select(segment => segment.Sequence),
            retried.Select(segment => segment.Sequence));
        var retriedMedia = Assert.Single(
            retried,
            segment => segment.Kind == EncodedWallpaperSegmentKind.Media);
        var retriedTfdt = FindBox(retriedMedia.Payload.Span, "tfdt");
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64BigEndian(
            retriedMedia.Payload.Span.Slice(retriedTfdt.Offset + 12, 8)));
    }

    [Fact]
    public void SplitDoesNotTreatAnIdrInALaterSampleAsTheFragmentCleanPoint()
    {
        var segmenter = new MediaFoundationFragmentedMp4Segmenter(generation: 45);

        var segments = segmenter.Split(CreateFinalizedFileWithLaterIdr());

        var media = Assert.Single(
            segments,
            segment => segment.Kind == EncodedWallpaperSegmentKind.Media);
        Assert.False(media.IsKeyFrame);
    }

    [Fact]
    public void SplitRejectsMalformedOrNonFragmentedOutput()
    {
        var segmenter = new MediaFoundationFragmentedMp4Segmenter(generation: 47);

        Assert.Throws<DynamicWallpaperUnavailableException>(() =>
            segmenter.Split(Box("ftyp", [0, 0, 0, 0])));
    }

    private static byte[] CreateFinalizedFile(
        uint sampleDuration,
        uint sampleFlags,
        byte nalType)
    {
        var mfhd = FullBox("mfhd", flags: 0, UInt32(1));
        var tfhdPayload = Concat(
            UInt32(1),
            UInt64(1234),
            UInt32(0));
        var tfhd = FullBox("tfhd", flags: 1, tfhdPayload);
        var sample = Concat(
            UInt32(sampleDuration),
            UInt32(7),
            UInt32(sampleFlags),
            UInt32(0));
        var trun = FullBox(
            "trun",
            flags: 0x000F01,
            Concat(UInt32(1), UInt32(0), sample));
        var moof = Box("moof", Concat(mfhd, Box("traf", Concat(tfhd, trun))));
        var mdat = Box("mdat", [0, 0, 0, 3, nalType, 0, 0]);
        return Concat(
            Box("ftyp", [0, 0, 0, 0]),
            Box("moov", Box("avcC", [1, 0x64, 0, 0x28])),
            moof,
            mdat,
            Box("mfra", [0, 0, 0, 0]));
    }

    private static byte[] CreateFinalizedFileWithLaterIdr()
    {
        var mfhd = FullBox("mfhd", flags: 0, UInt32(1));
        var tfhd = FullBox(
            "tfhd",
            flags: 1,
            Concat(UInt32(1), UInt64(1234), UInt32(0)));
        var firstSample = Concat(
            UInt32(1_000),
            UInt32(7),
            UInt32(0),
            UInt32(0));
        var secondSample = Concat(
            UInt32(1_000),
            UInt32(7),
            UInt32(0x00010000),
            UInt32(0));
        var trun = FullBox(
            "trun",
            flags: 0x000F01,
            Concat(UInt32(2), UInt32(0), firstSample, secondSample));
        var moof = Box("moof", Concat(mfhd, Box("traf", Concat(tfhd, trun))));
        var mdat = Box(
            "mdat",
            [0, 0, 0, 3, 1, 0, 0, 0, 0, 0, 3, 5, 0, 0]);
        return Concat(
            Box("ftyp", [0, 0, 0, 0]),
            Box("moov", Box("avcC", [1, 0x64, 0, 0x28])),
            moof,
            mdat,
            Box("mfra", [0, 0, 0, 0]));
    }

    private static byte[] CreateFinalizedFileWithTwoFragments()
    {
        return Concat(
            Box("ftyp", [0, 0, 0, 0]),
            Box("moov", Box("avcC", [1, 0x64, 0, 0x28])),
            CreateFragment(
                sourceSequence: 71,
                sampleDuration: 900,
                sampleFlags: 0,
                nalType: 5),
            CreateFragment(
                sourceSequence: 72,
                sampleDuration: 1_100,
                sampleFlags: 0x00010000,
                nalType: 1),
            Box("mfra", [0, 0, 0, 0]));
    }

    private static byte[] CreateFragment(
        uint sourceSequence,
        uint sampleDuration,
        uint sampleFlags,
        byte nalType)
    {
        var mfhd = FullBox("mfhd", flags: 0, UInt32(sourceSequence));
        var tfhd = FullBox(
            "tfhd",
            flags: 1,
            Concat(UInt32(1), UInt64(1234), UInt32(0)));
        var sample = Concat(
            UInt32(sampleDuration),
            UInt32(7),
            UInt32(sampleFlags),
            UInt32(0));
        var trun = FullBox(
            "trun",
            flags: 0x000F01,
            Concat(UInt32(1), UInt32(0), sample));
        return Concat(
            Box("moof", Concat(mfhd, Box("traf", Concat(tfhd, trun)))),
            Box("mdat", [0, 0, 0, 3, nalType, 0, 0]));
    }

    private static void AssertMediaTimeline(
        EncodedWallpaperSegment media,
        uint fragmentSequence,
        ulong decodeTime)
    {
        var mfhd = FindBox(media.Payload.Span, "mfhd");
        Assert.Equal(fragmentSequence, BinaryPrimitives.ReadUInt32BigEndian(
            media.Payload.Span.Slice(mfhd.Offset + 12, 4)));
        var tfdt = FindBox(media.Payload.Span, "tfdt");
        Assert.Equal(decodeTime, BinaryPrimitives.ReadUInt64BigEndian(
            media.Payload.Span.Slice(tfdt.Offset + 12, 8)));
    }

    private static (int Offset, int Size) FindBox(
        ReadOnlySpan<byte> bytes,
        string type)
    {
        var marker = Encoding.ASCII.GetBytes(type);
        for (var offset = 4; offset <= bytes.Length - marker.Length; offset++)
        {
            if (!bytes.Slice(offset, marker.Length).SequenceEqual(marker))
            {
                continue;
            }

            var start = offset - 4;
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[start..]));
            return (start, size);
        }

        throw new Xunit.Sdk.XunitException($"Box '{type}' was not found.");
    }

    private static int ReadFullBoxFlags(ReadOnlySpan<byte> box) =>
        box[9] << 16 | box[10] << 8 | box[11];

    private static byte[] FullBox(string type, int flags, byte[] payload) =>
        Box(type, Concat(
            [(byte)(flags >> 24), (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags],
            payload));

    private static byte[] Box(string type, byte[] payload)
    {
        var result = new byte[checked(payload.Length + 8)];
        BinaryPrimitives.WriteUInt32BigEndian(result, checked((uint)result.Length));
        Encoding.ASCII.GetBytes(type, result.AsSpan(4, 4));
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] UInt32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] UInt64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Concat(params byte[][] buffers)
    {
        var result = new byte[buffers.Sum(buffer => buffer.Length)];
        var offset = 0;
        foreach (var buffer in buffers)
        {
            buffer.CopyTo(result, offset);
            offset += buffer.Length;
        }

        return result;
    }
}
