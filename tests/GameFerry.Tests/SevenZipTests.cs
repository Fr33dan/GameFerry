using System.IO.Compression;
using GameFerry.Core.Archives;
using GameFerry.Tests.Support;

namespace GameFerry.Tests;

public sealed class SevenZipTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Extracts_the_named_entry_from_any_depth_and_reports_progress()
    {
        // 7-Zip is an optional speed-up; where it isn't installed there is nothing to test.
        if (SevenZip.TryLocate() is not { } sevenZip)
            return;

        byte[] image = new byte[3_000_000];
        new Random(7).NextBytes(image);
        string archive = _temp.Combine("games.zip");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (Stream entry = zip.CreateEntry("sub folder/Game (USA) [x].nkit.iso", CompressionLevel.NoCompression).Open())
                entry.Write(image);
            using (Stream other = zip.CreateEntry("readme.txt").Open())
                other.Write("not wanted"u8);
        }

        string output = _temp.Combine("out");
        Directory.CreateDirectory(output);
        List<double> progress = [];

        string extracted = await sevenZip.ExtractAsync(
            archive, "Game (USA) [x].nkit.iso", output, new ListProgress(progress), CancellationToken.None);

        Assert.Equal(Path.Combine(output, "Game (USA) [x].nkit.iso"), extracted);
        Assert.Equal(image, File.ReadAllBytes(extracted));
        Assert.Equal([extracted], Directory.GetFiles(output));
        Assert.Equal(1, progress[^1]);
    }

    [Fact]
    public async Task A_missing_entry_is_reported_as_an_error()
    {
        if (SevenZip.TryLocate() is not { } sevenZip)
            return;

        string archive = _temp.Combine("empty.zip");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using Stream entry = zip.CreateEntry("a.txt").Open();
            entry.Write("x"u8);
        }

        await Assert.ThrowsAsync<IOException>(() =>
            sevenZip.ExtractAsync(archive, "missing.iso", _temp.Path, progress: null, CancellationToken.None));
    }

    private sealed class ListProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }
}
