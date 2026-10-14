using System.Text.Json;
using GameFerry.Core.Library;
using GameFerry.Core.NKit;
using GameFerry.Core.Wbfs;
using GameFerry.Engine.Disc;
using GameFerry.Engine.Library;
using GameFerry.Engine.Wbfs;
using GameFerry.Tests.Support;

namespace GameFerry.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("Quest of Heroes: The Second Sword", "Quest of Heroes - The Second Sword")]
    [InlineData("Café Tycoon Deluxe", "Cafe Tycoon Deluxe")]
    [InlineData("Who Knows the Answer?", "Who Knows the Answer")]
    [InlineData("A/B\\C*D|E", "A B C D E")]
    [InlineData("Game [Prototype]", "Game (Prototype)")]
    [InlineData("  Spaced   out  ", "Spaced out")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("Cats & Dogs at the Summer Games", "Cats & Dogs at the Summer Games")]
    [InlineData("ゲームの冒険", "")]
    public void Titles_are_reduced_to_names_that_are_safe_on_fat32(string title, string expected)
    {
        Assert.Equal(expected, NameBuilder.SanitizeTitle(title));
    }

    [Fact]
    public void Folder_name_uses_the_first_usable_title_and_always_ends_with_the_id()
    {
        Assert.Equal("Space Racer [TSTE01]", NameBuilder.GameFolderName("TSTE01", "Space Racer", "SPACE RACER"));
        Assert.Equal("SPACE RACER [TSTE01]", NameBuilder.GameFolderName("TSTE01", null, "ゲーム", "SPACE RACER"));
        Assert.Equal("TSTE01 [TSTE01]", NameBuilder.GameFolderName("TSTE01", null, ""));
        Assert.Equal(80 + " [TSTE01]".Length, NameBuilder.GameFolderName("TSTE01", new string('x', 200)).Length);
    }

    [Theory]
    [InlineData(@"C:\games\Example Game (USA) (Rev 1).7z", "Example Game (USA) (Rev 1)")]
    [InlineData(@"C:\games\Example Game (USA) (Rev 1).nkit.iso", "Example Game (USA) (Rev 1)")]
    [InlineData(@"C:\games\Example Game [i].wbfs.7z", "Example Game [i]")]
    [InlineData(@"C:\games\TSTE01.wbfs", "TSTE01")]
    public void Title_from_a_file_name_drops_image_and_archive_extensions(string path, string expected)
    {
        Assert.Equal(expected, NameBuilder.TitleFromFileName(path));
    }

    [Fact]
    public void Game_titles_are_found_by_six_or_four_character_id()
    {
        string file = _temp.Combine("wiitdb.txt");
        File.WriteAllLines(file,
        [
            "TITLES = https://www.gametdb.com (type: Wii language: EN)",
            "TSTE02 = Example Game Deluxe",
            "TSTE = Example Game",
            "not a title line",
        ]);

        GameTitles titles = GameTitles.Load(file);

        Assert.Equal(2, titles.Count);
        Assert.Equal("Example Game Deluxe", titles.Find("TSTE02"));
        Assert.Equal("Example Game", titles.Find("TSTE01"));
        Assert.Null(titles.Find("XXXX01"));
        Assert.Equal(0, GameTitles.Load(_temp.Combine("missing.txt")).Count);
    }

    [Fact]
    public void Scan_cache_remembers_an_inspected_file_until_it_changes()
    {
        string cachePath = _temp.Combine("cache.json");
        DateTime written = new(2024, 3, 28, 0, 0, 0, DateTimeKind.Utc);
        SourceItem original = new(@"G:\games\Game.7z", 1000, written)
        {
            Inspected = true,
            EntryName = "Game.nkit.iso",
            Id6 = "TSTE01",
            DiscTitle = "SPORTS PACK",
            Kind = DiscKind.Wii,
            Format = SourceFormat.NkitIso,
            SourceSize = 5000,
            ImageSize = WiiConstants.SingleLayerSize,
            UpdateRemoved = true,
        };
        ScanCache cache = new(cachePath);
        cache.Store(original);
        cache.Save();

        SourceItem same = new(@"G:\games\Game.7z", 1000, written);
        SourceItem changed = new(@"G:\games\Game.7z", 1001, written);
        ScanCache reloaded = new(cachePath);

        Assert.True(reloaded.TryApply(same));
        Assert.True(same.CanTransfer);
        Assert.Equal(("Game.nkit.iso", "TSTE01", SourceFormat.NkitIso, 5000L, true), (same.EntryName, same.Id6, same.Format, same.SourceSize, same.UpdateRemoved));
        Assert.False(reloaded.TryApply(changed));
        Assert.False(changed.Inspected);
    }

    [Fact]
    public void A_restored_update_partition_is_counted_in_the_size_estimate()
    {
        SourceItem item = new(@"G:\games\Game.7z", 1000, DateTime.UtcNow)
        {
            Format = SourceFormat.NkitIso,
            SourceSize = 1L << 30,
            UpdateRemoved = true,
        };

        long withUpdate = item.EstimateWbfsSize(PartitionSelection.All);
        long withoutUpdate = item.EstimateWbfsSize(PartitionSelection.RemoveUpdate);

        Assert.InRange(withoutUpdate, 1L << 30, (long)(1.1 * (1L << 30)));
        Assert.Equal(160L << 20, withUpdate - withoutUpdate);
    }

    [Fact]
    public void Host_messages_are_parsed_into_typed_results()
    {
        using JsonDocument info = JsonDocument.Parse(
            """{"t":"info","path":"G:\\a b.7z","ok":true,"entry":"a b.nkit.iso","id":"TSTE01","title":"Example Game","isWii":true,"isNkit":true,"updateRemoved":true,"sourceSize":7408025600,"imageSize":8511160320}""");
        using JsonDocument failure = JsonDocument.Parse("""{"t":"info","path":"x.zip","ok":false,"message":"No disc image found."}""");
        using JsonDocument result = JsonDocument.Parse(
            """{"t":"result","ok":true,"output":"C:\\w\\TSTE01.iso","id":"TSTE01","outputSize":8511160320,"crc":"1234ABCD","readResult":"VerifySuccess","redumpMatch":"Redump","redumpName":"Example Game (USA)","seconds":19.5,"message":null}""");
        using JsonDocument progress = JsonDocument.Parse("""{"t":"progress","pass":"To ISO","value":0.5,"total":0.25}""");

        NKitImageInfo image = NKitHostProtocol.ToImageInfo(info.RootElement);
        Assert.Equal((@"G:\a b.7z", "a b.nkit.iso", "TSTE01", true, true, true, 7408025600L, 8511160320L),
            (image.Path, image.Entry, image.Id, image.Ok, image.IsNkit, image.UpdateRemoved, image.SourceSize, image.ImageSize));
        Assert.False(image.IsGameCube);

        NKitImageInfo missing = NKitHostProtocol.ToImageInfo(failure.RootElement);
        Assert.False(missing.Ok);
        Assert.Equal("No disc image found.", missing.Message);

        NKitResult converted = NKitHostProtocol.ToResult(result.RootElement);
        Assert.True(converted.Ok);
        Assert.True(converted.MatchesRedump);
        Assert.Equal((@"C:\w\TSTE01.iso", 8511160320L, 19.5), (converted.Output, converted.OutputSize, converted.Seconds));
        Assert.Null(converted.Message);

        Assert.Equal(new NKitProgress("To ISO", 0.5, 0.25), NKitHostProtocol.ToProgress(progress.RootElement));
        Assert.Equal("progress", NKitHostProtocol.Type(progress.RootElement));
    }

    [Fact]
    public void Games_go_in_the_wbfs_folder_of_whatever_is_chosen_drive_or_folder()
    {
        string staging = _temp.Combine("staging");
        string card = _temp.Combine("card");
        string cardWbfs = Path.Combine(card, "wbfs");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(cardWbfs);

        // A new, empty folder stands in for a drive just as well as a drive does.
        TargetLibrary emptyFolder = TargetLibrary.Open(staging);
        Assert.Equal(Path.Combine(staging, "wbfs"), emptyFolder.GamesRoot);
        Assert.Equal(staging, emptyFolder.DeviceRoot);
        Assert.False(emptyFolder.StoresGamesDirectly);

        Assert.Equal(cardWbfs, TargetLibrary.Open(card).GamesRoot);
        Assert.Equal(@"C:\wbfs", TargetLibrary.Open(@"C:\").GamesRoot);

        // Choosing the wbfs folder itself means the same as choosing the folder it is in.
        TargetLibrary wbfsChosen = TargetLibrary.Open(cardWbfs);
        Assert.Equal((cardWbfs, card, false), (wbfsChosen.GamesRoot, wbfsChosen.DeviceRoot, wbfsChosen.StoresGamesDirectly));
    }

    [Fact]
    public void A_folder_that_already_has_games_directly_in_it_keeps_taking_them_that_way()
    {
        string collection = _temp.Combine("collection");
        string game = Path.Combine(collection, "Standard Disc [TESTE1]");
        Directory.CreateDirectory(game);
        WriteGame(game, "TESTE1", splitSize: 0);

        TargetLibrary target = TargetLibrary.Open(collection);

        Assert.Equal(collection, target.GamesRoot);
        Assert.True(target.StoresGamesDirectly);
        Assert.Equal("TESTE1", Assert.Single(target.Scan()).Id6);

        // Give it a wbfs folder and it is laid out like a drive, so that folder is where games belong.
        Directory.CreateDirectory(Path.Combine(collection, "wbfs"));
        Assert.Equal(Path.Combine(collection, "wbfs"), TargetLibrary.Open(collection).GamesRoot);
    }

    [Fact]
    public void Folders_that_merely_look_like_game_folders_do_not_make_a_collection()
    {
        // A cover folder and an unrelated bracketed folder, but no WBFS file anywhere.
        string staging = _temp.Combine("staging");
        Directory.CreateDirectory(Path.Combine(staging, "apps", "usbloader_gx", "images"));
        Directory.CreateDirectory(Path.Combine(staging, "Notes [abcdef]"));
        File.WriteAllText(Path.Combine(staging, "Notes [abcdef]", "readme.txt"), "not a game");

        Assert.Equal(Path.Combine(staging, "wbfs"), TargetLibrary.Open(staging).GamesRoot);
    }

    [Fact]
    public void Splitting_follows_the_file_system_unless_forced()
    {
        // The temp folder is on NTFS (or another large-file system) on any machine this runs on.
        TargetLibrary target = TargetLibrary.Open(_temp.Path);

        Assert.True(target.SupportsLargeFiles);
        Assert.Equal(0, target.SplitSizeFor(SplitMode.Auto));
        Assert.Equal(0, target.SplitSizeFor(SplitMode.Never));
        Assert.Equal(0xFFFF8000, target.SplitSizeFor(SplitMode.Always));
        Assert.Equal(4L * 1024 * 1024 * 1024 - 32 * 1024, WbfsFormat.DefaultSplitSize);
    }

    [Fact]
    public void Scan_lists_games_in_folders_and_loose_and_delete_removes_only_the_games_files()
    {
        string root = _temp.Combine("games");
        string folder = Path.Combine(root, "Standard Disc [TESTE1]");
        Directory.CreateDirectory(folder);
        WriteGame(folder, "TESTE1", splitSize: 4 * 1024 * 1024);
        WriteGame(root, "LOOSE1", splitSize: 0);
        File.WriteAllText(Path.Combine(root, "notes.txt"), "keep me");
        File.WriteAllText(Path.Combine(folder, "cover.png"), "keep me too");

        TargetLibrary target = TargetLibrary.Open(root);
        List<InstalledGame> games = target.Scan();

        Assert.Equal(["LOOSE1", "TESTE1"], games.Select(g => g.Id6).Order());
        InstalledGame inFolder = games.Single(g => g.Id6 == "TESTE1");
        Assert.True(inFolder.Files.Count > 1);
        Assert.Equal(inFolder.Files.Sum(f => new FileInfo(f).Length), inFolder.Size);

        target.Delete(inFolder);
        target.Delete(games.Single(g => g.Id6 == "LOOSE1"));

        // The folder survives because something other than the game is in it; so do unrelated files.
        Assert.Equal(["cover.png"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.Equal(["notes.txt"], Directory.GetFiles(root).Select(Path.GetFileName));
        Assert.Empty(target.Scan());
    }

    private static void WriteGame(string directory, string id, long splitSize)
    {
        SyntheticDisc disc = new();
        disc.WriteHeader(id, "Standard Disc");
        disc.WritePartitionTable((0, SyntheticDisc.GameOffset, 0));
        disc.AddPartition(SyntheticDisc.GameOffset, clusterCount: 400, new TestFile("a.bin", 0x20000, 0x100), new TestFile("b.bin", 0x800000, 0x100));

        WbfsPlan plan = WbfsWriter.Plan(disc.Disc, PartitionSelection.All);
        using SplitFileWriter output = new(directory, id, splitSize);
        WbfsFileWriter.Write(disc.Disc, plan, output, progress: null, CancellationToken.None);
        output.Commit();
    }
}
