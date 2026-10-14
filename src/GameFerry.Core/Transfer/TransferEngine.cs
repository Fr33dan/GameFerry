using System.Collections.Concurrent;
using System.Diagnostics;
using GameFerry.Core.Archives;
using GameFerry.Core.Disc;
using GameFerry.Core.Library;
using GameFerry.Core.NKit;
using GameFerry.Core.Wbfs;
using GameFerry.Engine.Disc;
using GameFerry.Engine.Library;
using GameFerry.Engine.Transfer;
using GameFerry.Engine.Wbfs;

namespace GameFerry.Core.Transfer;

/// <param name="detail">True for NKit's own verbose output, false for the engine's summary lines.</param>
public delegate void TransferLog(string message, bool detail);

/// <summary>
/// Moves games to a target. Each game is prepared (extracted and rebuilt into a full ISO where needed), then
/// written: padding is dropped, split WBFS files are written under temporary names, read back, and only then
/// given their final names. While one game is being written, the next can already be prepared.
/// </summary>
public sealed class TransferEngine
{
    private const long TargetSpaceMargin = 32L << 20;
    private const long WorkSpaceMargin = 256L << 20;
    private static readonly TimeSpan[] LeftoverRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)];

    private readonly AppSettings _settings;
    private readonly NKitHostClient? _host;
    private readonly SevenZip? _sevenZip;
    private readonly GameTitles _titles;
    private readonly TransferLog? _log;

    /// <param name="host">Runs NKit; without it only plain ISO and WBFS files can be transferred.</param>
    /// <param name="sevenZip">Optional faster archive extraction; without it NKit reads archives itself.</param>
    public TransferEngine(AppSettings settings, NKitHostClient? host, SevenZip? sevenZip, GameTitles titles, TransferLog? log)
    {
        _settings = settings;
        _host = host;
        _sevenZip = sevenZip;
        _titles = titles;
        _log = log;
    }

    /// <summary>Transfers the items in order. A failure or skip of one item doesn't stop the rest.</summary>
    /// <param name="overwrite">Replace games already on the target instead of skipping them.</param>
    public async Task<List<TransferResult>> RunAsync(
        IReadOnlyList<SourceItem> items,
        TargetLibrary target,
        bool overwrite,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        WorkDirectory work = new(_settings.WorkFolder);
        work.CleanStale();

        ConcurrentDictionary<string, InstalledGame> installed = new(StringComparer.OrdinalIgnoreCase);
        foreach (InstalledGame game in target.Scan())
            installed.TryAdd(game.Id6, game);

        Session session = new(target, work, NKitLocator.Locate(_settings.NKitFolder), installed, overwrite, items.Count, progress);
        List<TransferResult> results = [];

        // The next game, being prepared in the background while the current one is written.
        GameRun? ahead = null;
        Task aheadTask = Task.CompletedTask;
        try
        {
            for (int index = 0; index < items.Count; index++)
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                GameRun run;
                if (ahead is not null)
                {
                    run = ahead;
                    ahead = null;
                    run.BringToForeground();
                    await aheadTask;
                }
                else
                {
                    run = new GameRun(items[index], index, _log, foreground: true);
                    await PrepareAsync(run, session, cancellationToken);
                }

                if (run.Result is null)
                {
                    if (index + 1 < items.Count && CanPrepareAhead(items[index + 1], run.Item, session))
                    {
                        ahead = new GameRun(items[index + 1], index + 1, _log, foreground: false);
                        aheadTask = PrepareAsync(ahead, session, cancellationToken);
                    }

                    run.Result = await WriteAsync(run, session, cancellationToken);
                }

                run.ReleaseResources();
                TransferResult result = run.Result with { Elapsed = elapsed.Elapsed };
                results.Add(result);
                if (result.Outcome is TransferStage.Failed or TransferStage.Skipped)
                    run.Log($"  {result.Outcome}: {result.Message}");
                progress?.Report(new TransferProgress(run.Item, index, items.Count, result.Outcome, 1, result.Message));
            }
        }
        finally
        {
            if (ahead is not null)
            {
                await aheadTask;
                ahead.ReleaseResources();
            }
        }

        return results;
    }

    /// <summary>
    /// Decides whether the item should be transferred at all and, if so, gets it into a readable form.
    /// Never throws: an item that ends here has <see cref="GameRun.Result"/> set instead.
    /// </summary>
    private async Task PrepareAsync(GameRun run, Session session, CancellationToken cancellationToken)
    {
        SourceItem item = run.Item;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            run.Log(item.FileName);
            if (!item.CanTransfer)
            {
                run.Result = new TransferResult(item, TransferStage.Skipped, item.Problem ?? "Not a Wii disc image.");
                return;
            }

            if (session.Installed.TryGetValue(item.Id6!, out InstalledGame? existing) && !session.Overwrite)
            {
                run.Result = new TransferResult(item, TransferStage.Skipped, $"Already on the target ({Path.GetFileName(existing.Folder)}).");
                return;
            }

            run.Replaces = existing;
            run.Job = item.NeedsNKit ? session.Work.CreateJob() : null;
            (run.Disc, run.MatchesRedump) = await OpenDiscAsync(run, session, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            run.Result = new TransferResult(item, TransferStage.Cancelled, "Cancelled.");
        }
        catch (Exception ex)
        {
            // One bad game must not take the rest of the queue down with it.
            run.Result = new TransferResult(item, TransferStage.Failed, ex.Message);
        }
    }

    /// <summary>Writes a prepared game to the target. Never throws; the outcome is in the result.</summary>
    private async Task<TransferResult> WriteAsync(GameRun run, Session session, CancellationToken cancellationToken)
    {
        SourceItem item = run.Item;
        try
        {
            return await WritePreparedAsync(run, session, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new TransferResult(item, TransferStage.Cancelled, "Cancelled.");
        }
        catch (Exception ex)
        {
            return new TransferResult(item, TransferStage.Failed, ex.Message);
        }
    }

    private async Task<TransferResult> WritePreparedAsync(GameRun run, Session session, CancellationToken cancellationToken)
    {
        SourceItem item = run.Item;
        string id = item.Id6!;
        IDiscReader disc = run.Disc!;
        TargetLibrary target = session.Target;

        session.Report(run, TransferStage.Analyzing, 0);
        WbfsPlan plan = await Task.Run(() => WbfsWriter.Plan(disc, _settings.Partitions), cancellationToken);
        foreach (string warning in plan.Usage.Warnings)
            run.Log($"  Warning: {warning}");
        run.Log($"  Partitions kept: {string.Join(", ", plan.Usage.KeptPartitions.Select(p => p.TypeName))}; {TargetLibrary.FormatSize(plan.OutputSize)} to write");

        target.Refresh();
        if (target.FreeBytes < plan.OutputSize + TargetSpaceMargin)
        {
            throw new TransferException(
                $"Not enough space on the target: {TargetLibrary.FormatSize(plan.OutputSize)} needed, {TargetLibrary.FormatSize(target.FreeBytes)} free.");
        }

        string folder = Path.Combine(target.GamesRoot, NameBuilder.GameFolderName(id, TitleCandidates(item)));
        bool createdFolder = !Directory.Exists(folder);
        Directory.CreateDirectory(folder);
        SplitFileWriter? output = null;
        try
        {
            output = new(folder, id, target.SplitSizeFor(_settings.Split));

            session.Report(run, TransferStage.Writing, 0, 0, plan.OutputSize);
            DirectProgress<long> writeProgress = new(bytes => session.Report(run, TransferStage.Writing, bytes / (double)plan.OutputSize, bytes, plan.OutputSize));
            byte[] written = await Task.Run(() => WbfsFileWriter.Write(disc, plan, output, writeProgress, cancellationToken), cancellationToken);
            output.Close();

            if (_settings.VerifyAfterWrite)
            {
                session.Report(run, TransferStage.Verifying, 0, 0, plan.OutputSize);
                IReadOnlyList<string> parts = output.TempPaths;
                DirectProgress<long> verifyProgress = new(bytes => session.Report(run, TransferStage.Verifying, bytes / (double)plan.OutputSize, bytes, plan.OutputSize));
                byte[] readBack = await Task.Run(() => WbfsVerifier.HashFiles(parts, verifyProgress, cancellationToken), cancellationToken);
                if (!written.AsSpan().SequenceEqual(readBack))
                {
                    throw new TransferException(
                        "Verification failed: the target returned different data from what was written. The drive may be faulty or not its advertised size.");
                }
            }

            IReadOnlyList<string> files = output.Commit();

            // When replacing a copy stored under a different folder name, the old one goes once the new one is safe.
            if (run.Replaces is { } old && !SamePath(old.Folder, folder))
                target.Delete(old);

            session.Installed[id] = new InstalledGame(id, item.DiscTitle ?? id, files[0], files, plan.OutputSize);
            run.Log($"  Done: {folder} ({TargetLibrary.FormatSize(plan.OutputSize)}{(files.Count > 1 ? $" in {files.Count} files" : string.Empty)})");

            // Fine where it is, but a staging folder's contents are usually headed for a FAT32 card.
            if (files.Count == 1 && plan.OutputSize > WbfsFormat.DefaultSplitSize)
            {
                run.Log($"  Note: {Path.GetFileName(files[0])} is over 4 GB and was written as one file because this drive allows it. "
                    + "It cannot be copied to a FAT32 drive like that; set File splitting to Always if this folder is a stopover on the way to one.");
            }
            return new TransferResult(item, TransferStage.Done)
            {
                BytesWritten = plan.OutputSize,
                Folder = folder,
                MatchesRedump = run.MatchesRedump,
            };
        }
        catch
        {
            // Disposing deletes the temporary parts, which a drive that dropped out for a moment can refuse at first.
            output?.Dispose();
            if (output is { LeftBehind.Count: > 0 })
                await RemoveLeftoversAsync(run, output.LeftBehind);
            if (createdFolder)
                RemoveIfEmpty(folder);
            throw;
        }
        finally
        {
            output?.Dispose();
        }
    }

    /// <summary>
    /// Retries deleting the temporary parts of a failed write, which a card reader resetting or a Remote Desktop
    /// drive reconnecting can refuse at first. Whatever still will not go is named in the log.
    /// </summary>
    private static async Task RemoveLeftoversAsync(GameRun run, IReadOnlyList<string> paths)
    {
        List<string> left = [.. paths];
        foreach (TimeSpan delay in LeftoverRetryDelays)
        {
            await Task.Delay(delay);
            left.RemoveAll(SplitFileWriter.TryDelete);
            if (left.Count == 0)
                return;
        }

        foreach (string path in left)
            run.Log($"  Could not remove the partial copy {path}; delete it once the drive is reachable again.");
    }

    /// <summary>
    /// Gets the item into a form the WBFS writer can read: plain ISO and WBFS files are opened as they are;
    /// anything else is extracted and/or rebuilt into a full ISO in the job folder first.
    /// </summary>
    private async Task<(IDiscReader Disc, bool? MatchesRedump)> OpenDiscAsync(GameRun run, Session session, CancellationToken cancellationToken)
    {
        SourceItem item = run.Item;
        WorkJob? job = run.Job;
        string path = item.Path;
        string? entry = item.EntryName;

        // 7-Zip unpacks an archive far faster than NKit's own reader does; without it NKit reads the archive itself.
        bool extracted = false;
        if (item.IsArchive && _sevenZip is not null && entry is not null && job is not null)
        {
            EnsureWorkSpace(session.Work, item.SourceSize + RebuildSpace(item));
            session.Report(run, TransferStage.Extracting, 0);
            try
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                path = await _sevenZip.ExtractAsync(
                    item.Path, entry, job.Path, new DirectProgress<double>(f => session.Report(run, TransferStage.Extracting, f)), cancellationToken);
                entry = null;
                extracted = true;
                run.Log($"  7-Zip extracted {TargetLibrary.FormatSize(new FileInfo(path).Length)} in {elapsed.Elapsed.TotalSeconds:0}s");
            }
            catch (IOException ex)
            {
                run.Log($"  {ex.Message} Letting NKit read the archive instead.");
            }
        }

        bool isLooseFile = !item.IsArchive || extracted;
        if (isLooseFile && !NeedsRebuild(item))
            return (item.Format == SourceFormat.Wbfs ? WbfsReader.Open(path) : new IsoDiscReader(path), null);

        if (_host is null || job is null)
            throw new TransferException(NKitHostClient.UnavailableMessage);

        EnsureWorkSpace(session.Work, item.ImageSize);
        session.Report(run, TransferStage.Converting, 0);
        List<string> nkitOutput = [];
        NKitJob nkitJob = new(path, entry, Path.Combine(job.Path, item.Id6 + ".iso"), job.Path, session.NKitData, _settings.NKitFullVerify);
        NKitResult result = await _host.ConvertToIsoAsync(
            nkitJob,
            new DirectProgress<NKitProgress>(p => session.Report(run, TransferStage.Converting, p.Total)),
            line =>
            {
                nkitOutput.Add(line);
                run.Log("    " + line, detail: true);
            },
            cancellationToken);

        if (!result.Ok || result.Output is null)
            throw new TransferException(ExplainNKitFailure(result, nkitOutput, session.NKitData));

        // The extracted copy has served its purpose; give its space back before the long write.
        if (extracted)
            File.Delete(path);

        string verdict = result.MatchesRedump
            ? $"verified against Redump ({result.RedumpName})"
            : "checksum matches the NKit header, but the image is not in the Redump database";
        run.Log($"  NKit rebuilt {TargetLibrary.FormatSize(result.OutputSize)} in {result.Seconds:0}s; {verdict}");
        return (new IsoDiscReader(result.Output), result.MatchesRedump);
    }

    /// <summary>
    /// True when the next item can usefully be prepared while the current one is written: it needs preparing,
    /// it is a different game, and the work folder has room for it alongside the current one.
    /// </summary>
    private bool CanPrepareAhead(SourceItem next, SourceItem current, Session session)
    {
        if (!_settings.PrepareAhead || !next.CanTransfer || !next.NeedsNKit)
            return false;

        // A second copy of the game being written must wait to see whether the first one makes it.
        if (string.Equals(next.Id6, current.Id6, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!session.Overwrite && session.Installed.ContainsKey(next.Id6!))
            return false;

        long needed = RebuildSpace(next) + (next.IsArchive && _sevenZip is not null ? next.SourceSize : 0);
        return session.Work.FreeBytes >= needed + WorkSpaceMargin;
    }

    private static bool NeedsRebuild(SourceItem item) => item.Format is not (SourceFormat.Iso or SourceFormat.Wbfs);

    private static long RebuildSpace(SourceItem item) => NeedsRebuild(item) ? item.ImageSize : 0;

    private static void EnsureWorkSpace(WorkDirectory work, long needed)
    {
        if (work.FreeBytes < needed + WorkSpaceMargin)
        {
            throw new TransferException(
                $"Not enough space in the work folder {work.Root}: {TargetLibrary.FormatSize(needed)} needed, {TargetLibrary.FormatSize(work.FreeBytes)} free.");
        }
    }

    private static string ExplainNKitFailure(NKitResult result, List<string> nkitOutput, NKitData data)
    {
        // An NKit image made without its update partition can only be rebuilt with the matching recovery file.
        string? missingUpdate = nkitOutput.FirstOrDefault(line =>
            line.Contains("Update partition", StringComparison.OrdinalIgnoreCase) && line.Contains("missing", StringComparison.OrdinalIgnoreCase));
        if (missingUpdate is not null)
        {
            string where = data.RecoveryDirectory is null
                ? "no Recovery folder was found in the NKit folder set in Settings"
                : $"it is not in {data.RecoveryDirectory}";
            return $"NKit could not rebuild this image because its update partition is needed and {where}. ({missingUpdate.Trim(' ', '|', '!')})";
        }

        if (result.Message is { Length: > 0 } message)
            return "NKit could not rebuild this image: " + message.Trim();

        string? verification = nkitOutput.LastOrDefault(line => line.Contains("Verification Failed", StringComparison.OrdinalIgnoreCase));
        return "NKit could not rebuild this image" + (verification is null ? "." : $": {verification.Trim()}");
    }

    private string?[] TitleCandidates(SourceItem item)
    {
        string fromFile = NameBuilder.TitleFromFileName(item.Path);
        string? fromDatabase = _titles.Find(item.Id6);
        return _settings.TitleSource switch
        {
            TitleSource.Disc => [item.DiscTitle, fromDatabase, fromFile],
            TitleSource.FileName => [fromFile, fromDatabase, item.DiscTitle],
            _ => [fromDatabase, item.DiscTitle, fromFile],
        };
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static void RemoveIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>What every game in one run shares.</summary>
    private sealed record Session(
        TargetLibrary Target,
        WorkDirectory Work,
        NKitData NKitData,
        ConcurrentDictionary<string, InstalledGame> Installed,
        bool Overwrite,
        int Count,
        IProgress<TransferProgress>? Progress)
    {
        public void Report(GameRun run, TransferStage stage, double fraction, long bytes = 0, long totalBytes = 0) =>
            Progress?.Report(new TransferProgress(run.Item, run.Index, Count, stage, fraction)
            {
                Background = !run.IsForeground,
                Bytes = bytes,
                TotalBytes = totalBytes,
            });
    }

    /// <summary>
    /// One game's passage through the engine. A game prepared ahead of its turn holds its log lines back
    /// until it becomes the current game, so the log reads one game at a time.
    /// </summary>
    private sealed class GameRun(SourceItem item, int index, TransferLog? log, bool foreground)
    {
        private readonly Lock _gate = new();
        private readonly List<(string Message, bool Detail)> _held = [];
        private bool _foreground = foreground;

        public SourceItem Item { get; } = item;

        public int Index { get; } = index;

        public WorkJob? Job { get; set; }

        public IDiscReader? Disc { get; set; }

        public bool? MatchesRedump { get; set; }

        /// <summary>The copy already on the target that this transfer replaces.</summary>
        public InstalledGame? Replaces { get; set; }

        /// <summary>Set once the game is finished with, whatever the outcome.</summary>
        public TransferResult? Result { get; set; }

        public bool IsForeground
        {
            get
            {
                lock (_gate)
                    return _foreground;
            }
        }

        public void Log(string message, bool detail = false)
        {
            lock (_gate)
            {
                if (_foreground)
                    log?.Invoke(message, detail);
                else
                    _held.Add((message, detail));
            }
        }

        public void BringToForeground()
        {
            lock (_gate)
            {
                _foreground = true;
                foreach ((string message, bool detail) in _held)
                    log?.Invoke(message, detail);
                _held.Clear();
            }
        }

        public void ReleaseResources()
        {
            Disc?.Dispose();
            Disc = null;
            Job?.Dispose();
            Job = null;
        }
    }
}
