using GameFerry.Core;
using GameFerry.Core.Archives;
using GameFerry.Core.Covers;
using GameFerry.Core.Library;
using GameFerry.Core.NKit;
using GameFerry.Core.Transfer;
using GameFerry.Core.Wbfs;
using GameFerry.Engine.Disc;
using GameFerry.Engine.Library;
using GameFerry.Engine.Transfer;

namespace GameFerry.Cli;

internal static class Commands
{
    private const int MaxUnusableListed = 10;

    public static async Task<int> ScanAsync(string[] args, CancellationToken cancellationToken)
    {
        CliArguments parsed = CliArguments.Parse(args, "recurse");
        parsed.AllowOnly("recurse");
        if (parsed.Positional.Count != 1)
            throw new UsageException("Usage: ferry scan <folder|file> [--recurse]");

        List<SourceItem> items = await LoadSourcesAsync(parsed.Positional, parsed.Has("recurse"), new ConsoleUi(), cancellationToken);
        PrintSources(items, AppSettings.Load().Partitions);
        return 0;
    }

    public static int Targets()
    {
        foreach (TargetDrive drive in TargetLibrary.ListDrives())
            Console.WriteLine(drive);
        return 0;
    }

    public static int List(string[] args)
    {
        CliArguments parsed = CliArguments.Parse(args);
        parsed.AllowOnly();
        if (parsed.Positional.Count != 1)
            throw new UsageException("Usage: ferry list <drive|folder>");

        TargetLibrary target = TargetLibrary.Open(parsed.Positional[0]);
        GameTitles titles = GameTitles.Load(NKitLocator.Locate(AppSettings.Load().NKitFolder).GameTdb);
        List<InstalledGame> games = target.Scan();
        foreach (InstalledGame game in games)
        {
            string title = titles.Find(game.Id6) ?? game.Title;
            Console.WriteLine($"{game.Id6}  {TargetLibrary.FormatSize(game.Size),10}  {title}  ({game.Files.Count} file{(game.Files.Count == 1 ? "" : "s")})");
        }

        Console.WriteLine();
        Console.WriteLine($"{games.Count} game(s), {TargetLibrary.FormatSize(games.Sum(g => g.Size))} in {target.GamesRoot}");
        Console.WriteLine($"{DescribeFileSystem(target)}, {TargetLibrary.FormatSize(target.FreeBytes)} free");
        return 0;
    }

    public static async Task<int> TransferAsync(string[] args, CancellationToken cancellationToken)
    {
        CliArguments parsed = CliArguments.Parse(args, "recurse", "no-verify", "one-at-a-time", "overwrite", "no-covers", "dry-run", "verbose");
        parsed.AllowOnly(
            "to", "recurse", "partitions", "split", "titles", "no-verify", "one-at-a-time", "overwrite",
            "no-covers", "loader", "cover-root", "nkit", "work", "dry-run", "verbose");
        if (parsed.Positional.Count == 0)
            throw new UsageException("Usage: ferry transfer <folder|file>... --to <drive|folder> [options]");

        AppSettings settings = AppSettings.Load();
        ApplyOverrides(settings, parsed);
        bool verbose = parsed.Has("verbose");

        ConsoleUi ui = new();
        TargetLibrary target = TargetLibrary.Open(parsed.Require("to"));
        ui.WriteLine($"Target: {target.GamesRoot}  ({DescribeFileSystem(target)}, {TargetLibrary.FormatSize(target.FreeBytes)} free)");
        if (target.StoresGamesDirectly)
            ui.WriteLine("  This folder has games directly in it and no wbfs folder, so new games are added the same way.");

        List<SourceItem> items = await LoadSourcesAsync(parsed.Positional, parsed.Has("recurse"), ui, cancellationToken);
        HashSet<string> onTarget = [.. target.Scan().Select(g => g.Id6)];
        bool overwrite = parsed.Has("overwrite");

        // The same game can be present twice (an archive and its extracted image); only the first copy is transferred.
        List<SourceItem> queue = [.. items
            .Where(i => i.CanTransfer && (overwrite || !onTarget.Contains(i.Id6!)))
            .DistinctBy(i => i.Id6, StringComparer.OrdinalIgnoreCase)];
        List<SourceItem> unusable = [.. items.Where(i => !i.CanTransfer)];
        ui.WriteLine($"{items.Count} file(s) found: {queue.Count} to transfer, "
            + $"{items.Count - unusable.Count - queue.Count} already on the target or duplicates, {unusable.Count} not usable.");

        // A big library folder can hold thousands of non-disc files; only a handful are worth spelling out.
        if (unusable.Count is > 0 and <= MaxUnusableListed)
        {
            foreach (SourceItem item in unusable)
                ui.WriteLine($"  Not usable: {item.FileName} - {item.Problem}");
        }

        ui.WriteLine($"Estimated space needed: {TargetLibrary.FormatSize(queue.Sum(i => i.EstimateWbfsSize(settings.Partitions)))}");

        if (parsed.Has("dry-run"))
        {
            PrintSources(items, settings.Partitions, onTarget);
            return 0;
        }

        NKitHostClient? host = NKitHostClient.TryCreate();
        GameTitles titles = GameTitles.Load(NKitLocator.Locate(settings.NKitFolder).GameTdb);
        TransferEngine engine = new(settings, host, SevenZip.TryLocate(), titles, (message, detail) =>
        {
            if (!detail || verbose)
                ui.WriteLine(message);
        });

        ui.WriteLine();
        List<TransferResult> results = await engine.RunAsync(queue, target, overwrite, new SynchronousProgress(ui), cancellationToken);
        ui.EndProgress();

        ui.WriteLine();
        int done = results.Count(r => r.Outcome == TransferStage.Done);
        int failed = results.Count(r => r.Outcome == TransferStage.Failed);
        int cancelled = results.Count(r => r.Outcome == TransferStage.Cancelled);
        ui.WriteLine($"Transferred {done} of {queue.Count}, failed {failed}"
            + (cancelled > 0 ? $", cancelled {cancelled}" : string.Empty)
            + $". {TargetLibrary.FormatSize(results.Sum(r => r.BytesWritten))} written.");

        foreach (TransferResult failure in results.Where(r => r.Outcome == TransferStage.Failed))
            ui.WriteLine($"  FAILED {failure.Item.FileName}: {failure.Message}");

        List<string> copied = [.. results.Where(r => r.Outcome == TransferStage.Done).Select(r => r.Item.Id6!)];
        if (settings.DownloadCoversAfterTransfer && copied.Count > 0 && cancelled == 0)
            await DownloadCoversAsync(settings, target, copied, overwrite: false, ui, cancellationToken);

        return cancelled > 0 ? 130 : failed > 0 ? 1 : 0;
    }

