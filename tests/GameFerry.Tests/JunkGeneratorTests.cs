using GameFerry.Engine.NKit;

namespace GameFerry.Tests;

public class JunkGeneratorTests
{
    private const long SingleLayer = 0x118240000;

    [Fact]
    public void Matches_the_junk_NKit_makes()
    {
        // Expected bytes from NKit 1.4's own junk generator, for a made-up game: near the end of a single-layer
        // disc, from an odd offset as junk after a partition often starts, and early on the game's second disc.
        JunkGenerator junk = new("TSTE"u8, 0, SingleLayer);

        byte[] start = new byte[36];
        junk.Read(0x1173C001C, start);
        byte[] middle = new byte[32];
        junk.Read(0x117B00000, middle);
        byte[] secondDisc = new byte[32];
        new JunkGenerator("TSTE"u8, 1, SingleLayer).Read(0x51234, secondDisc);

        Assert.Equal("2432F99C117F71E4128676470C3EC6660BDFCF6573CBB2DD928AB1EAE7C5833C2A80A846", Convert.ToHexString(start));
        Assert.Equal("4D5F278B356D7F81BD446328B56BB2635680074FCD76787CA28F96854FF008C1", Convert.ToHexString(middle));
        Assert.Equal("33FB5B084FE994BF6835C58959692EE117CB997F382AE050EA8D260A13F3A826", Convert.ToHexString(secondDisc));
    }

    [Fact]
    public void Pieces_read_anywhere_give_the_same_bytes()
    {
        JunkGenerator whole = new("TSTE"u8, 1, SingleLayer);
        JunkGenerator pieces = new("TSTE"u8, 1, SingleLayer);
        long start = JunkGenerator.BlockSize - 0x1235;
        byte[] expected = new byte[0x50000];
        whole.Read(start, expected);

        byte[] actual = new byte[expected.Length];
        int[] sizes = [1, 3, 0x1000, 0x233, 0x40001, 7];
        for (int offset = 0, i = 0; offset < actual.Length; i++)
        {
            int count = Math.Min(sizes[i % sizes.Length], actual.Length - offset);
            pieces.Read(start + offset, actual.AsSpan(offset, count));
            offset += count;
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Different_discs_and_games_give_different_junk()
    {
        byte[] first = new byte[64];
        byte[] secondDisc = new byte[64];
        byte[] otherGame = new byte[64];
        new JunkGenerator("TSTE"u8, 0, SingleLayer).Read(0x100000, first);
        new JunkGenerator("TSTE"u8, 1, SingleLayer).Read(0x100000, secondDisc);
        new JunkGenerator("TSTP"u8, 0, SingleLayer).Read(0x100000, otherGame);

        Assert.NotEqual(first, secondDisc);
        Assert.NotEqual(first, otherGame);
    }

    [Fact]
    public void Past_its_length_rounded_down_to_a_sector_it_is_zeros()
    {
        JunkGenerator junk = new("TSTE"u8, 0, 0x50000 + 0x1234);
        byte[] bytes = new byte[0x100];

        junk.Read(0x50000 - 0x80, bytes);

        Assert.True(bytes.AsSpan(0, 0x80).ContainsAnyExcept((byte)0));
        Assert.False(bytes.AsSpan(0x80).ContainsAnyExcept((byte)0));
    }
}
