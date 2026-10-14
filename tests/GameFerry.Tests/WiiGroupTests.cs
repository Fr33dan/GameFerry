using System.Security.Cryptography;
using GameFerry.Engine.NKit;

namespace GameFerry.Tests;

public class WiiGroupTests
{
    private static readonly byte[] Key = [.. Enumerable.Range(0, 16).Select(i => (byte)(i * 13 + 5))];

    private static void FillData(WiiGroup group, int sectors, int seed)
    {
        Random random = new(seed);
        for (int s = 0; s < sectors; s++)
            random.NextBytes(group.Decrypted.AsSpan(s * WiiGroup.SectorSize + WiiGroup.HashSize, WiiGroup.SectorDataSize));
    }

    /// <summary>The Wii hash tree worked out longhand: H0 per 1 KiB, H1 per sector's H0 table, H2 per eight H1 tables.</summary>
    private static (byte[][] H0, byte[][] H1, byte[] H2) ExpectedHashes(byte[] decrypted, int usedSectors)
    {
        byte[][] h0 = new byte[64][];
        for (int s = 0; s < 64; s++)
        {
            h0[s] = new byte[31 * 20];
            for (int i = 0; i < 31; i++)
            {
                byte[] chunk = s < usedSectors ? decrypted.AsSpan(s * 0x8000 + 0x400 + i * 0x400, 0x400).ToArray() : new byte[0x400];
                SHA1.HashData(chunk).CopyTo(h0[s], i * 20);
            }
        }

        byte[][] h1 = new byte[8][];
        byte[] h2 = new byte[8 * 20];
        for (int j = 0; j < 8; j++)
        {
            h1[j] = new byte[8 * 20];
            for (int i = 0; i < 8; i++)
                SHA1.HashData(h0[j * 8 + i]).CopyTo(h1[j], i * 20);
            SHA1.HashData(h1[j]).CopyTo(h2, j * 20);
        }

        return (h0, h1, h2);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(10)]
    public void Rebuilt_hashes_follow_the_Wii_hash_tree(int usedSectors)
    {
        byte[] h3 = new byte[0x18000];
        using WiiGroup group = new(Key, h3);
        FillData(group, usedSectors, seed: usedSectors);
        group.Begin(3, usedSectors);
        (byte[][] h0, byte[][] h1, byte[] h2) = ExpectedHashes(group.Decrypted, usedSectors);
        SHA1.HashData(h2).CopyTo(h3, 3 * 20);

        group.RecomputeHashes();

        Assert.True(group.MatchesH3);
        for (int s = 0; s < usedSectors; s++)
        {
            ReadOnlySpan<byte> hashBlock = group.Decrypted.AsSpan(s * WiiGroup.SectorSize, WiiGroup.HashSize);
            Assert.Equal(h0[s], hashBlock[..0x26C].ToArray());
            Assert.Equal(h1[s / 8], hashBlock.Slice(0x280, 0xA0).ToArray());
            Assert.Equal(h2, hashBlock.Slice(0x340, 0xA0).ToArray());
            Assert.False(hashBlock[0x26C..0x280].ContainsAnyExcept((byte)0));
            Assert.False(hashBlock[0x320..0x340].ContainsAnyExcept((byte)0));
            Assert.False(hashBlock[0x3E0..].ContainsAnyExcept((byte)0));
        }
    }

    [Fact]
    public void A_group_that_does_not_match_its_hash_table_says_so()
    {
        using WiiGroup group = new(Key, new byte[0x18000]);
        FillData(group, 64, seed: 1);
        group.Begin(0, 64);

        group.RecomputeHashes();

        Assert.False(group.MatchesH3);
    }

    [Fact]
    public void Encrypted_sectors_decrypt_back_as_a_disc_reader_would()
    {
        using WiiGroup group = new(Key, new byte[0x18000]);
        FillData(group, 64, seed: 2);
        group.Begin(0, 64);
        group.RecomputeHashes();
        group.Encrypt();

        using Aes aes = Aes.Create();
        aes.Key = Key;
        for (int s = 0; s < 64; s += 9)
        {
            int offset = s * WiiGroup.SectorSize;
            byte[] hashBlock = aes.DecryptCbc(group.Encrypted.AsSpan(offset, 0x400), new byte[16], PaddingMode.None);
            byte[] data = aes.DecryptCbc(group.Encrypted.AsSpan(offset + 0x400, 0x7C00), group.Encrypted.AsSpan(offset + 0x3D0, 16), PaddingMode.None);

            Assert.Equal(group.Decrypted.AsSpan(offset, 0x400).ToArray(), hashBlock);
            Assert.Equal(group.Decrypted.AsSpan(offset + 0x400, 0x7C00).ToArray(), data);
        }
    }

    [Fact]
    public void Loading_encrypted_sectors_gives_back_the_decrypted_group()
    {
        using WiiGroup first = new(Key, new byte[0x18000]);
        FillData(first, 20, seed: 3);
        first.Begin(5, 20);
        first.RecomputeHashes();
        first.Encrypt();

        using WiiGroup second = new(Key, new byte[0x18000]);
        second.Load(5, first.Encrypted.AsSpan(0, first.UsedSize));

        Assert.Equal(20, second.UsedSectors);
        Assert.Equal(first.Decrypted.AsSpan(0, first.UsedSize).ToArray(), second.Decrypted.AsSpan(0, second.UsedSize).ToArray());
    }
}