    public static async Task<int> CoversAsync(string[] args, CancellationToken cancellationToken)
    {
        CliArguments parsed = CliArguments.Parse(args, "force");
        parsed.AllowOnly("loader", "cover-root", "force");
        if (parsed.Positional.Count != 1)
            throw new UsageException("Usage: ferry covers <drive|folder> [--loader auto|wiiflow|gx|cfg|all] [--cover-root <folder>] [--force]");

        AppSettings settings = AppSettings.Load();
        ApplyOverrides(settings, parsed);

        TargetLibrary target = TargetLibrary.Open(parsed.Positional[0]);
        List<string> ids = [.. target.Scan().Select(g => g.Id6)];
        if (ids.Count == 0)
        {
            Console.WriteLine($"No games found in {target.GamesRoot}.");
            return 0;
        }

        return await DownloadCoversAsync(settings, target, ids, parsed.Has("force"), new ConsoleUi(), cancellationToken) ? 0 : 1;
    }

    /// <summary>Downloads cover art for the given games; false if there was nowhere to put it or something failed.</summary>
    private static async Task<bool> DownloadCoversAsync(
        AppSettings settings, TargetLibrary target, IReadOnlyCollection<string> gameIds, bool overwrite, ConsoleUi ui, CancellationToken cancellationToken)
    {
        CoverDestination? destination = CoverDestination.Resolve(settings.CoverLoader, settings.CoverRoot, target, out string? reason);
        if (destination is null)
        {
            ui.WriteLine($"Cover art: {reason}");
            return false;
        }

        ui.WriteLine($"Cover art for {gameIds.Count} game(s) -> {destination.Root} ({string.Join(", ", destination.Layouts.Select(l => l.Name))})");
        using HttpClient http = CoverDownloader.CreateHttpClient();
        CoverSummary summary = await new CoverDownloader(http).DownloadAsync(
            gameIds, destination, overwrite, new SynchronousCoverProgress(ui), cancellationToken);
        ui.EndProgress();

        ui.WriteLine($"Cover art: {summary}.");
        if (summary.GamesWithoutArt.Count > 0)
        {
            ui.WriteLine($"  GameTDB has no art for: {string.Join(", ", summary.GamesWithoutArt)}");
            ui.WriteLine($"  Anyone can add the missing covers at {GameTdbArt.Site}.");
        }
        return summary.Failed == 0;
    }

