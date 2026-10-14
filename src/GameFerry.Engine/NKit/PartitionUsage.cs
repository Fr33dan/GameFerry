using System.Buffers.Binary;
using GameFerry.Engine.Disc;

namespace GameFerry.Engine.NKit;

/// <summary>
/// Marks a kept partition's used sectors while it is restored, by the rules <see cref="DiscUsageMap"/> applies to a
/// finished disc: the system areas as soon as the boot header and file table are in, each file as it is put back.
/// Once the partition is done, <see cref="Verify"/> recomputes the marks from the final file table; any difference
/// would mean blocks were chosen differently from the desktop, so it is an error.
/// </summary>
internal sealed class PartitionUsage
{
    private readonly WiiPartitionEntry _entry;
    private readonly long _dataStart;
    private readonly long _clusterCount;
    private readonly Action<long, long> _mark;
    private readonly List<string> _warnings;
    private readonly bool[] _marked;
    private bool _whole;

    // For a partition NKit kept as raw data: its start, collected as it is restored, until the file table is in.
    private byte[]? _capture;
    private long _captured;

    private PartitionUsage(WiiPartitionEntry entry, long dataStart, long clusterCount, Action<long, long> mark, List<string> warnings)
    {
        _entry = entry;
        _dataStart = dataStart;
        _clusterCount = clusterCount;
        _mark = mark;
        _warnings = warnings;
        _marked = new bool[clusterCount];
    }

    /// <param name="mark">Marks disc bytes as used, as <see cref="IRestoredDiscSink.MarkUsed"/> does.</param>
    public static PartitionUsage Start(WiiPartitionEntry entry, long dataStart, PartitionDataReader reader, Action<long, long> mark, List<string> warnings)
    {
        long hashedSize = reader.HasNoId
            ? PartitionDataReader.DataToHashed(reader.DataSize)
            : BinaryPrimitives.ReadUInt32BigEndian(reader.BootHeader.AsSpan(0x210)) * 4L;
        PartitionUsage usage = new(entry, dataStart, hashedSize / WiiConstants.SectorSize, mark, warnings);
        try
        {
            if (reader.HasNoId)
            {
                // NKit keeps a partition whose data has no ID as raw data, so its file table only turns up as the data
                // streams by. If its boot block is valid, collect the start of the data and analyse it then.
                long needed = BytesToAnalyse(reader.BootHeader, usage.DataLength);
                usage._capture = new byte[needed];
            }
            else
            {
                foreach (DataExtent extent in WiiPartitionContents.GetSystemExtents(usage.DataLength, Known(reader), out _))
                    usage.Mark(extent, warn: true);
            }
        }
        catch (InvalidDataException ex)
        {
            usage.KeepWhole(ex.Message, hashedSize);
        }

        return usage;
    }

    /// <summary>True while the partition's start is still being collected; nothing in it may be decided yet.</summary>
    public bool Deferred => _capture is not null && _captured < _capture.Length;

    /// <summary>Takes restored data in order, for a partition analysed once its file table has gone by.</summary>
    public void Capture(long offset, ReadOnlySpan<byte> data)
    {
        if (!Deferred || offset != _captured)
            return;

        int count = (int)Math.Min(data.Length, _capture!.Length - _captured);
        data[..count].CopyTo(_capture.AsSpan((int)_captured));
        _captured += count;
        if (Deferred)
            return;

        try
        {
            foreach (DataExtent extent in WiiPartitionContents.GetUsedExtents(DataLength, Captured()))
                Mark(extent, warn: true);
        }
        catch (InvalidDataException ex)
        {
            KeepWhole(ex.Message, _clusterCount * WiiConstants.SectorSize);
        }
    }

    private long DataLength => _clusterCount * WiiConstants.ClusterDataSize;

