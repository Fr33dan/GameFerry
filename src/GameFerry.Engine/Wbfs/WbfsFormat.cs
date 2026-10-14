using GameFerry.Engine.Disc;

namespace GameFerry.Engine.Wbfs;

/// <summary>
/// Layout of a single-disc WBFS file, as written by Wii Backup Manager and wbfs_file:
/// <code>
/// 0x000  "WBFS", u32 hd-sector count, u8 hd-sector shift, u8 wbfs-sector shift, 2 pad bytes, disc table
/// 0x200  copy of the first 0x100 bytes of the disc
/// 0x300  block map: one big-endian u16 per disc block, 0 = not stored
/// then   the stored blocks, numbered from 1 (block 0 is this header)
/// </code>
/// </summary>
public static class WbfsFormat
{
    public const int HdSectorShift = 9;
    public const int HdSectorSize = 1 << HdSectorShift;

    /// <summary>Block size used for single-disc files: the smallest whose block numbers still fit in 16 bits.</summary>
    public const int BlockShift = 21;
    public const int BlockSize = 1 << BlockShift;
    public const int SectorsPerBlock = BlockSize / WiiConstants.SectorSize;

    /// <summary>Entries in the block map for <see cref="BlockSize"/> blocks.</summary>
    public const int BlocksPerDisc = WiiConstants.MaxDiscSectors / SectorsPerBlock;

    public const int DiscTableOffset = 12;
    public const int DiscHeaderCopySize = 0x100;

    /// <summary>The conventional split size: 4 GiB minus 32 KiB, safely under FAT32's file size limit.</summary>
    public const long DefaultSplitSize = 0xFFFF8000;

    public static int BlocksPerDiscFor(int blockShift) =>
        WiiConstants.MaxDiscSectors >> (blockShift - 15);

    /// <summary>File name of one part of a split set: <c>ID.wbfs</c>, <c>ID.wbf1</c>, <c>ID.wbf2</c>…</summary>
    public static string PartFileName(string baseName, int index) =>
        index == 0 ? baseName + ".wbfs" : $"{baseName}.wbf{index}";
}
