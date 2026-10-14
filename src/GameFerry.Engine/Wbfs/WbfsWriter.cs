using System.Buffers.Binary;
using GameFerry.Engine.Disc;

namespace GameFerry.Engine.Wbfs;

/// <summary>What a disc will look like as a WBFS file, worked out before anything is written.</summary>
public sealed class WbfsPlan
{
    private readonly ushort[] _blockMap;

    internal WbfsPlan(DiscUsageMap usage, ushort[] blockMap, int storedBlocks)
    {
        Usage = usage;
        _blockMap = blockMap;
        StoredBlocks = storedBlocks;
    }

    public DiscUsageMap Usage { get; }

    /// <summary>For each disc block, the WBFS block that stores it, or 0 if it is dropped.</summary>
    public ReadOnlySpan<ushort> BlockMap => _blockMap;

    public int StoredBlocks { get; }

    /// <summary>Exact size of the finished file (all parts together): the header block plus every stored block.</summary>
    public long OutputSize => (long)(StoredBlocks + 1) * WbfsFormat.BlockSize;
}

/// <summary>One piece of a WBFS image: <see cref="Data"/> belongs at <see cref="Position"/> in the finished file.</summary>
public readonly record struct WbfsChunk(long Position, ReadOnlyMemory<byte> Data);

public static class WbfsWriter
{
    /// <summary>Analyses the disc and decides which blocks to store.</summary>
    public static WbfsPlan Plan(IDiscReader disc, PartitionSelection selection)
    {
        DiscUsageMap usage = DiscUsageMap.Build(disc, selection);
        ReadOnlySpan<bool> sectors = usage.SectorUsed;

        int mappedSectors = WbfsFormat.BlocksPerDisc * WbfsFormat.SectorsPerBlock;
        if (sectors.Length > mappedSectors && sectors[mappedSectors..].Contains(true))
            throw new InvalidDataException("The image uses sectors beyond what a WBFS file can address.");

        ushort[] blockMap = new ushort[WbfsFormat.BlocksPerDisc];
        int stored = 0;
        for (int block = 0; block < blockMap.Length; block++)
        {
            int first = block * WbfsFormat.SectorsPerBlock;
            if (first >= sectors.Length)
                break;

            int count = Math.Min(WbfsFormat.SectorsPerBlock, sectors.Length - first);
            if (sectors.Slice(first, count).Contains(true))
                blockMap[block] = (ushort)++stored;
        }

        return new WbfsPlan(usage, blockMap, stored);
    }

    /// <summary>
    /// Produces the planned WBFS image in file order, one block at a time: the header block, then every stored
    /// disc block. Each chunk's memory is reused for the next one, so consume it before moving on.
    /// </summary>
    public static IEnumerable<WbfsChunk> Produce(IDiscReader disc, WbfsPlan plan, CancellationToken cancellationToken = default)
    {
        byte[] block = new byte[WbfsFormat.BlockSize];
        long position = 0;

        BuildHeaderBlock(disc, plan, block);
        yield return new WbfsChunk(position, block);
        position += block.Length;

        for (int discBlock = 0; discBlock < plan.BlockMap.Length; discBlock++)
        {
            if (plan.BlockMap[discBlock] == 0)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            disc.ReadAt((long)discBlock * WbfsFormat.BlockSize, block);

            // The partition table lives in the first block and must only list what was kept.
            if (discBlock == 0 && plan.Usage.KeptPartitions.Count != plan.Usage.Partitions.Count)
                WiiPartitionTable.Rewrite(block, plan.Usage.IsKept);

            yield return new WbfsChunk(position, block);
            position += block.Length;
        }
    }

    private static void BuildHeaderBlock(IDiscReader disc, WbfsPlan plan, Span<byte> block)
    {
        block.Clear();
        "WBFS"u8.CopyTo(block);
        BinaryPrimitives.WriteUInt32BigEndian(block[4..], (uint)(plan.OutputSize >> WbfsFormat.HdSectorShift));
        block[8] = WbfsFormat.HdSectorShift;
        block[9] = WbfsFormat.BlockShift;
        block[WbfsFormat.DiscTableOffset] = 1;

        Span<byte> discInfo = block[WbfsFormat.HdSectorSize..];
        disc.ReadAt(0, discInfo[..WbfsFormat.DiscHeaderCopySize]);

        Span<byte> map = discInfo[WbfsFormat.DiscHeaderCopySize..];
        ReadOnlySpan<ushort> blockMap = plan.BlockMap;
        for (int i = 0; i < blockMap.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(map[(i * 2)..], blockMap[i]);
    }
}
