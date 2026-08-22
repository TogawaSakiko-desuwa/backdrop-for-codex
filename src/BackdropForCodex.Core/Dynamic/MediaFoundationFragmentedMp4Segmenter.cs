using System.Buffers.Binary;

namespace BackdropForCodex.Core.Dynamic;

/// <summary>
/// Rewrites finalized Media Foundation fragments into independently appendable MSE segments.
/// Media Foundation emits an absolute base-data-offset and no tfdt; both are unsafe after a
/// queued GOP is dropped. The rewrite uses default-base-is-moof and an explicit decode time.
/// </summary>
internal sealed class MediaFoundationFragmentedMp4Segmenter
{
    private const int MaximumFinalizedChunkBytes =
        EncodedWallpaperStreamBuffer.MaximumBufferedBytes + (1024 * 1024);

    private const uint BoxFtyp = 0x66747970;
    private const uint BoxMdat = 0x6D646174;
    private const uint BoxMfhd = 0x6D666864;
    private const uint BoxMfra = 0x6D667261;
    private const uint BoxMoof = 0x6D6F6F66;
    private const uint BoxMoov = 0x6D6F6F76;
    private const uint BoxTfdt = 0x74666474;
    private const uint BoxTfhd = 0x74666864;
    private const uint BoxTraf = 0x74726166;
    private const uint BoxTrun = 0x7472756E;

    private readonly long _generation;
    private ulong _decodeTime;
    private long _nextSequence;
    private bool _initializationEmitted;
    private PendingBatch? _pendingBatch;

    internal MediaFoundationFragmentedMp4Segmenter(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        _generation = generation;
    }

    internal IReadOnlyList<EncodedWallpaperSegment> Split(
        ReadOnlyMemory<byte> finalizedFile)
    {
        if (_pendingBatch.HasValue)
        {
            throw new InvalidOperationException(
                "The previous encoded segment batch must be committed or discarded first.");
        }

        if (finalizedFile.IsEmpty || finalizedFile.Length > MaximumFinalizedChunkBytes)
        {
            throw EncodingFailure();
        }

        try
        {
            var source = finalizedFile.Span;
            var boxes = ReadBoxes(source, 0, source.Length);
            var firstMoofIndex = boxes.FindIndex(box => box.Type == BoxMoof);
            if (firstMoofIndex <= 0 ||
                !boxes.Take(firstMoofIndex).Any(box => box.Type == BoxFtyp) ||
                !boxes.Take(firstMoofIndex).Any(box => box.Type == BoxMoov))
            {
                throw EncodingFailure();
            }

            var initializationLength = boxes[firstMoofIndex].Offset;
            if (initializationLength is <= 0 or > EncodedWallpaperStreamBuffer.MaximumBufferedBytes ||
                !HasHighProfileLevel40AvcConfiguration(source[..initializationLength]))
            {
                throw EncodingFailure();
            }

            var nextSequence = _nextSequence;
            var decodeTime = _decodeTime;
            var segments = new List<EncodedWallpaperSegment>();
            if (!_initializationEmitted)
            {
                segments.Add(new EncodedWallpaperSegment(
                    _generation,
                    nextSequence++,
                    EncodedWallpaperSegmentKind.Initialization,
                    isKeyFrame: false,
                    source[..initializationLength].ToArray()));
            }

            for (var boxIndex = firstMoofIndex; boxIndex < boxes.Count; boxIndex++)
            {
                var moof = boxes[boxIndex];
                if (moof.Type == BoxMfra)
                {
                    break;
                }

                if (moof.Type != BoxMoof ||
                    boxIndex + 1 >= boxes.Count ||
                    boxes[boxIndex + 1].Type != BoxMdat)
                {
                    throw EncodingFailure();
                }

                var mdat = boxes[++boxIndex];
                var rewrite = RewriteMoof(
                    source.Slice(moof.Offset, moof.Size),
                    decodeTime,
                    checked((uint)nextSequence));
                decodeTime = checked(decodeTime + rewrite.Duration);
                var payloadLength = checked(rewrite.Payload.Length + mdat.Size);
                if (payloadLength > EncodedWallpaperStreamBuffer.MaximumBufferedBytes)
                {
                    throw EncodingFailure();
                }

                var payload = new byte[payloadLength];
                rewrite.Payload.CopyTo(payload, 0);
                source.Slice(mdat.Offset, mdat.Size).CopyTo(
                    payload.AsSpan(rewrite.Payload.Length));
                var isKeyFrame = rewrite.FirstSampleIsClean &&
                    rewrite.FirstSampleSize <= mdat.Size - 8 &&
                    ContainsIdrNal(source.Slice(
                        mdat.Offset + 8,
                        rewrite.FirstSampleSize));
                segments.Add(new EncodedWallpaperSegment(
                    _generation,
                    nextSequence++,
                    EncodedWallpaperSegmentKind.Media,
                    isKeyFrame,
                    payload));
            }

            if (segments.Count == (_initializationEmitted ? 0 : 1))
            {
                throw EncodingFailure();
            }

            _pendingBatch = new PendingBatch(
                nextSequence,
                decodeTime,
                InitializationEmitted: true);
            return segments;
        }
        catch (DynamicWallpaperUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                ArithmeticException or
                IndexOutOfRangeException)
        {
            throw EncodingFailure(exception);
        }
    }

