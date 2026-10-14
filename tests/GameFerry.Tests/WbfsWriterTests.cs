using System.Buffers.Binary;
using GameFerry.Core.Wbfs;
using GameFerry.Engine.Disc;
using GameFerry.Engine.Wbfs;
using GameFerry.Tests.Support;

namespace GameFerry.Tests;

public sealed class WbfsWriterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private (IReadOnlyList<string> Files, byte[] Hash, WbfsPlan Plan) WriteDisc(
        IDiscReader disc, string folder, PartitionSelection selection = PartitionSelection.All, long splitSize = 0)
    {
        string directory = _temp.Combine(folder);
        Directory.CreateDirectory(directory);

        WbfsPlan plan = WbfsWriter.Plan(disc, selection);
        using SplitFileWriter output = new(directory, "TESTE1", splitSize);
        byte[] hash = WbfsFileWriter.Write(disc, plan, output, progress: null, CancellationToken.None);
        return (output.Commit(), hash, plan);
    }

    private static byte[] Concatenate(IEnumerable<string> files) => [.. files.SelectMany(File.ReadAllBytes)];

    [Fact]
    public void The_engine_produces_the_image_in_file_order()
    {
        SyntheticDisc disc = SyntheticDisc.CreateStandard();
        WbfsPlan plan = WbfsWriter.Plan(disc.Disc, PartitionSelection.RemoveUpdate);

        long position = 0;
        foreach (WbfsChunk chunk in WbfsWriter.Produce(disc.Disc, plan))
        {
            Assert.Equal(position, chunk.Position);
            Assert.Equal(WbfsFormat.BlockSize, chunk.Data.Length);
            position += chunk.Data.Length;
        }

        Assert.Equal(plan.OutputSize, position);
    }

    [Fact]
    public void Writes_the_same_header_layout_as_the_reference_files()
    {
        SyntheticDisc disc = SyntheticDisc.CreateStandard();

        (IReadOnlyList<string> files, _, WbfsPlan plan) = WriteDisc(disc.Disc, "layout");

        string file = Assert.Single(files);
        Assert.Equal("TESTE1.wbfs", Path.GetFileName(file));
        byte[] bytes = File.ReadAllBytes(file);

        Assert.Equal((long)(plan.StoredBlocks + 1) * WbfsFormat.BlockSize, bytes.Length);
        Assert.Equal("WBFS"u8.ToArray(), bytes[..4]);
        Assert.Equal((uint)(bytes.Length / 512), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)));
        Assert.Equal(new byte[] { 9, 21, 0, 0, 1, 0, 0, 0 }, bytes[8..16]);

        // Disc header copy, then the block map.
        Assert.Equal("TESTE1"u8.ToArray(), bytes[0x200..0x206]);
        Assert.Equal(WiiConstants.WiiMagic, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0x218)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0x300)));
        Assert.Equal(4482, WbfsFormat.BlocksPerDisc);
    }

    [Fact]
    public void Stored_blocks_are_numbered_sequentially_and_only_cover_used_sectors()
    {
        SyntheticDisc disc = SyntheticDisc.CreateStandard();

        WbfsPlan plan = WbfsWriter.Plan(disc.Disc, PartitionSelection.All);

        HashSet<int> expectedBlocks =
        [
            .. disc.HeaderSectors
                .Concat(disc.SectorsOf(SyntheticDisc.UpdateOffset))
                .Concat(disc.SectorsOf(SyntheticDisc.GameOffset))
                .Concat(disc.SectorsOf(SyntheticDisc.ChannelOffset))
                .Select(sector => (int)(sector / WbfsFormat.SectorsPerBlock)),
        ];

        List<int> stored = [];
        int next = 1;
        ReadOnlySpan<ushort> map = plan.BlockMap;
        for (int block = 0; block < map.Length; block++)
        {
            if (map[block] == 0)
                continue;

            Assert.Equal(next++, map[block]);
            stored.Add(block);
        }

        Assert.Equal(expectedBlocks.Order(), stored);
        Assert.Equal(expectedBlocks.Count, plan.StoredBlocks);
    }

    [Fact]
    public void Reading_the_file_back_returns_every_used_sector_unchanged()
    {
        SyntheticDisc disc = SyntheticDisc.CreateStandard();
        (IReadOnlyList<string> files, _, WbfsPlan plan) = WriteDisc(disc.Disc, "roundtrip");

        using WbfsReader reader = WbfsReader.Open(files[0]);

        Assert.Equal("TESTE1", reader.Info.Disc.Id6);
        Assert.Equal("Synthetic Test Disc", reader.Info.Disc.Title);
        Assert.Equal(plan.StoredBlocks, reader.Info.StoredBlocks);
        Assert.True(reader.Info.SizeMatchesHeader);
        Assert.True(plan.BlockMap.SequenceEqual(reader.BlockMap));

        byte[] sector = new byte[WiiConstants.SectorSize];
        ReadOnlySpan<bool> used = plan.Usage.SectorUsed;
        for (int i = 0; i < used.Length; i++)
        {
            if (!used[i])
                continue;

            reader.ReadAt((long)i * WiiConstants.SectorSize, sector);
            Assert.True(sector.AsSpan().SequenceEqual(disc.Disc.ReadSector(i)), $"sector {i} differs");
        }

        // A block that was dropped reads back as zeros.
        reader.ReadAt(50L * WbfsFormat.BlockSize, sector);
        Assert.All(sector, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Splitting_only_changes_where_the_bytes_are_stored()
    {
        const long splitSize = 3 * 1024 * 1024 + 512; // deliberately not a multiple of the block size
        SyntheticDisc disc = SyntheticDisc.CreateStandard();

        (IReadOnlyList<string> whole, byte[] wholeHash, _) = WriteDisc(disc.Disc, "whole");
        (IReadOnlyList<string> parts, byte[] splitHash, _) = WriteDisc(disc.Disc, "split", splitSize: splitSize);

        Assert.True(parts.Count > 1);
        Assert.Equal(["TESTE1.wbfs", .. Enumerable.Range(1, parts.Count - 1).Select(i => $"TESTE1.wbf{i}")], parts.Select(Path.GetFileName));
        Assert.All(parts.SkipLast(1), part => Assert.Equal(splitSize, new FileInfo(part).Length));
        Assert.InRange(new FileInfo(parts[^1]).Length, 1, splitSize);

        Assert.Equal(wholeHash, splitHash);
        Assert.Equal(Concatenate(whole), Concatenate(parts));

        using WbfsReader reader = WbfsReader.Open(parts[0]);
        Assert.Equal(parts.Count, reader.Info.Parts.Count);
        Assert.True(reader.Info.SizeMatchesHeader);
    }

    [Fact]
    public void RemoveUpdate_leaves_the_update_partition_out_of_the_table_and_the_file()
    {
        // An update partition far from everything else, so it gets blocks of its own.
        const long updateOffset = 0x4000000;
        SyntheticDisc disc = new();
        disc.WriteHeader("TESTE1", "Update Test");
        disc.WritePartitionTable((0, updateOffset, 1), (0, SyntheticDisc.GameOffset, 0));
        disc.AddPartition(updateOffset, clusterCount: 24, new TestFile("sys.wad", 0x20000, 0x9000));
        disc.AddPartition(SyntheticDisc.GameOffset, clusterCount: 64, new TestFile("a.bin", 0x20000, 0x100));

        (IReadOnlyList<string> all, _, WbfsPlan allPlan) = WriteDisc(disc.Disc, "all");
        (IReadOnlyList<string> trimmed, _, WbfsPlan trimmedPlan) = WriteDisc(disc.Disc, "trimmed", PartitionSelection.RemoveUpdate);

        Assert.True(trimmedPlan.StoredBlocks < allPlan.StoredBlocks);
        Assert.Equal(0, trimmedPlan.BlockMap[(int)(updateOffset / WbfsFormat.BlockSize)]);

        using WbfsReader allReader = WbfsReader.Open(all[0]);
        using WbfsReader trimmedReader = WbfsReader.Open(trimmed[0]);
        Assert.Equal(2, WiiPartitionTable.Read(allReader).Count);
        Assert.Equal([new WiiPartitionEntry(0, SyntheticDisc.GameOffset, 0)], WiiPartitionTable.Read(trimmedReader));
    }

    [Fact]
    public void A_wbfs_file_can_be_the_source_of_another()
    {
        SyntheticDisc disc = SyntheticDisc.CreateStandard();
        (IReadOnlyList<string> first, byte[] firstHash, WbfsPlan firstPlan) = WriteDisc(disc.Disc, "first");

        using WbfsReader source = WbfsReader.Open(first[0]);
        (IReadOnlyList<string> second, byte[] secondHash, WbfsPlan secondPlan) = WriteDisc(source, "second");

        Assert.True(firstPlan.BlockMap.SequenceEqual(secondPlan.BlockMap));
        Assert.Equal(firstHash, secondHash);
        Assert.Equal(File.ReadAllBytes(first[0]), File.ReadAllBytes(second[0]));
    }

    [Fact]
    public void Verifier_hash_matches_what_was_written_and_notices_corruption()
    {
        SyntheticDisc disc = SyntheticDisc.CreateStandard();
        (IReadOnlyList<string> files, byte[] hash, _) = WriteDisc(disc.Disc, "verify", splitSize: 4 * 1024 * 1024);

        Assert.Equal(hash, WbfsVerifier.HashFiles(files, progress: null, CancellationToken.None));

        using (FileStream stream = new(files[^1], FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = stream.Length / 2;
            int original = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(original ^ 0x01));
        }

        Assert.NotEqual(hash, WbfsVerifier.HashFiles(files, progress: null, CancellationToken.None));
    }
}