    public static async Task<int> VerifyAsync(string[] args, CancellationToken cancellationToken)
    {
        CliArguments parsed = CliArguments.Parse(args, "deep");
        parsed.AllowOnly("deep", "nkit", "work");
        if (parsed.Positional.Count != 1)
            throw new UsageException("Usage: ferry verify <file.wbfs> [--deep]");

        string path = parsed.Positional[0];
        WbfsCheckResult check = WbfsInspector.Check(path);
        WbfsFileInfo info = check.Info;

        Console.WriteLine($"{info.Disc.Id6}  {info.Disc.Title}  (disc {info.Disc.DiscNumber}, version {info.Disc.Version})");
        Console.WriteLine($"  Files:        {info.Parts.Count} ({string.Join(" + ", info.Parts.Select(p => new FileInfo(p).Length.ToString("N0")))} bytes)");
        Console.WriteLine($"  Header:       {info.HdSectorCount:N0} sectors of {1 << info.HdSectorShift} bytes, {info.BlockSize >> 20} MiB blocks"
            + $" - size {(info.SizeMatchesHeader ? "matches" : "DOES NOT MATCH")} the files");
        Console.WriteLine($"  Blocks:       {info.StoredBlocks:N0} stored, highest number {info.HighestStoredBlock:N0}, last disc block {info.LastDiscBlock:N0}"
            + $" - map {(check.BlockMapValid ? "valid" : "INVALID")}");
        Console.WriteLine($"  Partitions:   {string.Join(", ", check.Partitions.Select(p => $"{p.TypeName} @0x{p.Offset:X}"))}");
        Console.WriteLine($"  Used sectors: {check.UsedSectors:N0}, missing from the file: {check.MissingSectors:N0}");
        Console.WriteLine($"  Surplus:      {check.SurplusBlocks:N0} stored block(s) hold nothing the disc uses");
        foreach (string warning in check.Warnings)
            Console.WriteLine($"  Warning: {warning}");
        Console.WriteLine(check.Ok ? "  Structure OK." : "  STRUCTURE PROBLEMS FOUND.");

        if (!parsed.Has("deep"))
            return check.Ok ? 0 : 1;

        NKitHostClient host = NKitHostClient.TryCreate() ?? throw new IOException(NKitHostClient.UnavailableMessage);
        AppSettings settings = AppSettings.Load();
        ApplyOverrides(settings, parsed);

        WorkDirectory work = new(settings.WorkFolder);
        using WorkJob job = work.CreateJob();
        ConsoleUi ui = new();
        ui.WriteLine("  Rebuilding the original disc with NKit (nothing is kept)...");
        NKitResult result = await host.RecoverTestAsync(
            new NKitJob(Path.GetFullPath(path), null, string.Empty, job.Path, NKitLocator.Locate(settings.NKitFolder)),
            new SynchronousNKitProgress(ui),
            log: null,
            cancellationToken);
        ui.EndProgress();

        if (result.Ok && result.MatchesRedump)
        {
            ui.WriteLine($"  Redump match: {result.RedumpName} (CRC {result.Crc}). Nothing but padding was dropped.");
            return check.Ok ? 0 : 1;
        }

        ui.WriteLine($"  NO REDUMP MATCH (result: {result.RedumpMatch ?? "none"}, CRC {result.Crc}). {result.Message}");
        return 1;
    }

    public static int Compare(string[] args, CancellationToken cancellationToken)
    {
        CliArguments parsed = CliArguments.Parse(args);
        parsed.AllowOnly();
        if (parsed.Positional.Count != 2)
            throw new UsageException("Usage: ferry compare <first.wbfs> <second.wbfs>");

        ConsoleUi ui = new();
        WbfsComparison comparison = WbfsInspector.Compare(
            parsed.Positional[0], parsed.Positional[1], new SynchronousFraction(ui, "Comparing"), cancellationToken);
        ui.EndProgress();

        ui.WriteLine($"Disc header copies: {(comparison.DiscHeadersEqual ? "identical" : "DIFFERENT")}");
        ui.WriteLine($"Blocks stored by both: {comparison.BlocksInBoth:N0}; only in first: {comparison.BlocksOnlyInFirst:N0}; only in second: {comparison.BlocksOnlyInSecond:N0}");
        ui.WriteLine($"Blocks whose contents differ: {comparison.DifferingBlocks.Count:N0}"
            + (comparison.DifferingBlocks.Count > 0 ? $" (disc blocks {string.Join(", ", comparison.DifferingBlocks.Take(16))}{(comparison.DifferingBlocks.Count > 16 ? ", ..." : "")})" : string.Empty));
        foreach ((long offset, int length) in comparison.FirstBlockDifferences)
            ui.WriteLine($"  first block differs at disc offset 0x{offset:X}, {length} byte(s)");

        bool identical = comparison.SameMap && comparison.DifferingBlocks.Count == 0;
        ui.WriteLine(identical ? "The two files hold the same disc data." : "The two files differ.");
        return identical ? 0 : 1;
    }

