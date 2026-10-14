using GameFerry.Engine.Disc;
using GameFerry.Engine.Wbfs;

namespace GameFerry.Core.Wbfs;

/// <summary>Result of checking a WBFS file against the disc structure it contains.</summary>
public sealed record WbfsCheckResult
{
    public required WbfsFileInfo Info { get; init; }
    public required IReadOnlyList<WiiPartitionEntry> Partitions { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Sectors the disc's own file tables say hold data.</summary>
    public required int UsedSectors { get; init; }

    /// <summary>Used sectors the file does not store. Anything above zero means missing game data.</summary>
    public required int MissingSectors { get; init; }

    /// <summary>Stored blocks that hold no used sector: harmless, just larger than necessary.</summary>
    public required int SurplusBlocks { get; init; }

    /// <summary>False if the block map points outside the file or at the same block twice.</summary>
    public required bool BlockMapValid { get; init; }

    public bool Ok => BlockMapValid && MissingSectors == 0 && Info.SizeMatchesHeader && Warnings.Count == 0;
}

/// <summary>How two WBFS files of the same disc differ.</summary>
public sealed record WbfsComparison
{
    public required bool DiscHeadersEqual { get; init; }
    public required int BlocksInBoth { get; init; }
    public required int BlocksOnlyInFirst { get; init; }
    public required int BlocksOnlyInSecond { get; init; }

    /// <summary>Disc block numbers stored by both files whose contents differ.</summary>
    public required IReadOnlyList<int> DifferingBlocks { get; init; }

    /// <summary>Byte ranges (disc offset, length) that differ within the first block, where the partition table is.</summary>
    public required IReadOnlyList<(long Offset, int Length)> FirstBlockDifferences { get; init; }

    public bool SameMap => BlocksOnlyInFirst == 0 && BlocksOnlyInSecond == 0;
}

public static class WbfsInspector
{
    private const int MaxReportedRanges = 32;

    /// <summary>
    /// Opens a WBFS file, walks every partition's file table, and confirms the file stores every sector they use.
    /// </summary>
    public static WbfsCheckResult Check(string wbfsPath)
    {
        using WbfsReader reader = WbfsReader.Open(wbfsPath);

        // Analyse the disc as if it were complete, then see what the file actually holds.
        DiscUsageMap usage = DiscUsageMap.Build(new FullDiscView(reader), PartitionSelection.All);
        ReadOnlySpan<bool> used = usage.SectorUsed;

        int usedSectors = 0, missing = 0;
        for (int sector = 0; sector < used.Length; sector++)
        {
            if (!used[sector])
                continue;

            usedSectors++;
            if (!reader.IsSectorPresent(sector))
                missing++;
        }

        int sectorsPerBlock = (int)(reader.Info.BlockSize / WiiConstants.SectorSize);
        long blocksInFile = reader.Info.TotalSize >> reader.Info.BlockShift;
        HashSet<ushort> seen = [];
        bool mapValid = true;
        int surplus = 0;
        ReadOnlySpan<ushort> blockMap = reader.BlockMap;
        for (int block = 0; block < blockMap.Length; block++)
        {
            ushort stored = blockMap[block];
            if (stored == 0)
                continue;

            if (stored >= blocksInFile || !seen.Add(stored))
                mapValid = false;

            int first = block * sectorsPerBlock;
            int count = Math.Min(sectorsPerBlock, Math.Max(0, used.Length - first));
            if (count == 0 || !used.Slice(first, count).Contains(true))
                surplus++;
        }

        return new WbfsCheckResult
        {
            Info = reader.Info,
            Partitions = usage.Partitions,
            Warnings = usage.Warnings,
            UsedSectors = usedSectors,
            MissingSectors = missing,
            SurplusBlocks = surplus,
            BlockMapValid = mapValid,
        };
    }

    /// <summary>Compares two WBFS files block by block, in disc terms rather than file layout.</summary>
    /// <param name="progress">Receives the fraction of the disc compared.</param>
    public static WbfsComparison Compare(string firstPath, string secondPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using WbfsReader first = WbfsReader.Open(firstPath);
        using WbfsReader second = WbfsReader.Open(secondPath);

        byte[] a = new byte[WbfsFormat.BlockSize];
        byte[] b = new byte[WbfsFormat.BlockSize];

        int both = 0, onlyFirst = 0, onlySecond = 0;
        List<int> differing = [];
        List<(long, int)> firstBlockDifferences = [];

        for (int block = 0; block < WbfsFormat.BlocksPerDisc; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long sector = (long)block * WbfsFormat.SectorsPerBlock;
            bool inFirst = first.IsSectorPresent(sector);
            bool inSecond = second.IsSectorPresent(sector);
            if (!inFirst && !inSecond)
                continue;

            if (inFirst != inSecond)
            {
                if (inFirst)
                    onlyFirst++;
                else
                    onlySecond++;
                continue;
            }

            both++;
            first.ReadAt((long)block * WbfsFormat.BlockSize, a);
            second.ReadAt((long)block * WbfsFormat.BlockSize, b);
            if (!a.AsSpan().SequenceEqual(b))
            {
                differing.Add(block);
                if (block == 0)
                    firstBlockDifferences = DifferingRanges(a, b);
            }

            progress?.Report((block + 1) / (double)WbfsFormat.BlocksPerDisc);
        }

        return new WbfsComparison
        {
            DiscHeadersEqual = first.Info.Disc == second.Info.Disc,
            BlocksInBoth = both,
            BlocksOnlyInFirst = onlyFirst,
            BlocksOnlyInSecond = onlySecond,
            DifferingBlocks = differing,
            FirstBlockDifferences = firstBlockDifferences,
        };
    }

    private static List<(long, int)> DifferingRanges(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        List<(long, int)> ranges = [];
        int index = 0;
        while (index < a.Length && ranges.Count < MaxReportedRanges)
        {
            if (a[index] == b[index])
            {
                index++;
                continue;
            }

            int start = index;
            while (index < a.Length && a[index] != b[index])
                index++;
            ranges.Add((start, index - start));
        }

        return ranges;
    }

    /// <summary>Hides a reader's sparseness so the disc is analysed without regard to what is stored.</summary>
    private sealed class FullDiscView(IDiscReader inner) : IDiscReader
    {
        public long Length => inner.Length;

        public void ReadAt(long offset, Span<byte> buffer) => inner.ReadAt(offset, buffer);

        public void Dispose()
        {
        }
    }
}
