using System.Buffers.Binary;

namespace GameFerry.Engine.Disc;

/// <summary>A run of bytes in a partition's decrypted data address space.</summary>
public readonly record struct DataExtent(long Offset, long Length);

/// <summary>Reads decrypted partition data; <paramref name="offset"/> is in the partition's data address space.</summary>
public delegate void PartitionDataSource(long offset, Span<byte> buffer);

/// <summary>Finds which parts of a partition's data area actually hold something.</summary>
public static class WiiPartitionContents
{
    private const int BootBlockSize = 0x440;
    private const int ApploaderOffset = 0x2440;
    private const int DolSectionCount = 18;
    private const int FstEntrySize = 12;
    private const long MaxFstSize = 256L * 1024 * 1024;

    /// <summary>
    /// Returns the system areas (boot block, apploader, main DOL, file table) and every file listed in the file table.
    /// Everything else in the partition is padding.
    /// </summary>
    public static List<DataExtent> GetUsedExtents(WiiPartition partition) =>
        GetUsedExtents(partition.ClusterCount * WiiConstants.ClusterDataSize, partition.ReadData);

    /// <inheritdoc cref="GetUsedExtents(WiiPartition)"/>
    /// <param name="dataLength">Size of the partition's data area.</param>
    public static List<DataExtent> GetUsedExtents(long dataLength, PartitionDataSource read)
    {
        List<DataExtent> extents = GetSystemExtents(dataLength, read, out byte[] fst);
        extents.AddRange(GetFileExtents(fst));
        return extents;
    }

    /// <summary>The files the file table lists, as extents; folders and empty files are left out.</summary>
    public static IEnumerable<DataExtent> GetFileExtents(byte[] fst)
    {
        long entryCount = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(8));
        for (int i = 1; i < entryCount; i++)
        {
            ReadOnlySpan<byte> entry = fst.AsSpan(i * FstEntrySize, FstEntrySize);
            bool isDirectory = entry[0] != 0;
            if (isDirectory)
                continue;

            long offset = (long)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]) << 2;
            long size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            if (size > 0)
                yield return new DataExtent(offset, size);
        }
    }

    /// <summary>
    /// The boot block and bi2, the apploader, the main DOL and the file table, after checking that the boot block
    /// and file table make sense. <paramref name="fst"/> receives the file table.
    /// </summary>
    /// <exception cref="InvalidDataException">The partition's layout isn't one this can analyse.</exception>
    public static List<DataExtent> GetSystemExtents(long dataLength, PartitionDataSource read, out byte[] fst)
    {
        byte[] boot = new byte[BootBlockSize];
        read(0, boot);
        if (BinaryPrimitives.ReadUInt32BigEndian(boot.AsSpan(0x18)) != WiiConstants.WiiMagic)
            throw new InvalidDataException("decrypted boot block has no Wii magic");

        long dolOffset = (long)BinaryPrimitives.ReadUInt32BigEndian(boot.AsSpan(0x420)) << 2;
        long fstOffset = (long)BinaryPrimitives.ReadUInt32BigEndian(boot.AsSpan(0x424)) << 2;
        long fstSize = (long)BinaryPrimitives.ReadUInt32BigEndian(boot.AsSpan(0x428)) << 2;
        if (fstSize < FstEntrySize || fstSize > MaxFstSize || fstOffset + fstSize > dataLength || dolOffset + 0x100 > dataLength)
            throw new InvalidDataException($"boot block points outside the partition (DOL 0x{dolOffset:X}, FST 0x{fstOffset:X}+0x{fstSize:X})");

        // Boot block and bi2 occupy everything up to the apploader.
        List<DataExtent> extents = [new DataExtent(0, ApploaderOffset)];

        Span<byte> apploader = stackalloc byte[0x20];
        read(ApploaderOffset, apploader);
        long apploaderSize = 0x20L
            + BinaryPrimitives.ReadUInt32BigEndian(apploader[0x14..])
            + BinaryPrimitives.ReadUInt32BigEndian(apploader[0x18..]);
        extents.Add(new DataExtent(ApploaderOffset, apploaderSize));

        // DOL header: 18 section file offsets at 0x00 and their sizes at 0x90.
        Span<byte> dol = stackalloc byte[0x100];
        read(dolOffset, dol);
        long dolSize = 0x100;
        for (int i = 0; i < DolSectionCount; i++)
        {
            long end = (long)BinaryPrimitives.ReadUInt32BigEndian(dol[(i * 4)..])
                     + BinaryPrimitives.ReadUInt32BigEndian(dol[(0x90 + i * 4)..]);
            dolSize = Math.Max(dolSize, end);
        }

        extents.Add(new DataExtent(dolOffset, dolSize));
        extents.Add(new DataExtent(fstOffset, fstSize));

        fst = new byte[fstSize];
        read(fstOffset, fst);

        // The root entry's size field holds the total number of entries.
        long entryCount = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(8));
        if (entryCount < 1 || entryCount * FstEntrySize > fstSize)
            throw new InvalidDataException($"file table declares {entryCount} entries but is only 0x{fstSize:X} bytes");

        return extents;
    }
}
