using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GameFerry.Engine.Crypto;
using GameFerry.Engine.Disc;

namespace GameFerry.Tests.Support;

internal sealed record TestFile(string Name, long Offset, int Size);

/// <summary>
/// Builds a small but structurally real Wii disc: header, multi-group partition table, and partitions
/// whose clusters are encrypted with a title key wrapped by the common key.
/// </summary>
internal sealed class SyntheticDisc
{
    public const long UpdateOffset = 0x50000;
    public const long GameOffset = 0xF800000;
    public const long ChannelOffset = 0x20000000;
    public const uint ChannelType = 0x48414445; // "HADE"

    private const long PartitionDataOffset = 0x20000;
    private const int ApploaderOffset = 0x2440;
    private const int ApploaderSize = 0x20 + 0x1000 + 0x200;
    private const int DolOffset = 0x4000;
    private const int DolSize = 0x2100;
    private const int FstOffset = 0x8000;

    private readonly Dictionary<long, HashSet<long>> _partitionSectors = [];

    public SparseDisc Disc { get; } = new();

    /// <summary>Sectors of the unencrypted header area.</summary>
    public HashSet<long> HeaderSectors { get; } = [.. Enumerable.Range(0, 10).Select(i => (long)i)];

    /// <summary>Sectors that hold real content for the partition at the given offset.</summary>
    public IReadOnlySet<long> SectorsOf(long partitionOffset) => _partitionSectors[partitionOffset];

    /// <summary>Update + game partitions in group 0 and a channel partition in group 1.</summary>
    public static SyntheticDisc CreateStandard()
    {
        SyntheticDisc disc = new();
        disc.WriteHeader("TESTE1", "Synthetic Test Disc");
        disc.WritePartitionTable((0, UpdateOffset, 1), (0, GameOffset, 0), (1, ChannelOffset, ChannelType));
        disc.AddPartition(UpdateOffset, clusterCount: 24, new TestFile("sys.wad", 0x20000, 0x9000));
        disc.AddPartition(
            GameOffset,
            clusterCount: 5000,
            new TestFile("a.bin", 0x100000, WiiConstants.ClusterDataSize * 3 + 5),
            new TestFile("empty.bin", 0x200000, 0),
            new TestFile("far.bin", 0x8000000, 100));
        disc.AddPartition(ChannelOffset, clusterCount: 16, new TestFile("vc.bin", 0x10000, 0x4000));
        return disc;
    }

