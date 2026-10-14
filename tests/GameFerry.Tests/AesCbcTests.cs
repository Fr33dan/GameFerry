using System.Security.Cryptography;
using GameFerry.Engine.Crypto;

namespace GameFerry.Tests;

public class AesCbcTests
{
    private static byte[] Bytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void Managed_AES_matches_the_FIPS_197_example()
    {
        using ManagedAesCbc aes = new(Convert.FromHexString("000102030405060708090A0B0C0D0E0F"));

        byte[] cipher = new byte[16];
        aes.Encrypt(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), new byte[16], cipher);
        Assert.Equal("69C4E0D86A7B0430D8CDB78070B4C55A", Convert.ToHexString(cipher));

        byte[] plain = new byte[16];
        aes.Decrypt(cipher, new byte[16], plain);
        Assert.Equal("00112233445566778899AABBCCDDEEFF", Convert.ToHexString(plain));
    }

    [Fact]
    public void Managed_AES_matches_the_NIST_CBC_example_both_ways()
    {
        // NIST SP 800-38A, F.2.1 and F.2.2.
        byte[] key = Convert.FromHexString("2B7E151628AED2A6ABF7158809CF4F3C");
        byte[] iv = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        const string plain = "6BC1BEE22E409F96E93D7E117393172AAE2D8A571E03AC9C9EB76FAC45AF8E51"
            + "30C81C46A35CE411E5FBC1191A0A52EFF69F2445DF4F9B17AD2B417BE66C3710";
        const string cipher = "7649ABAC8119B246CEE98E9B12E9197D5086CB9B507219EE95DB113A917678B2"
            + "73BED6B8E3C1743B7116E69E222295163FF1CAA1681FAC09120ECA307586E1A7";
        using ManagedAesCbc aes = new(key);

        byte[] encrypted = new byte[64];
        aes.Encrypt(Convert.FromHexString(plain), iv, encrypted);
        byte[] decrypted = new byte[64];
        aes.Decrypt(Convert.FromHexString(cipher), iv, decrypted);

        Assert.Equal(cipher, Convert.ToHexString(encrypted));
        Assert.Equal(plain, Convert.ToHexString(decrypted));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(0x7C00 / 16)]
    public void Managed_AES_agrees_with_the_platform_on_random_data(int blocks)
    {
        Random random = new(blocks);
        for (int round = 0; round < 20; round++)
        {
            byte[] key = Bytes(random, 16);
            byte[] iv = Bytes(random, 16);
            byte[] data = Bytes(random, blocks * 16);
            using ManagedAesCbc managed = new(key);
            using Aes platform = Aes.Create();
            platform.Key = key;

            byte[] encrypted = new byte[data.Length];
            managed.Encrypt(data, iv, encrypted);
            byte[] decrypted = new byte[data.Length];
            managed.Decrypt(encrypted, iv, decrypted);

            Assert.Equal(platform.EncryptCbc(data, iv, PaddingMode.None), encrypted);
            Assert.Equal(data, decrypted);
        }
    }

    [Fact]
    public void Managed_AES_can_work_in_place()
    {
        Random random = new(3);
        byte[] key = Bytes(random, 16);
        byte[] iv = Bytes(random, 16);
        byte[] data = Bytes(random, 0x400);
        byte[] buffer = (byte[])data.Clone();
        using ManagedAesCbc managed = new(key);
        using Aes platform = Aes.Create();
        platform.Key = key;

        managed.Encrypt(buffer, iv, buffer);
        Assert.Equal(platform.EncryptCbc(data, iv, PaddingMode.None), buffer);

        managed.Decrypt(buffer, iv, buffer);
        Assert.Equal(data, buffer);
    }

    [Fact]
    public void Off_the_browser_the_platform_AES_is_used()
    {
        using AesCbc aes = AesCbc.Create(new byte[16]);

        Assert.IsType<PlatformAesCbc>(aes);
    }

    [Fact]
    public void Partial_blocks_and_short_keys_are_refused()
    {
        using ManagedAesCbc aes = new(new byte[16]);

        Assert.Throws<ArgumentException>(() => aes.Encrypt(new byte[20], new byte[16], new byte[32]));
        Assert.Throws<ArgumentException>(() => aes.Decrypt(new byte[16], new byte[8], new byte[16]));
        Assert.Throws<ArgumentException>(() => AesCbc.Create(new byte[24]));
    }
}