    /// <summary>
    /// Commits sequence and decode-time progress only after the complete batch has been accepted by
    /// the downstream stream. This keeps a canceled publication from creating an MSE timeline gap.
    /// </summary>
    internal void CommitPendingBatch()
    {
        var pending = _pendingBatch ?? throw new InvalidOperationException(
            "No encoded segment batch is awaiting publication.");
        _nextSequence = pending.NextSequence;
        _decodeTime = pending.DecodeTime;
        _initializationEmitted = pending.InitializationEmitted;
        _pendingBatch = null;
    }

    /// <summary>
    /// Commits a pending batch only when the sink reports that the whole publication was accepted.
    /// A dropped or closed write may have exposed only part of the batch, so the current generation
    /// must fail closed without advancing the segmenter's retry timeline.
    /// </summary>
    internal bool TryCommitPendingBatch(EncodedWallpaperWriteResult result)
    {
        if (result is EncodedWallpaperWriteResult.Accepted or
            EncodedWallpaperWriteResult.RecoveredAtKeyFrame or
            EncodedWallpaperWriteResult.RecoveredAfterQueueDrain)
        {
            CommitPendingBatch();
            return true;
        }

        DiscardPendingBatch();
        return false;
    }

    /// <summary>
    /// Abandons an unpublished batch without advancing the externally visible stream timeline.
    /// </summary>
    internal void DiscardPendingBatch()
    {
        _pendingBatch = null;
    }

    private static MoofRewrite RewriteMoof(
        ReadOnlySpan<byte> original,
        ulong decodeTime,
        uint fragmentSequence)
    {
        var children = ReadBoxes(original, 8, original.Length - 8);
        var mfhd = RequireSingle(children, BoxMfhd);
        var traf = RequireSingle(children, BoxTraf);
        var trafChildren = ReadBoxes(original, traf.Offset + 8, traf.Size - 8);
        var tfhd = RequireSingle(trafChildren, BoxTfhd);
        var trun = RequireSingle(trafChildren, BoxTrun);
        if (trafChildren.Any(box => box.Type == BoxTfdt))
        {
            throw EncodingFailure();
        }

        ValidateMediaFoundationTfhd(original.Slice(tfhd.Offset, tfhd.Size));
        var trunInfo = ReadTrun(original.Slice(trun.Offset, trun.Size));

        var rewrittenTfhd = CreateTfhd(original.Slice(tfhd.Offset, tfhd.Size));
        var rewrittenTfdt = CreateTfdt(decodeTime);
        var rewrittenTrafContentLength = checked(
            trafChildren.Sum(child => child.Type == BoxTfhd ? rewrittenTfhd.Length : child.Size) +
            rewrittenTfdt.Length);
        var rewrittenTraf = new byte[checked(rewrittenTrafContentLength + 8)];
        WriteBoxHeader(rewrittenTraf, BoxTraf);
        var trafDestination = 8;
        foreach (var child in trafChildren)
        {
            if (child.Type == BoxTfhd)
            {
                rewrittenTfhd.CopyTo(rewrittenTraf, trafDestination);
                trafDestination += rewrittenTfhd.Length;
                rewrittenTfdt.CopyTo(rewrittenTraf, trafDestination);
                trafDestination += rewrittenTfdt.Length;
                continue;
            }

            original.Slice(child.Offset, child.Size).CopyTo(
                rewrittenTraf.AsSpan(trafDestination));
            trafDestination += child.Size;
        }

        var rewrittenContentLength = checked(
            children.Sum(child => child.Type == BoxTraf ? rewrittenTraf.Length : child.Size));
        var rewritten = new byte[checked(rewrittenContentLength + 8)];
        WriteBoxHeader(rewritten, BoxMoof);
        var destination = 8;
        foreach (var child in children)
        {
            if (child.Type == BoxTraf)
            {
                rewrittenTraf.CopyTo(rewritten, destination);
                destination += rewrittenTraf.Length;
                continue;
            }

            original.Slice(child.Offset, child.Size).CopyTo(rewritten.AsSpan(destination));
            if (child.Type == BoxMfhd)
            {
                if (child.Size < 16)
                {
                    throw EncodingFailure();
                }

                BinaryPrimitives.WriteUInt32BigEndian(
                    rewritten.AsSpan(destination + 12, 4),
                    fragmentSequence);
            }

            destination += child.Size;
        }

        var rewrittenBoxes = ReadBoxes(rewritten, 8, rewritten.Length - 8);
        var rewrittenTrafBox = RequireSingle(rewrittenBoxes, BoxTraf);
        var rewrittenTrafChildren = ReadBoxes(
            rewritten,
            rewrittenTrafBox.Offset + 8,
            rewrittenTrafBox.Size - 8);
        var rewrittenTrun = RequireSingle(rewrittenTrafChildren, BoxTrun);
        BinaryPrimitives.WriteInt32BigEndian(
            rewritten.AsSpan(rewrittenTrun.Offset + trunInfo.DataOffsetFieldOffset, 4),
            checked(rewritten.Length + 8));
        return new MoofRewrite(
            rewritten,
            trunInfo.Duration,
            trunInfo.FirstSampleIsClean,
            trunInfo.FirstSampleSize);
    }

