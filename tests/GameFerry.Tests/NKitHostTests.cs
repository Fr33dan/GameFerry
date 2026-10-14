using GameFerry.Core.NKit;
using GameFerry.Tests.Support;

namespace GameFerry.Tests;

/// <summary>
/// Exercises the real NKit helper process, unpacked from the copy embedded in the core library.
/// It is a .NET Framework program, which every supported Windows version can run.
/// </summary>
public sealed class NKitHostTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_embedded_helper_unpacks_with_everything_nkit_needs_beside_it()
    {
        string host = EmbeddedNKitHost.Unpack();

        string folder = Path.GetDirectoryName(host)!;
        Assert.Equal("GameFerry.NKitHost.exe", Path.GetFileName(host));
        Assert.StartsWith(GameFerry.Core.AppPaths.NKitHostFolder, folder);
        foreach (string needed in (string[])["GameFerry.NKitHost.exe", "NKit.dll", "NKit.dll.config", "SharpCompress.dll"])
            Assert.True(new FileInfo(Path.Combine(folder, needed)).Length > 0, $"{needed} is missing or empty");

        // Unpacking again finds the same files rather than writing a second copy.
        DateTime written = File.GetLastWriteTimeUtc(host);
        Assert.Equal(host, EmbeddedNKitHost.Unpack());
        Assert.Equal(written, File.GetLastWriteTimeUtc(host));
    }

    [Fact]
    public async Task The_unpacked_helper_runs_and_identifies_images()
    {
        // A plain Wii image and a file that is nothing of the sort.
        SyntheticDisc disc = new();
        disc.WriteHeader("TSTE01", "Helper Test");
        disc.WritePartitionTable((0, 0x400000, 0));
        disc.AddPartition(0x400000, clusterCount: 16, new TestFile("a.bin", 0x10000, 64));
        byte[] image = new byte[0x800000];
        disc.Disc.ReadAt(0, image);
        string iso = _temp.Combine("Helper Test (USA).iso");
        File.WriteAllBytes(iso, image);

        string notAnArchive = _temp.Combine("notes.zip");
        File.WriteAllText(notAnArchive, "this is not a zip file");

        NKitHostClient client = Assert.IsType<NKitHostClient>(NKitHostClient.TryCreate());
        List<NKitImageInfo> found = [];
        await client.InspectAsync([iso, notAnArchive], found.Add, CancellationToken.None);

        NKitImageInfo wii = found.Single(info => info.Path == iso);
        Assert.True(wii.Ok, wii.Message);
        Assert.Equal(("TSTE01", "Helper Test", true, false, false), (wii.Id, wii.Title, wii.IsWii, wii.IsNkit, wii.IsGameCube));

        Assert.False(found.Single(info => info.Path == notAnArchive).Ok);
    }
}
