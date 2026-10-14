using GameFerry.Core.Wbfs;
using GameFerry.Tests.Support;

namespace GameFerry.Tests;

public sealed class SplitFileWriterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static byte[] Pattern(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 31 + i / 251))];

    [Fact]
    public void Nothing_carries_a_final_name_until_commit()
    {
        byte[] data = Pattern(5000);
        using SplitFileWriter writer = new(_temp.Path, "GAME01", splitSize: 2048);
        writer.Write(data);
        writer.Close();

        Assert.Equal(
            ["GAME01.wbf1.tmp", "GAME01.wbf2.tmp", "GAME01.wbfs.tmp"],
            Directory.GetFiles(_temp.Path).Select(Path.GetFileName).Order());

        IReadOnlyList<string> parts = writer.Commit();

        Assert.Equal(["GAME01.wbfs", "GAME01.wbf1", "GAME01.wbf2"], parts.Select(Path.GetFileName));
        Assert.Equal([2048L, 2048L, 904L], parts.Select(p => new FileInfo(p).Length));
        Assert.Equal(data, parts.SelectMany(File.ReadAllBytes).ToArray());
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }

    [Fact]
    public void Abandoning_a_write_removes_its_temporary_files()
    {
        using (SplitFileWriter writer = new(_temp.Path, "GAME01", splitSize: 1024))
            writer.Write(Pattern(3000));

        Assert.Empty(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public void Parts_the_drive_refuses_to_delete_are_reported_rather_than_forgotten()
    {
        SplitFileWriter writer = new(_temp.Path, "GAME01", splitSize: 1024);
        writer.Write(Pattern(3000));
        writer.Close();

        // Held open without delete sharing, as a drive that has gone away would refuse it.
        string held = _temp.Combine("GAME01.wbf1.tmp");
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            writer.Dispose();
            writer.Dispose();
        }

        Assert.Equal([held], writer.LeftBehind);
        Assert.Equal(["GAME01.wbf1.tmp"], Directory.GetFiles(_temp.Path).Select(Path.GetFileName));
        Assert.True(SplitFileWriter.TryDelete(held));
        Assert.Empty(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public void A_write_that_ends_exactly_on_the_boundary_does_not_open_an_empty_part()
    {
        using SplitFileWriter writer = new(_temp.Path, "GAME01", splitSize: 1024);
        writer.Write(Pattern(2048));

        Assert.Equal(2, writer.Commit().Count);
    }

    [Fact]
    public void Commit_removes_leftover_parts_from_a_larger_previous_copy()
    {
        using (SplitFileWriter big = new(_temp.Path, "GAME01", splitSize: 1024))
        {
            big.Write(Pattern(4000));
            big.Commit();
        }

        using SplitFileWriter small = new(_temp.Path, "GAME01", splitSize: 1024);
        small.Write(Pattern(1500));
        small.Commit();

        Assert.Equal(["GAME01.wbf1", "GAME01.wbfs"], Directory.GetFiles(_temp.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Zero_split_size_writes_one_file()
    {
        using SplitFileWriter writer = new(_temp.Path, "GAME01", splitSize: 0);
        writer.Write(Pattern(10_000));

        Assert.Equal(10_000, new FileInfo(Assert.Single(writer.Commit())).Length);
    }

    [Fact]
    public void Reader_spans_the_parts_and_zero_fills_past_the_end()
    {
        byte[] data = Pattern(5000);
        using SplitFileWriter writer = new(_temp.Path, "GAME01", splitSize: 2048);
        writer.Write(data);
        writer.Commit();

        Assert.Equal(3, SplitFileReader.FindParts(_temp.Combine("GAME01.wbfs")).Count);
        using SplitFileReader reader = new(SplitFileReader.FindParts(_temp.Combine("GAME01.wbfs")));
        Assert.Equal(5000, reader.Length);

        byte[] middle = new byte[3000];
        reader.ReadAt(1500, middle);
        Assert.Equal(data[1500..4500], middle);

        byte[] tail = new byte[200];
        reader.ReadAt(4900, tail);
        Assert.Equal([.. data[4900..], .. new byte[100]], tail);
    }

    [Fact]
    public void Split_size_must_be_a_whole_number_of_sectors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplitFileWriter(_temp.Path, "GAME01", splitSize: 1000));
    }
}
