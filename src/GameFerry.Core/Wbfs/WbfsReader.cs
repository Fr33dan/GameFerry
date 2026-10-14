using System.Buffers.Binary;
using GameFerry.Engine.Disc;
using GameFerry.Engine.Wbfs;

namespace GameFerry.Core.Wbfs;

/// <summary>Header-level facts about a WBFS file (all parts of a split set together).</summary>
public sealed record WbfsFileInfo
{
    public required string Path { get; init; }
    public required IReadOnlyList<string> Parts { get; init; }
    public required long TotalSize { get; init; }
    public required DiscHeader Disc { get; init; }
    public required uint HdSectorCount { get; init; }
    public required int HdSectorShift { get; init; }
    public required int BlockShift { get; init; }
    public required int StoredBlocks { get; init; }
    public required int HighestStoredBlock { get; init; }
    public required int LastDiscBlock { get; init; }

    public long BlockSize => 1L << BlockShift;

    /// <summary>True when the header's sector count agrees with the size of the files on disk.</summary>
    public bool SizeMatchesHeader => ((long)HdSectorCount << HdSectorShift) == TotalSize;
}

/// <summary>Reads a single-disc WBFS file (optionally split) as a disc image.</summary>
public sealed class WbfsReader : ISparseDiscReader
{
    private readonly SplitFileReader _file;
    private readonly ushort[] _blockMap;

    private WbfsReader(SplitFileReader file, ushort[] blockMap, WbfsFileInfo info)
    {
        _file = file;
        _blockMap = blockMap;
        Info = info;

        long mappedEnd = (long)(info.LastDiscBlock + 1) << info.BlockShift;
        Length = mappedEnd > WiiConstants.SingleLayerSize ? WiiConstants.DualLayerSize : WiiConstants.SingleLayerSize;
    }

    public WbfsFileInfo Info { get; }

    /// <summary>For each disc block, the WBFS block that stores it, or 0.</summary>
    public ReadOnlySpan<ushort> BlockMap => _blockMap;

    public long Length { get; }

    public static WbfsReader Open(string wbfsPath)
    {
        SplitFileReader file = new(SplitFileReader.FindParts(wbfsPath));
        try
        {
            (WbfsFileInfo info, ushort[] blockMap) = ReadHeader(wbfsPath, file);
            return new WbfsReader(file, blockMap, info);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Reads just the header; returns null if the file isn't a usable WBFS image.</summary>
    public static WbfsFileInfo? TryReadInfo(string wbfsPath)
    {
        try
        {
            using SplitFileReader file = new(SplitFileReader.FindParts(wbfsPath));
            return ReadHeader(wbfsPath, file).Info;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void ReadAt(long offset, Span<byte> buffer)
    {
        long blockSize = Info.BlockSize;
        while (!buffer.IsEmpty)
        {
            long discBlock = Math.DivRem(offset, blockSize, out long within);
            int count = (int)Math.Min(buffer.Length, blockSize - within);

            ushort stored = discBlock < _blockMap.Length ? _blockMap[discBlock] : (ushort)0;
            if (stored == 0)
                buffer[..count].Clear();
            else
                _file.ReadAt(((long)stored << Info.BlockShift) + within, buffer[..count]);

            buffer = buffer[count..];
            offset += count;
        }
    }

    public bool IsSectorPresent(long sectorIndex)
    {
        long discBlock = (sectorIndex * WiiConstants.SectorSize) >> Info.BlockShift;
        return discBlock < _blockMap.Length && _blockMap[discBlock] != 0;
    }

    public void Dispose() => _file.Dispose();

    private static (WbfsFileInfo Info, ushort[] BlockMap) ReadHeader(string wbfsPath, SplitFileReader file)
    {
        byte[] head = new byte[WbfsFormat.HdSectorSize];
        file.ReadAt(0, head);
        if (!head.AsSpan(0, 4).SequenceEqual("WBFS"u8))
            throw new InvalidDataException("Not a WBFS file.");

        int hdSectorShift = head[8];
        int blockShift = head[9];
        if (hdSectorShift is < 9 or > 12 || blockShift is < 15 or > 30)
            throw new InvalidDataException($"Unsupported WBFS geometry (sector shift {hdSectorShift}, block shift {blockShift}).");

        int hdSectorSize = 1 << hdSectorShift;
        if (hdSectorSize > head.Length)
        {
            head = new byte[hdSectorSize];
            file.ReadAt(0, head);
        }

        int blocksPerDisc = WbfsFormat.BlocksPerDiscFor(blockShift);
        int discInfoSize = AlignUp(WbfsFormat.DiscHeaderCopySize + blocksPerDisc * 2, hdSectorSize);

        // A file normally holds one disc in slot 0, but honour the disc table anyway.
        int slot = head.AsSpan(WbfsFormat.DiscTableOffset).IndexOfAnyExcept((byte)0);
        if (slot < 0)
            throw new InvalidDataException("The WBFS file contains no disc.");

        byte[] discInfo = new byte[discInfoSize];
        file.ReadAt(hdSectorSize + (long)slot * discInfoSize, discInfo);

        DiscHeader disc = DiscHeader.Parse(discInfo.AsSpan(0, WbfsFormat.DiscHeaderCopySize));
        if (disc.Kind != DiscKind.Wii)
            throw new InvalidDataException("The WBFS file does not contain a Wii disc.");

        ushort[] blockMap = new ushort[blocksPerDisc];
        int stored = 0, highest = 0, last = -1;
        for (int i = 0; i < blocksPerDisc; i++)
        {
            ushort value = BinaryPrimitives.ReadUInt16BigEndian(discInfo.AsSpan(WbfsFormat.DiscHeaderCopySize + i * 2));
            blockMap[i] = value;
            if (value == 0)
                continue;

            stored++;
            highest = Math.Max(highest, value);
            last = i;
        }

        WbfsFileInfo info = new()
        {
            Path = wbfsPath,
            Parts = file.Paths,
            TotalSize = file.Length,
            Disc = disc,
            HdSectorCount = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4)),
            HdSectorShift = hdSectorShift,
            BlockShift = blockShift,
            StoredBlocks = stored,
            HighestStoredBlock = highest,
            LastDiscBlock = last,
        };
        return (info, blockMap);
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
