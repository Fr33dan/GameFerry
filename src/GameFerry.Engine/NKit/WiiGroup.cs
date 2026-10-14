using System.Security.Cryptography;
using GameFerry.Engine.Crypto;

namespace GameFerry.Engine.NKit;

/// <summary>
/// One 2 MiB group of a Wii partition as NKit 1.4 rebuilds it: 64 sectors of 0x8000 bytes, each a 0x400 hash block
/// (H0 at 0x000, H1 at 0x280, H2 at 0x340) followed by 0x7C00 of data, then AES-128-CBC with the title key.
/// <see cref="Decrypted"/> is kept from one group to the next, as NKit's buffer is, because the rare groups whose
/// hashes are not recalculated encrypt whatever their hash blocks already hold.
/// </summary>
internal sealed class WiiGroup : IDisposable
{
    public const int Sectors = 64;
    public const int SectorSize = 0x8000;
    public const int HashSize = 0x400;
    public const int SectorDataSize = SectorSize - HashSize;
    public const int Size = Sectors * SectorSize;
    public const int DataSize = Sectors * SectorDataSize;

    private const int H0Size = 31 * 20;
    private const int H1Offset = 0x280;
    private const int H2Offset = 0x340;
    private const int TableSize = 8 * 20;
    private const int IvOffset = 0x3D0;

    private static readonly byte[] BlankHash = SHA1.HashData(new byte[0x400]);

    private readonly AesCbc _aes;
    private readonly byte[] _h3Table;
    private readonly byte[] _h0 = new byte[Sectors * H0Size];
    private readonly byte[] _h1 = new byte[8 * TableSize];
    private readonly byte[] _h2 = new byte[TableSize];
    private readonly bool[] _scrubbed = new bool[Sectors];
    private readonly byte[] _scrubByte = new byte[Sectors];

    public WiiGroup(byte[] titleKey, byte[] h3Table)
    {
        _aes = AesCbc.Create(titleKey);
        _h3Table = h3Table;
    }

    public byte[] Decrypted { get; } = new byte[Size];

    public byte[] Encrypted { get; } = new byte[Size];

    public int Index { get; private set; }

    public int UsedSectors { get; private set; }

    public int UsedSize => UsedSectors * SectorSize;

    /// <summary>After <see cref="RecomputeHashes"/>: whether the group's H2 table hashes to its entry in the H3 table.</summary>
    public bool MatchesH3 { get; private set; }

    /// <summary>Starts a group whose data has been (or is being) placed in <see cref="Decrypted"/>.</summary>
    public void Begin(int index, int usedSectors)
    {
        Index = index;
        UsedSectors = usedSectors;
        Array.Clear(_scrubbed);
        Array.Clear(_scrubByte);
    }

    /// <summary>
    /// Starts a group from encrypted sectors, as NKit does when it goes back to patch one: decrypt (hash blocks with a
    /// zero IV) and note sectors whose encrypted hash block is a single repeated 00 or FF as scrubbed.
    /// </summary>
    public void Load(int index, ReadOnlySpan<byte> encrypted)
    {
        Begin(index, encrypted.Length / SectorSize);
        encrypted.CopyTo(Encrypted);
        Encrypted.AsSpan(encrypted.Length).Clear();

        Span<byte> zeroIv = stackalloc byte[16];
        for (int s = 0; s < UsedSectors; s++)
        {
            int offset = s * SectorSize;
            _aes.Decrypt(Encrypted.AsSpan(offset, HashSize), zeroIv, Decrypted.AsSpan(offset, HashSize));
            _aes.Decrypt(Encrypted.AsSpan(offset + HashSize, SectorDataSize), Encrypted.AsSpan(offset + IvOffset, 16), Decrypted.AsSpan(offset + HashSize, SectorDataSize));

            ReadOnlySpan<byte> hashBlock = Encrypted.AsSpan(offset, HashSize);
            byte first = hashBlock[0];
            if (first is 0x00 or 0xFF)
            {
                _scrubbed[s] = !hashBlock.ContainsAnyExcept(first);
                _scrubByte[s] = first;
            }
        }
    }

    public void MarkScrubbed(int sector, byte value)
    {
        _scrubbed[sector] = sector < UsedSectors;
        _scrubByte[sector] = value;
    }