    private static async Task<List<SourceItem>> LoadSourcesAsync(IReadOnlyList<string> paths, bool recurse, ConsoleUi ui, CancellationToken cancellationToken)
    {
        List<SourceItem> items = [];
        foreach (string path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new UsageException($"Source not found: {path}");
            items.AddRange(SourceScanner.Enumerate(path, recurse));
        }

        SourceScanner scanner = new(NKitHostClient.TryCreate(), new ScanCache(AppPaths.ScanCacheFile));
        int inspected = 0;
        await scanner.InspectAsync(
            items,
            _ => ui.Progress("Reading game headers", Interlocked.Increment(ref inspected) / (double)items.Count),
            cancellationToken);
        ui.EndProgress();
        return items;
    }

    private static void PrintSources(List<SourceItem> items, PartitionSelection partitions, HashSet<string>? onTarget = null)
    {
        foreach (SourceItem item in items)
        {
            if (!item.CanTransfer)
            {
                Console.WriteLine($"------  {"",-15}  {"",10}  {item.FileName}  -- {item.Problem}");
                continue;
            }

            string note = onTarget?.Contains(item.Id6!) == true ? "  [on target]" : string.Empty;
            Console.WriteLine($"{item.Id6}  {item.FormatLabel,-15}  {"~" + TargetLibrary.FormatSize(item.EstimateWbfsSize(partitions)),10}  {item.FileName}{note}");
        }
    }

    private static void ApplyOverrides(AppSettings settings, CliArguments parsed)
    {
        if (parsed.Get("nkit") is { } nkit)
            settings.NKitFolder = nkit;
        if (parsed.Get("work") is { } work)
            settings.WorkFolder = work;
        if (parsed.Has("no-verify"))
            settings.VerifyAfterWrite = false;
        if (parsed.Has("one-at-a-time"))
            settings.PrepareAhead = false;
        if (parsed.Has("no-covers"))
            settings.DownloadCoversAfterTransfer = false;
        if (parsed.Get("cover-root") is { } coverRoot)
            settings.CoverRoot = coverRoot;

        if (parsed.Get("loader") is { } loader)
        {
            settings.CoverLoader = loader.ToLowerInvariant() switch
            {
                "auto" => CoverLoaderChoice.Auto,
                "wiiflow" => CoverLoaderChoice.WiiFlow,
                "gx" => CoverLoaderChoice.UsbLoaderGx,
                "cfg" => CoverLoaderChoice.ConfigurableUsbLoader,
                "all" => CoverLoaderChoice.All,
                _ => throw new UsageException("--loader must be auto, wiiflow, gx, cfg or all."),
            };
        }

        if (parsed.Get("partitions") is { } partitions)
        {
            settings.Partitions = partitions.ToLowerInvariant() switch
            {
                "all" => PartitionSelection.All,
                "no-update" => PartitionSelection.RemoveUpdate,
                "game" => PartitionSelection.GameOnly,
                _ => throw new UsageException("--partitions must be all, no-update or game."),
            };
        }

        if (parsed.Get("split") is { } split)
        {
            settings.Split = split.ToLowerInvariant() switch
            {
                "auto" => SplitMode.Auto,
                "always" or "4g" => SplitMode.Always,
                "never" or "none" => SplitMode.Never,
                _ => throw new UsageException("--split must be auto, always or never."),
            };
        }

        if (parsed.Get("titles") is { } titles)
        {
            settings.TitleSource = titles.ToLowerInvariant() switch
            {
                "gametdb" => TitleSource.GameTdb,
                "disc" => TitleSource.Disc,
                "file" => TitleSource.FileName,
                _ => throw new UsageException("--titles must be gametdb, disc or file."),
            };
        }
    }

    private static string DescribeFileSystem(TargetLibrary target) =>
        target.FileSystem.Length == 0 ? "unknown file system" : target.FileSystem;

    private sealed class SynchronousProgress(ConsoleUi ui) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => ui.Progress(value);
    }

    private sealed class SynchronousNKitProgress(ConsoleUi ui) : IProgress<NKitProgress>
    {
        public void Report(NKitProgress value) => ui.Progress("NKit recovery", value.Total);
    }

    private sealed class SynchronousFraction(ConsoleUi ui, string stage) : IProgress<double>
    {
        public void Report(double value) => ui.Progress(stage, value);
    }

    private sealed class SynchronousCoverProgress(ConsoleUi ui) : IProgress<CoverProgress>
    {
        public void Report(CoverProgress value) => ui.Progress("Downloading cover art", value.Completed / (double)value.Total);
    }
}
