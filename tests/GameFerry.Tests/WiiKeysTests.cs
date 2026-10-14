using GameFerry.Engine.Crypto;
using GameFerry.Engine.Disc;
using GameFerry.Tests.Support;

namespace GameFerry.Tests;

/// <summary>One test swaps the key source every partition read uses, so nothing else may run meanwhile.</summary>
[CollectionDefinition(nameof(WiiKeysCollection), DisableParallelization = true)]
public sealed class WiiKeysCollection;

[Collection(nameof(WiiKeysCollection))]
public class WiiKeysTests
{
    private static byte[] StandardKey => WiiKeys.GetCommonKey(WiiKeys.StandardIndex);

    private static byte[] KoreanKey => WiiKeys.GetCommonKey(WiiKeys.KoreanIndex);

    /// <summary>A BootMii keys.bin: a text header, then the console's OTP with the common key at 0x14.</summary>
    private static byte[] KeysBin(ReadOnlySpan<byte> commonKey)
    {
        byte[] file = new byte[KeysBinSource.Size];
        new Random(5).NextBytes(file.AsSpan(0x100));
        "BackupMii v1, ConsoleID: 0123abcd\n"u8.CopyTo(file);
        commonKey.CopyTo(file.AsSpan(0x114));
        return file;
    }

    [Fact]
    public void A_keys_bin_supplies_the_common_key_but_not_the_Korean_one()
    {
        KeysBinSource keys = KeysBinSource.Parse(KeysBin(StandardKey));

        Assert.Equal(StandardKey, keys.GetCommonKey(WiiKeys.StandardIndex));
        Assert.Null(keys.GetCommonKey(WiiKeys.KoreanIndex));
    }

    [Fact]
    public void A_keys_bin_of_the_wrong_size_or_holding_something_else_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => KeysBinSource.Parse(KeysBin(StandardKey).AsSpan(0, 0x200)));
        Assert.Throws<InvalidDataException>(() => KeysBinSource.Parse(KeysBin(new byte[16])));
        Assert.Throws<InvalidDataException>(() => KeysBinSource.Parse(KeysBin(KoreanKey)));
    }

    [Fact]
    public void A_typed_key_may_be_spaced_out_but_must_be_the_right_one()
    {
        string spaced = string.Join(' ', Convert.ToHexString(KoreanKey).Chunk(4).Select(chunk => new string(chunk)));

        HexKeySource typed = new(WiiKeys.KoreanIndex, spaced);

        Assert.Equal(KoreanKey, typed.GetCommonKey(WiiKeys.KoreanIndex));
        Assert.Null(typed.GetCommonKey(WiiKeys.StandardIndex));
        Assert.Throws<InvalidDataException>(() => new HexKeySource(WiiKeys.KoreanIndex, "not a key"));
        Assert.Throws<InvalidDataException>(() => new HexKeySource(WiiKeys.KoreanIndex, "00112233445566778899AABBCCDDEEFF"));
        Assert.Throws<InvalidDataException>(() => new HexKeySource(WiiKeys.KoreanIndex, Convert.ToHexString(StandardKey)));
    }

    [Fact]
    public void Combined_sources_answer_from_whichever_has_the_key()
    {
        CombinedWiiKeys keys = new(
            KeysBinSource.Parse(KeysBin(StandardKey)),
            new HexKeySource(WiiKeys.KoreanIndex, Convert.ToHexString(KoreanKey)));

        Assert.Equal(StandardKey, keys.GetCommonKey(WiiKeys.StandardIndex));
        Assert.Equal(KoreanKey, keys.GetCommonKey(WiiKeys.KoreanIndex));
        Assert.Null(keys.GetCommonKey(2));
    }

    [Fact]
    public void A_missing_key_stops_the_analysis_rather_than_keeping_the_partition_whole()
    {
        SyntheticDisc disc = SyntheticDisc.CreateStandard();
        IWiiKeySource saved = WiiKeys.Current;
        WiiKeys.Current = new CombinedWiiKeys();
        try
        {
            MissingWiiKeyException error = Assert.Throws<MissingWiiKeyException>(() => DiscUsageMap.Build(disc.Disc, PartitionSelection.All));

            Assert.Equal(WiiKeys.StandardIndex, error.Index);
            Assert.Contains("keys.bin", error.Message);
        }
        finally
        {
            WiiKeys.Current = saved;
        }
    }
}
