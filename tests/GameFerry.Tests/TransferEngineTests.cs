using GameFerry.Core.Library;
using GameFerry.Core.Transfer;
using GameFerry.Core.Wbfs;
using GameFerry.Engine.Disc;
using GameFerry.Engine.Library;
using GameFerry.Engine.Transfer;
using GameFerry.Tests.Support;

namespace GameFerry.Tests;

/// <summary>
/// Runs the engine on plain ISO files, which need neither NKit nor 7-Zip, so the whole
/// write / verify / commit path is exercised without external tools.
/// </summary>
public sealed class TransferEngineTests : IDisposable
{
    private const long GameOffset = 0x400000;
    private const int ImageSize = 0x800000;

    private readonly TempDirectory _temp = new();
    private readonly List<string> _log = [];

    public void Dispose() => _temp.Dispose();

    private string SourceFolder => _temp.Combine("source");

    private string TargetFolder => _temp.Combine("target");

    /// <summary>Where the engine puts games in a target folder that starts out empty.</summary>
    private string GamesFolder => Path.Combine(TargetFolder, "wbfs");

    private AppSettings Settings => new() { WorkFolder = _temp.Combine("work"), NKitFolder = _temp.Combine("no-nkit") };

    private string WriteIso(string fileName, string id = "TSTE01", string title = "Engine Test")
    {
        SyntheticDisc disc = new();
        disc.WriteHeader(id, title);
        disc.WritePartitionTable((0, SyntheticDisc.UpdateOffset, 1), (0, GameOffset, 0));
        disc.AddPartition(SyntheticDisc.UpdateOffset, clusterCount: 24, new TestFile("sys.wad", 0x20000, 0x900));
        disc.AddPartition(GameOffset, clusterCount: 48, new TestFile("game.bin", 0x40000, 0x12345));

        byte[] image = new byte[ImageSize];
        disc.Disc.ReadAt(0, image);
        Directory.CreateDirectory(SourceFolder);
        string path = Path.Combine(SourceFolder, fileName);
        File.WriteAllBytes(path, image);
        return path;
    }

    private async Task<List<SourceItem>> ScanAsync()
    {
        List<SourceItem> items = SourceScanner.Enumerate(SourceFolder, recurse: false);
        await new SourceScanner(host: null, new ScanCache(_temp.Combine("cache.json"))).InspectAsync(items, inspected: null, CancellationToken.None);
        return items;
    }

    private Task<List<TransferResult>> RunAsync(
        List<SourceItem> items, AppSettings? settings = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(TargetFolder);
        TransferEngine engine = new(settings ?? Settings, host: null, sevenZip: null, GameTitles.Empty, (message, _) => _log.Add(message));
        return engine.RunAsync(items, TargetLibrary.Open(TargetFolder), overwrite, progress: null, cancellationToken);
    }

    [Fact]
    public async Task Writes_a_verified_game_into_its_own_folder_under_wbfs_and_leaves_no_temporary_files()
    {
        WriteIso("Engine Test (USA).iso");

        TransferResult result = Assert.Single(await RunAsync(await ScanAsync()));

        Assert.Equal(TransferStage.Done, result.Outcome);
        string folder = Path.Combine(GamesFolder, "Engine Test [TSTE01]");
        Assert.Equal(folder, result.Folder);
        Assert.Equal(["TSTE01.wbfs"], Directory.GetFiles(folder).Select(Path.GetFileName));

        WbfsCheckResult check = WbfsInspector.Check(Path.Combine(folder, "TSTE01.wbfs"));
        Assert.True(check.Ok);
        Assert.Equal(2, check.Partitions.Count);
        Assert.Equal(result.BytesWritten, check.Info.TotalSize);
        Assert.Empty(Directory.GetDirectories(_temp.Combine("work")));
    }

    [Fact]
    public async Task A_game_already_on_the_target_is_skipped_unless_overwriting()
    {
        WriteIso("Engine Test (USA).iso");
        List<SourceItem> items = await ScanAsync();
        await RunAsync(items);

        TransferResult second = Assert.Single(await RunAsync(items));
        Assert.Equal(TransferStage.Skipped, second.Outcome);
        Assert.Contains("Already on the target", second.Message);

        // Overwriting under a different naming scheme replaces the old folder rather than adding a second copy.
        AppSettings byFileName = Settings;
        byFileName.TitleSource = TitleSource.FileName;
        TransferResult third = Assert.Single(await RunAsync(items, byFileName, overwrite: true));

        Assert.Equal(TransferStage.Done, third.Outcome);
        Assert.Equal(["Engine Test (USA) [TSTE01]"], Directory.GetDirectories(GamesFolder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Only_the_first_of_two_copies_of_a_game_is_transferred()
    {
        WriteIso("Copy A.iso");
        WriteIso("Copy B.iso");

        List<TransferResult> results = await RunAsync(await ScanAsync());

        Assert.Equal([TransferStage.Done, TransferStage.Skipped], results.Select(r => r.Outcome));
        Assert.Single(Directory.GetDirectories(GamesFolder));
    }

    [Fact]
    public async Task Files_that_are_not_wii_discs_are_skipped_with_the_reason()
    {
        WriteIso("Good.iso");
        File.WriteAllBytes(Path.Combine(SourceFolder, "Bad.iso"), new byte[4096]);

        List<TransferResult> results = await RunAsync(await ScanAsync());

        TransferResult bad = results.Single(r => r.Item.FileName == "Bad.iso");
        Assert.Equal(TransferStage.Skipped, bad.Outcome);
        Assert.Equal("Not a Wii or GameCube disc image.", bad.Message);
        Assert.Equal(TransferStage.Done, results.Single(r => r.Item.FileName == "Good.iso").Outcome);
    }

    [Fact]
    public async Task Split_setting_is_honoured_and_the_parts_read_back_as_one_disc()
    {
        WriteIso("Engine Test (USA).iso");
        AppSettings settings = Settings;
        settings.Split = SplitMode.Always;

        TransferResult result = Assert.Single(await RunAsync(await ScanAsync(), settings));

        // The test image is far below the 4 GiB split size, so "always" still yields a single, valid file.
        Assert.Equal(TransferStage.Done, result.Outcome);
        Assert.True(WbfsInspector.Check(Path.Combine(result.Folder!, "TSTE01.wbfs")).Ok);
    }

    [Fact]
    public async Task Removing_the_update_partition_writes_a_smaller_game_with_only_the_data_partition()
    {
        WriteIso("Engine Test (USA).iso");
        AppSettings settings = Settings;
        settings.Partitions = PartitionSelection.RemoveUpdate;

        TransferResult result = Assert.Single(await RunAsync(await ScanAsync(), settings));

        WbfsCheckResult check = WbfsInspector.Check(Path.Combine(result.Folder!, "TSTE01.wbfs"));
        Assert.True(check.Ok);
        Assert.Equal(GameOffset, Assert.Single(check.Partitions).Offset);
    }

    [Fact]
    public async Task Cancelling_before_the_start_transfers_nothing()
    {
        WriteIso("Engine Test (USA).iso");
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        TransferResult result = Assert.Single(await RunAsync(await ScanAsync(), cancellationToken: cancelled.Token));

        Assert.Equal(TransferStage.Cancelled, result.Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(TargetFolder));
    }
}
