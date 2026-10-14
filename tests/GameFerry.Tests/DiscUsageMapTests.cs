using GameFerry.Engine.Disc;
using GameFerry.Tests.Support;

namespace GameFerry.Tests;

public class DiscUsageMapTests
{
    private static HashSet<long> UsedSectors(DiscUsageMap map)
    {
        HashSet<long> used = [];
        ReadOnlySpan<bool> sectors = map.SectorUsed;
        for (int i = 0; i < sectors.Length; i++)
        {
            if (sectors[i])
                used.Add(i);
        }

        return used;
    }

    [Fact]
    public void All_marks_exactly_the_header_and_every_partitions_contents()
    {
        SyntheticDisc disc = SyntheticDisc.CreateStandard();

        DiscUsageMap map = DiscUsageMap.Build(disc.Disc, PartitionSelection.All);

        HashSet<long> expected =
        [
            .. disc.HeaderSectors,
            .. disc.SectorsOf(SyntheticDisc.UpdateOffset),
            .. disc.SectorsOf(SyntheticDisc.GameOffset),
            .. disc.SectorsOf(SyntheticDisc.ChannelOffset),
        ];
        Assert.Empty(map.Warnings);
        Assert.Equal(expected.Order(), UsedSectors(map).Order());
    }

    [Fact]
    public void Reads_partitions_from_every_table_group()
    {
        SyntheticDisc disc = SyntheticDisc.CreateStandard();

        List<WiiPartitionEntry> partitions = WiiPartitionTable.Read(disc.Disc);

        Assert.Equal(
            [
                new WiiPartitionEntry(0, SyntheticDisc.UpdateOffset, 1),
                new WiiPartitionEntry(0, SyntheticDisc.GameOffset, 0),
                new WiiPartitionEntry(1, SyntheticDisc.ChannelOffset, SyntheticDisc.ChannelType),
            ],
            partitions);
        Assert.Equal("HADE", partitions[2].TypeName);
    }

    [Fact]
    public void RemoveUpdate_drops_only_the_update_partition()
    {
        SyntheticDisc disc = SyntheticDisc.CreateStandard();

        DiscUsageMap map = DiscUsageMap.Build(disc.Disc, PartitionSelection.RemoveUpdate);

        HashSet<long> expected =
        [
            .. disc.HeaderSectors,
            .. disc.SectorsOf(SyntheticDisc.GameOffset),
            .. disc.SectorsOf(SyntheticDisc.ChannelOffset),
        ];
        Assert.Equal(expected.Order(), UsedSectors(map).Order());
        Assert.Equal(2, map.KeptPartitions.Count);
    }

    [Fact]
    public void GameOnly_keeps_just_the_data_partition()
    {
        SyntheticDisc disc = SyntheticDisc.CreateStandard();

        DiscUsageMap map = DiscUsageMap.Build(disc.Disc, PartitionSelection.GameOnly);

        HashSet<long> expected = [.. disc.HeaderSectors, .. disc.SectorsOf(SyntheticDisc.GameOffset)];
        Assert.Equal(expected.Order(), UsedSectors(map).Order());
    }

    [Fact]
    public void A_partition_that_cannot_be_parsed_is_kept_whole_with_a_warning()
    {
        const long garbageOffset = 0x30000000;
        const int garbageClusters = 40;
        SyntheticDisc disc = new();
        disc.WriteHeader("TESTE1", "Garbage");
        disc.WritePartitionTable((0, SyntheticDisc.GameOffset, 0), (0, garbageOffset, 2));
        disc.AddPartition(SyntheticDisc.GameOffset, clusterCount: 64, new TestFile("a.bin", 0x20000, 16));
        disc.AddGarbagePartition(garbageOffset, garbageClusters);

        DiscUsageMap map = DiscUsageMap.Build(disc.Disc, PartitionSelection.All);

        long firstSector = garbageOffset / WiiConstants.SectorSize;
        IEnumerable<long> wholePartition = Enumerable.Range(0, 4 + garbageClusters).Select(i => firstSector + i);
        HashSet<long> expected = [.. disc.HeaderSectors, .. disc.SectorsOf(SyntheticDisc.GameOffset), .. wholePartition];
        Assert.Equal(expected.Order(), UsedSectors(map).Order());
        Assert.Single(map.Warnings);
    }

    [Fact]
    public void A_blank_partition_is_left_out_instead_of_being_kept_as_padding()
    {
        // The table lists an update partition, but nothing was ever written where it should be.
        SyntheticDisc disc = new();
        disc.WriteHeader("TESTE1", "Blank Update");
        disc.WritePartitionTable((0, SyntheticDisc.UpdateOffset, 1), (0, SyntheticDisc.GameOffset, 0));
        disc.AddPartition(SyntheticDisc.GameOffset, clusterCount: 64, new TestFile("a.bin", 0x20000, 16));

        DiscUsageMap map = DiscUsageMap.Build(disc.Disc, PartitionSelection.All);

        HashSet<long> expected = [.. disc.HeaderSectors, .. disc.SectorsOf(SyntheticDisc.GameOffset)];
        Assert.Equal(expected.Order(), UsedSectors(map).Order());
        Assert.Equal([new WiiPartitionEntry(0, SyntheticDisc.GameOffset, 0)], map.KeptPartitions);
        Assert.Single(map.Warnings);
    }

    [Fact]
    public void Rejects_images_that_are_not_wii_discs()
    {
        SparseDisc disc = new();
        disc.Write(0, "GALE01"u8);

        Assert.Throws<InvalidDataException>(() => DiscUsageMap.Build(disc, PartitionSelection.All));
    }

    [Fact]
    public void Rewrite_moves_kept_entries_to_the_front_of_their_group()
    {
        SyntheticDisc disc = SyntheticDisc.CreateStandard();
        byte[] start = new byte[WiiConstants.HeaderAreaSize];
        disc.Disc.ReadAt(0, start);
        byte[] gameEntry = start[0x40028..0x40030];

        WiiPartitionTable.Rewrite(start, partition => !partition.IsUpdate);

        SparseDisc rewritten = new();
        rewritten.Write(0, start);
        Assert.Equal(
            [
                new WiiPartitionEntry(0, SyntheticDisc.GameOffset, 0),
                new WiiPartitionEntry(1, SyntheticDisc.ChannelOffset, SyntheticDisc.ChannelType),
            ],
            WiiPartitionTable.Read(rewritten));

        // Like libwbfs, the slot beyond the new count is left as it was, which keeps output identical to WBM's.
        Assert.Equal(gameEntry, start[0x40020..0x40028]);
        Assert.Equal(gameEntry, start[0x40028..0x40030]);
    }
}