    /// <summary>Recalculates H0 to H2 over the data and writes them into every sector's hash block.</summary>
    public void RecomputeHashes()
    {
        byte[] data = Decrypted;
        for (int s = 0; s < Sectors; s++)
        {
            Span<byte> h0 = _h0.AsSpan(s * H0Size, H0Size);
            for (int i = 0; i < 31; i++)
            {
                if (s < UsedSectors)
                    SHA1.HashData(data.AsSpan(s * SectorSize + HashSize + i * 0x400, 0x400), h0.Slice(i * 20, 20));
                else
                    BlankHash.CopyTo(h0.Slice(i * 20, 20));
            }
        }

        for (int j = 0; j < 8; j++)
        {
            for (int i = 0; i < 8; i++)
                SHA1.HashData(_h0.AsSpan((8 * j + i) * H0Size, H0Size), _h1.AsSpan(j * TableSize + i * 20, 20));
            SHA1.HashData(_h1.AsSpan(j * TableSize, TableSize), _h2.AsSpan(j * 20, 20));
        }

        Span<byte> h3 = stackalloc byte[20];
        SHA1.HashData(_h2, h3);
        MatchesH3 = (Index + 1) * 20 <= _h3Table.Length && h3.SequenceEqual(_h3Table.AsSpan(Index * 20, 20));

        for (int s = 0; s < Sectors; s++)
        {
            Span<byte> hashBlock = data.AsSpan(s * SectorSize, HashSize);
            _h0.AsSpan(s * H0Size, H0Size).CopyTo(hashBlock);
            hashBlock[H0Size..H1Offset].Clear();
            _h1.AsSpan(s / 8 * TableSize, TableSize).CopyTo(hashBlock[H1Offset..]);
            hashBlock[(H1Offset + TableSize)..H2Offset].Clear();
            _h2.CopyTo(hashBlock[H2Offset..]);
            hashBlock[(H2Offset + TableSize)..].Clear();
        }
    }

    /// <summary>For a group NKit found entirely scrubbed: each sector's first 0x400 bytes of data stand in for its hashes.</summary>
    public void CopyScrubbedHashes()
    {
        for (int s = 0; s < UsedSectors; s++)
            Decrypted.AsSpan(s * SectorSize + HashSize, HashSize).CopyTo(Decrypted.AsSpan(s * SectorSize, HashSize));
    }

    /// <summary>Copies every sector's hash block out, 0x400 bytes each, in order.</summary>
    public void CopyHashBlocks(Span<byte> destination)
    {
        for (int s = 0; s < Sectors; s++)
            Decrypted.AsSpan(s * SectorSize, HashSize).CopyTo(destination[(s * HashSize)..]);
    }

    /// <summary>Sets every sector's hash block from <paramref name="blocks"/>, 0x400 bytes each, in order.</summary>
    public void SetHashBlocks(ReadOnlySpan<byte> blocks)
    {
        for (int s = 0; s < Sectors; s++)
            blocks.Slice(s * HashSize, HashSize).CopyTo(Decrypted.AsSpan(s * SectorSize, HashSize));
    }

    /// <summary>Puts back hash blocks NKit stored because they could not be recreated (0x400 per sector, in order).</summary>
    public void ApplyStoredHashes(ReadOnlySpan<byte> hashes, int sectors)
    {
        for (int s = 0; s < sectors; s++)
            hashes.Slice(s * HashSize, HashSize).CopyTo(Decrypted.AsSpan(s * SectorSize, HashSize));
    }

    /// <summary>Encrypts the used sectors into <see cref="Encrypted"/>.</summary>
    /// <param name="storedHashes">True once stored hashes have been put back: their hash blocks always use a zero IV.</param>
    public void Encrypt(bool storedHashes = false)
    {
        Span<byte> iv = stackalloc byte[16];
        for (int s = 0; s < UsedSectors; s++)
        {
            int offset = s * SectorSize;

            // A sector scrubbed to FF must encrypt back to FF, which takes an IV of FFs.
            if (_scrubbed[s] && _scrubByte[s] != 0 && !storedHashes)
                iv.Fill(_scrubByte[s]);
            else
                iv.Clear();

            _aes.Encrypt(Decrypted.AsSpan(offset, HashSize), iv, Encrypted.AsSpan(offset, HashSize));
            _aes.Encrypt(Decrypted.AsSpan(offset + HashSize, SectorDataSize), Encrypted.AsSpan(offset + IvOffset, 16), Encrypted.AsSpan(offset + HashSize, SectorDataSize));
        }
    }

    public void Dispose() => _aes.Dispose();
}
