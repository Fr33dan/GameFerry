namespace GameFerry.Engine.Disc;

public enum PartitionSelection
{
    /// <summary>Keep every partition (update, game, channels).</summary>
    All,

    /// <summary>Drop the system-update partition; keep the game and any channel partitions.</summary>
    RemoveUpdate,

    /// <summary>Keep only the game data partition.</summary>
    GameOnly,
}

/// <summary>Which sectors of a Wii disc hold real data, as opposed to padding that can be dropped.</summary>
public sealed class DiscUsageMap
{
    private readonly bool[] _sectorUsed;
    private readonly List<string> _warnings = [];

    private DiscUsageMap(int sectorCount, List<WiiPartitionEntry> partitions, List<WiiPartitionEntry> kept)
    {
        _sectorUsed = new bool[sectorCount];
        Partitions = partitions;
        KeptPartitions = kept;
    }

    /// <summary>Every partition the disc's table lists.</summary>
    public IReadOnlyList<WiiPartitionEntry> Partitions { get; }

    /// <summary>The partitions that are kept; the written image's table must list exactly these.</summary>
    public IReadOnlyList<WiiPartitionEntry> KeptPartitions { get; }

    /// <summary>One flag per 0x8000-byte disc sector.</summary>
    public ReadOnlySpan<bool> SectorUsed => _sectorUsed;

    public IReadOnlyList<string> Warnings => _warnings;

    public bool IsKept(WiiPartitionEntry partition) => KeptPartitions.Contains(partition);

    public static DiscUsageMap Build(IDiscReader disc, PartitionSelection selection)
    {
        if (DiscHeader.Read(disc).Kind != DiscKind.Wii)
            throw new InvalidDataException("Not a Wii disc image.");

        long sectorCount = (disc.Length + WiiConstants.SectorSize - 1) / WiiConstants.SectorSize;
        if (sectorCount > WiiConstants.MaxDiscSectors)
            throw new InvalidDataException($"Image is larger than a dual-layer Wii disc ({disc.Length:N0} bytes).");

        List<WiiPartitionEntry> partitions = WiiPartitionTable.Read(disc);
        List<string> warnings = [];
        List<WiiPartitionEntry> kept = [];
        foreach (WiiPartitionEntry entry in partitions.Where(p => Selects(selection, p)))
        {
            // Some tools leave a table entry behind for a partition they blanked out; it can't be kept as a partition.
            if (WiiPartition.IsBlank(disc, entry))
                warnings.Add($"{entry.TypeName} partition at 0x{entry.Offset:X} is blank in the source image and was left out.");
            else
                kept.Add(entry);
        }

        DiscUsageMap map = new((int)sectorCount, partitions, kept);
        map._warnings.AddRange(warnings);

        map.MarkBytes(0, WiiConstants.HeaderAreaSize);
        foreach (WiiPartitionEntry entry in map.KeptPartitions)
            map.MarkPartition(disc, entry);

        if (disc is ISparseDiscReader sparse)
        {
            // A scrubbed source can't supply sectors it never stored.
            for (int i = 0; i < map._sectorUsed.Length; i++)
                map._sectorUsed[i] &= sparse.IsSectorPresent(i);
        }

        return map;
    }

    private static bool Selects(PartitionSelection selection, WiiPartitionEntry partition) => selection switch
    {
        PartitionSelection.RemoveUpdate => !partition.IsUpdate,
        PartitionSelection.GameOnly => partition.IsData,
        _ => true,
    };

    private void MarkPartition(IDiscReader disc, WiiPartitionEntry entry)
    {
        try
        {
            using WiiPartition partition = WiiPartition.Open(disc, entry);
            List<DataExtent> extents = WiiPartitionContents.GetUsedExtents(partition);

            // Ticket, TMD, certificates and the H3 table sit between the partition start and its data area.
            MarkBytes(entry.Offset, partition.DataOffset);

            long firstSector = partition.DataStart / WiiConstants.SectorSize;
            foreach (DataExtent extent in extents)
            {
                long first = extent.Offset / WiiConstants.ClusterDataSize;
                long last = (extent.Offset + extent.Length - 1) / WiiConstants.ClusterDataSize;
                if (last >= partition.ClusterCount)
                {
                    _warnings.Add($"{entry.TypeName} partition at 0x{entry.Offset:X}: a file extends past the end of the partition.");
                    last = partition.ClusterCount - 1;
                }

                for (long cluster = first; cluster <= last; cluster++)
                    MarkSector(firstSector + cluster);
            }
        }
        catch (InvalidDataException ex)
        {
            // Unknown layout: keep the whole partition rather than risk dropping something it needs.
            _warnings.Add($"{entry.TypeName} partition at 0x{entry.Offset:X} could not be analysed ({ex.Message}); keeping all of it.");
            MarkBytes(entry.Offset, WholePartitionEnd(disc, entry) - entry.Offset);
        }
    }

    private long WholePartitionEnd(IDiscReader disc, WiiPartitionEntry entry)
    {
        long limit = Partitions
            .Select(p => p.Offset)
            .Where(offset => offset > entry.Offset)
            .DefaultIfEmpty(disc.Length)
            .Min();

        (long dataOffset, long dataSize) = WiiPartition.ReadDataArea(disc, entry);
        long declaredEnd = entry.Offset + dataOffset + dataSize;
        return dataSize > 0 && declaredEnd > entry.Offset && declaredEnd < limit ? declaredEnd : limit;
    }

    private void MarkBytes(long offset, long length)
    {
        if (length <= 0)
            return;

        long first = offset / WiiConstants.SectorSize;
        long last = (offset + length - 1) / WiiConstants.SectorSize;
        for (long sector = first; sector <= last; sector++)
            MarkSector(sector);
    }

    private void MarkSector(long sector)
    {
        if (sector >= 0 && sector < _sectorUsed.Length)
            _sectorUsed[sector] = true;
    }
}