    /// <summary>
    /// How much of the data area GetUsedExtents reads: the apploader header, the DOL header and the file table, all
    /// of which the boot block locates. Throws as GetSystemExtents would if the boot block can't be analysed.
    /// </summary>
    private static long BytesToAnalyse(byte[] boot, long dataLength)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(boot.AsSpan(0x18)) != WiiConstants.WiiMagic)
            throw new InvalidDataException("decrypted boot block has no Wii magic");
        long dolOffset = (long)BinaryPrimitives.ReadUInt32BigEndian(boot.AsSpan(0x420)) << 2;
        long fstOffset = (long)BinaryPrimitives.ReadUInt32BigEndian(boot.AsSpan(0x424)) << 2;
        long fstSize = (long)BinaryPrimitives.ReadUInt32BigEndian(boot.AsSpan(0x428)) << 2;
        if (fstSize < 12 || fstSize > 256L << 20 || fstOffset + fstSize > dataLength || dolOffset + 0x100 > dataLength)
            throw new InvalidDataException($"boot block points outside the partition (DOL 0x{dolOffset:X}, FST 0x{fstOffset:X}+0x{fstSize:X})");
        return Math.Max(0x2460, Math.Max(dolOffset + 0x100, fstOffset + fstSize));
    }

    /// <summary>Unknown layout: keep the whole partition rather than risk dropping something it needs.</summary>
    private void KeepWhole(string reason, long hashedSize)
    {
        _warnings.Add($"{_entry.TypeName} partition at 0x{_entry.Offset:X} could not be analysed ({reason}); keeping all of it.");
        _whole = true;
        _capture = null;
        _mark(_entry.Offset, _dataStart + hashedSize - _entry.Offset);
    }

    private PartitionDataSource Captured() => (offset, buffer) =>
    {
        if (offset < 0 || offset + buffer.Length > _captured)
            throw new InvalidDataException($"partition data at 0x{offset:X} lies beyond what was collected");
        _capture.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
    };

    public void MarkFile(long offset, long length)
    {
        if (!_whole)
            Mark(new DataExtent(offset, length), warn: true);
    }

    public void Verify(PartitionDataReader reader)
    {
        if (_whole)
            return;

        bool[] expected = new bool[_clusterCount];
        foreach (DataExtent extent in WiiPartitionContents.GetUsedExtents(DataLength, _capture is null ? Known(reader) : Captured()))
        {
            (long first, long last) = Clusters(extent);
            for (long cluster = first; cluster <= last; cluster++)
                expected[cluster] = true;
        }

        if (!expected.AsSpan().SequenceEqual(_marked))
        {
            throw new InvalidOperationException(
                $"{_entry.TypeName} partition at 0x{_entry.Offset:X}: the sectors marked while restoring differ from the finished file table's.");
        }
    }

    private void Mark(DataExtent extent, bool warn)
    {
        (long first, long last) = Clusters(extent);
        if (warn && extent.Length > 0 && (extent.Offset + extent.Length - 1) / WiiConstants.ClusterDataSize >= _clusterCount)
            _warnings.Add($"{_entry.TypeName} partition at 0x{_entry.Offset:X}: a file extends past the end of the partition.");
        if (first > last)
            return;

        for (long cluster = first; cluster <= last; cluster++)
            _marked[cluster] = true;
        _mark(_dataStart + first * WiiConstants.SectorSize, (last - first + 1) * WiiConstants.SectorSize);
    }

    /// <summary>The clusters an extent touches, cut off at the end of the partition as DiscUsageMap does.</summary>
    private (long First, long Last) Clusters(DataExtent extent)
    {
        long first = extent.Offset / WiiConstants.ClusterDataSize;
        long last = (extent.Offset + extent.Length - 1) / WiiConstants.ClusterDataSize;
        return (first, Math.Min(last, _clusterCount - 1));
    }

    /// <summary>The partition data held in memory: everything up to the end of the file table.</summary>
    private static PartitionDataSource Known(PartitionDataReader reader) => (offset, buffer) =>
    {
        long fstEnd = reader.FstOffset + reader.Fst.Length;
        for (int i = 0; i < buffer.Length;)
        {
            long at = offset + i;
            ReadOnlySpan<byte> source;
            if (at < PartitionDataReader.BootHeaderSize)
                source = reader.BootHeader.AsSpan((int)at);
            else if (at < reader.FstOffset && reader.HeaderToFst.Length > 0)
                source = reader.HeaderToFst.AsSpan((int)(at - PartitionDataReader.BootHeaderSize));
            else if (at >= reader.FstOffset && at < fstEnd && reader.Fst.Length > 0)
                source = reader.Fst.AsSpan((int)(at - reader.FstOffset));
            else
                throw new NotSupportedException($"Partition data at 0x{at:X} is needed to find the used areas before it has been restored.");

            int count = Math.Min(buffer.Length - i, source.Length);
            source[..count].CopyTo(buffer[i..]);
            i += count;
        }
    };
}