    private static void ValidateMediaFoundationTfhd(ReadOnlySpan<byte> box)
    {
        if (box.Length < 24 || box[8] != 0 || ReadFlags(box) != 1)
        {
            throw EncodingFailure();
        }

        if (!box[24..].IsEmpty && box[24..].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw EncodingFailure();
        }
    }

    private static byte[] CreateTfhd(ReadOnlySpan<byte> original)
    {
        var result = new byte[16];
        WriteBoxHeader(result, BoxTfhd);
        result[8] = 0;
        WriteFlags(result, 0x020000);
        original.Slice(12, 4).CopyTo(result.AsSpan(12));
        return result;
    }

    private static byte[] CreateTfdt(ulong decodeTime)
    {
        var result = new byte[20];
        WriteBoxHeader(result, BoxTfdt);
        result[8] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(12), decodeTime);
        return result;
    }

    private static TrunInfo ReadTrun(ReadOnlySpan<byte> box)
    {
        if (box.Length < 20)
        {
            throw EncodingFailure();
        }

        var flags = ReadFlags(box);
        const int dataOffsetPresent = 0x000001;
        const int firstSampleFlagsPresent = 0x000004;
        const int sampleDurationPresent = 0x000100;
        const int sampleSizePresent = 0x000200;
        const int sampleFlagsPresent = 0x000400;
        const int compositionOffsetPresent = 0x000800;
        if ((flags & (dataOffsetPresent |
                sampleDurationPresent |
                sampleSizePresent |
                sampleFlagsPresent)) !=
            (dataOffsetPresent |
                sampleDurationPresent |
                sampleSizePresent |
                sampleFlagsPresent))
        {
            throw EncodingFailure();
        }

        var sampleCount = BinaryPrimitives.ReadUInt32BigEndian(box.Slice(12, 4));
        if (sampleCount is 0 or > 240)
        {
            throw EncodingFailure();
        }

        var cursor = 16;
        var dataOffsetFieldOffset = cursor;
        cursor += 4;
        uint? firstSampleFlags = null;
        if ((flags & firstSampleFlagsPresent) != 0)
        {
            EnsureAvailable(box, cursor, 4);
            firstSampleFlags = BinaryPrimitives.ReadUInt32BigEndian(box.Slice(cursor, 4));
            cursor += 4;
        }

        ulong duration = 0;
        var firstSampleSize = 0;
        for (var sampleIndex = 0u; sampleIndex < sampleCount; sampleIndex++)
        {
            EnsureAvailable(box, cursor, 4);
            duration = checked(duration + BinaryPrimitives.ReadUInt32BigEndian(
                box.Slice(cursor, 4)));
            cursor += 4;
            EnsureAvailable(box, cursor, 4);
            var sampleSize = BinaryPrimitives.ReadUInt32BigEndian(box.Slice(cursor, 4));
            cursor += 4;
            if (sampleSize > int.MaxValue)
            {
                throw EncodingFailure();
            }

            if (sampleIndex == 0)
            {
                firstSampleSize = checked((int)sampleSize);
            }

            EnsureAvailable(box, cursor, 4);
            var sampleFlags = BinaryPrimitives.ReadUInt32BigEndian(box.Slice(cursor, 4));
            cursor += 4;
            if (sampleIndex == 0 && !firstSampleFlags.HasValue)
            {
                firstSampleFlags = sampleFlags;
            }

            if ((flags & compositionOffsetPresent) != 0)
            {
                EnsureAvailable(box, cursor, 4);
                cursor += 4;
            }
        }

        if (cursor > box.Length || !firstSampleFlags.HasValue)
        {
            throw EncodingFailure();
        }

        return new TrunInfo(
            dataOffsetFieldOffset,
            duration,
            (firstSampleFlags.Value & 0x00010000) == 0,
            firstSampleSize);
    }

    private static bool ContainsIdrNal(ReadOnlySpan<byte> payload)
    {
        var hasIdr = false;
        for (var cursor = 0; cursor <= payload.Length - 5;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(payload[cursor..]);
            cursor += 4;
            if (length == 0 || length > payload.Length - cursor)
            {
                return false;
            }

            if ((payload[cursor] & 0x1F) == 5)
            {
                hasIdr = true;
            }

            cursor += checked((int)length);
            if (cursor == payload.Length)
            {
                return hasIdr;
            }
        }

        return false;
    }

    private static bool HasHighProfileLevel40AvcConfiguration(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> marker = "avcC"u8;
        for (var offset = 4; offset <= bytes.Length - 8; offset++)
        {
            if (bytes.Slice(offset, 4).SequenceEqual(marker) &&
                bytes[offset + 4] == 1 &&
                bytes[offset + 5] == 0x64 &&
                bytes[offset + 6] == 0 &&
                bytes[offset + 7] == 0x28)
            {
                return true;
            }
        }

        return false;
    }

    private static List<IsoBox> ReadBoxes(
        ReadOnlySpan<byte> bytes,
        int offset,
        int length)
    {
        var end = checked(offset + length);
        if (offset < 0 || end > bytes.Length)
        {
            throw EncodingFailure();
        }

        var boxes = new List<IsoBox>();
        while (offset < end)
        {
            if (end - offset < 8)
            {
                throw EncodingFailure();
            }

            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (size is < 8 or > int.MaxValue || size > end - offset)
            {
                throw EncodingFailure();
            }

            boxes.Add(new IsoBox(
                offset,
                checked((int)size),
                BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 4, 4))));
            offset += checked((int)size);
        }

        return boxes;
    }

    private static IsoBox RequireSingle(List<IsoBox> boxes, uint type)
    {
        var matches = boxes.Where(box => box.Type == type).ToArray();
        return matches.Length == 1 ? matches[0] : throw EncodingFailure();
    }

    private static int ReadFlags(ReadOnlySpan<byte> box) =>
        box[9] << 16 | box[10] << 8 | box[11];

    private static void WriteFlags(Span<byte> box, int flags)
    {
        box[9] = checked((byte)((flags >> 16) & 0xFF));
        box[10] = checked((byte)((flags >> 8) & 0xFF));
        box[11] = checked((byte)(flags & 0xFF));
    }

    private static void WriteBoxHeader(Span<byte> destination, uint type)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, checked((uint)destination.Length));
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], type);
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> bytes, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > bytes.Length - count)
        {
            throw EncodingFailure();
        }
    }

    private static DynamicWallpaperUnavailableException EncodingFailure() =>
        new(DynamicWallpaperCapabilityReasonCode.EncodingFailed);

    private static DynamicWallpaperUnavailableException EncodingFailure(Exception inner) =>
        new(DynamicWallpaperCapabilityReasonCode.EncodingFailed, inner);

    private readonly record struct IsoBox(int Offset, int Size, uint Type);

    private readonly record struct TrunInfo(
        int DataOffsetFieldOffset,
        ulong Duration,
        bool FirstSampleIsClean,
        int FirstSampleSize);

    private readonly record struct MoofRewrite(
        byte[] Payload,
        ulong Duration,
        bool FirstSampleIsClean,
        int FirstSampleSize);

    private readonly record struct PendingBatch(
        long NextSequence,
        ulong DecodeTime,
        bool InitializationEmitted);
}
