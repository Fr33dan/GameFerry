using GameFerry.Engine.Archives;

namespace GameFerry.Tests;

/// <summary>Archives in TestData were made with 7-Zip 26.00 from generated data (see their file names for the settings).</summary>
public class SevenZipArchiveTests
{
    private static FileStream Open(string name) => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    [Fact]
    public void Finds_the_single_file_its_coder_and_where_its_packed_bytes_are()
    {
        using FileStream file = Open("single-lzma.7z");

        SevenZipArchive archive = SevenZipArchive.Read(file, unpack: null);

        SevenZipEntry entry = Assert.Single(archive.Entries);
        Assert.Equal(new SevenZipEntry("game.nkit.iso", 65536, 0x454B6221, 0, 0, false), entry);
        SevenZipFolder folder = Assert.Single(archive.Folders);
        Assert.True(folder.IsSimple);
        Assert.True(folder.Coders[0].Is(SevenZipCoder.Lzma));
        Assert.Equal(5, folder.Coders[0].Properties.Length);
        Assert.Equal(32, folder.PackOffset);
        Assert.InRange(folder.PackSize, 1, file.Length - 32);
        Assert.Equal(65536, folder.UnpackSize);
    }

    [Fact]
    public void Files_in_a_solid_folder_follow_one_another()
    {
        using FileStream file = Open("solid-lzma2.7z");

        SevenZipArchive archive = SevenZipArchive.Read(file, unpack: null);

        Assert.Equal(
            [
                new SevenZipEntry("a.bin", 10240, 0x3F3EA803, 0, 0, false),
                new SevenZipEntry("b.iso", 20480, 0xBA061009, 0, 10240, false),
            ],
            archive.Entries);
        SevenZipFolder folder = Assert.Single(archive.Folders);
        Assert.True(folder.Coders[0].Is(SevenZipCoder.Lzma2));
        Assert.Single(folder.Coders[0].Properties);
        Assert.Equal(30720, folder.UnpackSize);
    }

    [Fact]
    public void A_compressed_table_of_contents_needs_an_unpacker()
    {
        using FileStream file = Open("compressed-header.7z");
        SevenZipFolder? asked = null;

        Assert.Throws<NotSupportedException>(() => SevenZipArchive.Read(file, unpack: null));
        Assert.Throws<InvalidOperationException>(() => SevenZipArchive.Read(file, (folder, _) =>
        {
            asked = folder;
            throw new InvalidOperationException("no LZMA here");
        }));

        // What it asked to have unpacked is the table of contents: a single LZMA stream.
        Assert.NotNull(asked);
        Assert.True(asked.IsSimple);
        Assert.True(asked.Coders[0].Is(SevenZipCoder.Lzma));
        Assert.InRange(asked.PackOffset + asked.PackSize, 33, file.Length);
    }

    [Fact]
    public void Anything_else_is_not_a_7z_archive()
    {
        using MemoryStream notAnArchive = new(new byte[64]);

        Assert.False(SevenZipArchive.HasSignature(new byte[8]));
        Assert.Throws<InvalidDataException>(() => SevenZipArchive.Read(notAnArchive, unpack: null));
    }
}
