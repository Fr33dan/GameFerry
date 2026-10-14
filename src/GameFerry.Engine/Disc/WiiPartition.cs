using System.Buffers.Binary;
using GameFerry.Engine.Crypto;

namespace GameFerry.Engine.Disc;

/// <summary>Decrypting reader for one partition of a Wii disc.</summary>
public sealed class WiiPartition : IDisposable
{
    private const int HeaderSize = 0x2C0;
    private const int TitleKeyOffset = 0x1BF;
    private const int TitleIdOffset = 0x1DC;
    private const int CommonKeyIndexOffset = 0x1F1;
    private const int DataOffsetField = 0x2B8;
    private const int DataSizeField = 0x2BC;
    private const int ClusterIvOffset = 0x3D0;

    private readonly IDiscReader _disc;
    private readonly AesCbc _aes;
    private readonly byte[] _encrypted = new byte[WiiConstants.SectorSize];
    private readonly byte[] _decrypted = new byte[WiiConstants.ClusterDataSize];
    private long _loadedCluster = -1;

    private WiiPartition(IDiscReader disc, WiiPartitionEntry entry, long dataOffset, long dataSize, byte[] titleKey)
    {
        _disc = disc;
        Entry = entry;
        DataOffset = dataOffset;
        DataSize = dataSize;
        _aes = AesCbc.Create(titleKey);
    }

    public WiiPartitionEntry Entry { get; }

    /// <summary>Offset of the encrypted data area, relative to the start of the partition.</summary>
    public long DataOffset { get; }

    /// <summary>Size of the encrypted data area in bytes.</summary>
    public long DataSize { get; }

    /// <summary>Disc offset of the first cluster.</summary>
    public long DataStart => Entry.Offset + DataOffset;

    public long ClusterCount => DataSize / WiiConstants.SectorSize;

    /// <summary>Reads the data-area location straight from a partition header, without decrypting anything.</summary>
    public static (long DataOffset, long DataSize) ReadDataArea(IDiscReader disc, WiiPartitionEntry entry)
    {
        Span<byte> fields = stackalloc byte[8];
        disc.ReadAt(entry.Offset + DataOffsetField, fields);
        return ((long)BinaryPrimitives.ReadUInt32BigEndian(fields) << 2,
                (long)BinaryPrimitives.ReadUInt32BigEndian(fields[4..]) << 2);
    }

    /// <summary>True when the partition's header (ticket and data-area fields) is entirely zero.</summary>
    public static bool IsBlank(IDiscReader disc, WiiPartitionEntry entry)
    {
        byte[] header = new byte[HeaderSize];
        disc.ReadAt(entry.Offset, header);
        return !header.AsSpan().ContainsAnyExcept((byte)0);
    }

    public static WiiPartition Open(IDiscReader disc, WiiPartitionEntry entry)
    {
        byte[] header = new byte[HeaderSize];
        disc.ReadAt(entry.Offset, header);

        long dataOffset = (long)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(DataOffsetField)) << 2;
        long dataSize = (long)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(DataSizeField)) << 2;
        if (dataOffset < HeaderSize || dataOffset % WiiConstants.SectorSize != 0 || dataSize < WiiConstants.SectorSize)
            throw new InvalidDataException($"partition header is not valid (data offset 0x{dataOffset:X}, size 0x{dataSize:X})");

        return new WiiPartition(disc, entry, dataOffset, dataSize, DecryptTitleKey(header));
    }

    /// <summary>The partition's title key, from the ticket at the start of its header.</summary>
    public static byte[] DecryptTitleKey(ReadOnlySpan<byte> header)
    {
        int keyIndex = header[CommonKeyIndexOffset];
        if (keyIndex > WiiKeys.KoreanIndex)
            throw new InvalidDataException($"unknown common key index {keyIndex}");

        // The title key is encrypted with the common key, using the title ID as the IV.
        Span<byte> iv = stackalloc byte[AesCbc.BlockSize];
        header.Slice(TitleIdOffset, 8).CopyTo(iv);
        byte[] titleKey = new byte[AesCbc.KeySize];
        using (AesCbc aes = AesCbc.Create(WiiKeys.GetCommonKey(keyIndex)))
            aes.Decrypt(header.Slice(TitleKeyOffset, AesCbc.KeySize), iv, titleKey);
        return titleKey;
    }

    /// <summary>Reads decrypted partition data; <paramref name="offset"/> is in the partition's own data address space.</summary>
    public void ReadData(long offset, Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            long cluster = Math.DivRem(offset, WiiConstants.ClusterDataSize, out long within);
            LoadCluster(cluster);

            int count = (int)Math.Min(buffer.Length, WiiConstants.ClusterDataSize - within);
            _decrypted.AsSpan((int)within, count).CopyTo(buffer);
            buffer = buffer[count..];
            offset += count;
        }
    }

    private void LoadCluster(long cluster)
    {
        if (cluster == _loadedCluster)
            return;
        if (cluster < 0 || cluster >= ClusterCount)
            throw new InvalidDataException($"read beyond the partition's data area (cluster {cluster} of {ClusterCount})");

        _disc.ReadAt(DataStart + cluster * WiiConstants.SectorSize, _encrypted);

        // Each cluster's payload is encrypted separately; its IV sits inside the (encrypted) hash header.
        _aes.Decrypt(_encrypted.AsSpan(WiiConstants.ClusterHashSize), _encrypted.AsSpan(ClusterIvOffset, AesCbc.BlockSize), _decrypted);
        _loadedCluster = cluster;
    }

    public void Dispose() => _aes.Dispose();
}