    public void WriteHeader(string id, string title)
    {
        byte[] header = new byte[0x100];
        Encoding.ASCII.GetBytes(id).CopyTo(header, 0);
        header[7] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x18), WiiConstants.WiiMagic);
        Encoding.ASCII.GetBytes(title).CopyTo(header, 0x20);
        Disc.Write(0, header);

        Disc.Write(0x4E000, [0, 0, 0, 1]);
        Disc.Write(0x4FFFC, [0xC3, 0xF8, 0x1A, 0x8E]);
    }

    public void WritePartitionTable(params (int Group, long Offset, uint Type)[] partitions)
    {
        byte[] table = new byte[0x100];
        foreach (var group in partitions.GroupBy(p => p.Group))
        {
            int entriesAt = 0x20 + group.Key * 0x20;
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(group.Key * 8), (uint)group.Count());
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(group.Key * 8 + 4), (uint)((WiiConstants.PartitionTableOffset + entriesAt) >> 2));

            int index = 0;
            foreach (var partition in group)
            {
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(entriesAt + index * 8), (uint)(partition.Offset >> 2));
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(entriesAt + index * 8 + 4), partition.Type);
                index++;
            }
        }

        Disc.Write(WiiConstants.PartitionTableOffset, table);
    }

    public void AddPartition(long partitionOffset, int clusterCount, params TestFile[] files)
    {
        byte[] titleKey = [.. Enumerable.Range(1, 16).Select(i => (byte)(i * 7 + partitionOffset % 251))];
        byte[] titleId = [0, 1, 0, 0, (byte)'T', (byte)'E', (byte)'S', (byte)'T'];

        WritePartitionHeader(partitionOffset, clusterCount, titleKey, titleId);

        HashSet<long> sectors = [];
        _partitionSectors[partitionOffset] = sectors;
        for (long sector = partitionOffset / WiiConstants.SectorSize; sector < (partitionOffset + PartitionDataOffset) / WiiConstants.SectorSize; sector++)
            sectors.Add(sector);

        // Decrypted contents, keyed by cluster.
        Dictionary<long, byte[]> clusters = [];
        void Place(long offset, ReadOnlySpan<byte> data)
        {
            long firstSector = (partitionOffset + PartitionDataOffset) / WiiConstants.SectorSize;
            for (long c = offset / WiiConstants.ClusterDataSize; c <= (offset + data.Length - 1) / WiiConstants.ClusterDataSize; c++)
                sectors.Add(firstSector + c);

            while (!data.IsEmpty)
            {
                long cluster = Math.DivRem(offset, WiiConstants.ClusterDataSize, out long within);
                if (!clusters.TryGetValue(cluster, out byte[]? bytes))
                    clusters[cluster] = bytes = new byte[WiiConstants.ClusterDataSize];

                int count = (int)Math.Min(data.Length, WiiConstants.ClusterDataSize - within);
                data[..count].CopyTo(bytes.AsSpan((int)within));
                data = data[count..];
                offset += count;
            }
        }

        byte[] fst = BuildFst(files);

        byte[] boot = new byte[ApploaderOffset];
        BinaryPrimitives.WriteUInt32BigEndian(boot.AsSpan(0x18), WiiConstants.WiiMagic);
        BinaryPrimitives.WriteUInt32BigEndian(boot.AsSpan(0x420), DolOffset >> 2);
        BinaryPrimitives.WriteUInt32BigEndian(boot.AsSpan(0x424), FstOffset >> 2);
        BinaryPrimitives.WriteUInt32BigEndian(boot.AsSpan(0x428), (uint)(fst.Length >> 2));
        Place(0, boot);

        byte[] apploader = Filler(ApploaderSize, seed: 1);
        BinaryPrimitives.WriteUInt32BigEndian(apploader.AsSpan(0x14), 0x1000);
        BinaryPrimitives.WriteUInt32BigEndian(apploader.AsSpan(0x18), 0x200);
        Place(ApploaderOffset, apploader);

        byte[] dol = Filler(DolSize, seed: 2);
        dol.AsSpan(0, 0x100).Clear();
        BinaryPrimitives.WriteUInt32BigEndian(dol.AsSpan(0x00), 0x100);
        BinaryPrimitives.WriteUInt32BigEndian(dol.AsSpan(0x90), DolSize - 0x100);
        Place(DolOffset, dol);

        Place(FstOffset, fst);

        int fileSeed = 10;
        foreach (TestFile file in files.Where(f => f.Size > 0))
            Place(file.Offset, Filler(file.Size, fileSeed++));

        using Aes aes = Aes.Create();
        aes.Key = titleKey;
        foreach ((long cluster, byte[] data) in clusters)
        {
            byte[] encrypted = new byte[WiiConstants.SectorSize];
            Filler(WiiConstants.ClusterHashSize, seed: (int)cluster + 1000).CopyTo(encrypted, 0);
            aes.EncryptCbc(data, encrypted.AsSpan(0x3D0, 16), encrypted.AsSpan(WiiConstants.ClusterHashSize), PaddingMode.None);
            Disc.Write(partitionOffset + PartitionDataOffset + cluster * WiiConstants.SectorSize, encrypted);
        }
    }

    /// <summary>Writes a partition header whose data area holds nothing decryptable.</summary>
    public void AddGarbagePartition(long partitionOffset, int clusterCount)
    {
        WritePartitionHeader(partitionOffset, clusterCount, new byte[16], new byte[8]);
        Disc.Write(partitionOffset + PartitionDataOffset, Filler(WiiConstants.SectorSize, seed: 99));
    }

    private void WritePartitionHeader(long partitionOffset, int clusterCount, byte[] titleKey, byte[] titleId)
    {
        byte[] iv = new byte[16];
        titleId.CopyTo(iv, 0);

        byte[] header = new byte[0x2C0];
        using (Aes aes = Aes.Create())
        {
            aes.Key = WiiKeys.GetCommonKey(WiiKeys.StandardIndex);
            aes.EncryptCbc(titleKey, iv, header.AsSpan(0x1BF, 16), PaddingMode.None);
        }

        titleId.CopyTo(header, 0x1DC);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x2B4), 0x8000 >> 2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x2B8), (uint)(PartitionDataOffset >> 2));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x2BC), (uint)(((long)clusterCount * WiiConstants.SectorSize) >> 2));
        Disc.Write(partitionOffset, header);
    }

    private static byte[] BuildFst(TestFile[] files)
    {
        int entryCount = files.Length + 1;
        MemoryStream names = new();
        byte[] entries = new byte[entryCount * 12];

        entries[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(entries.AsSpan(8), (uint)entryCount);
        names.WriteByte(0);

        for (int i = 0; i < files.Length; i++)
        {
            Span<byte> entry = entries.AsSpan((i + 1) * 12, 12);
            BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)names.Position);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)(files[i].Offset >> 2));
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)files[i].Size);

            names.Write(Encoding.ASCII.GetBytes(files[i].Name));
            names.WriteByte(0);
        }

        byte[] fst = new byte[(entries.Length + (int)names.Length + 3) & ~3];
        entries.CopyTo(fst, 0);
        names.ToArray().CopyTo(fst, entries.Length);
        return fst;
    }

    private static byte[] Filler(int size, int seed)
    {
        byte[] bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
