using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameFerry.Core;
using GameFerry.Core.Archives;
using GameFerry.Core.Covers;
using GameFerry.Core.Library;
using GameFerry.Core.NKit;
using GameFerry.Core.Transfer;
using GameFerry.Engine.Library;
using GameFerry.Engine.Transfer;

namespace GameFerry.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int MaxLogLength = 200_000;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly NKitHostClient? _host = NKitHostClient.TryCreate();
    private readonly ScanCache _cache = new(AppPaths.ScanCacheFile);
    private readonly CoverImageLoader _covers = new(new CoverCache(AppPaths.CoverCacheFolder, CoverDownloader.CreateHttpClient()));
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly StringBuilder _log = new();

    private GameTitles _titles = GameTitles.Empty;
    private TargetLibrary? _target;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _transferCancellation;
    private int _targetRefreshVersion;
    private bool _initialized;
    private bool _summaryUpdatePending;
    private bool _copyingGames;

    [ObservableProperty]
    private string _sourceFolder = string.Empty;

    [ObservableProperty]
    private bool _includeSubfolders;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private bool _showOtherFiles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameRowHeight))]
    private bool _showCovers;

    [ObservableProperty]
    private TargetDrive? _selectedDrive;

    [ObservableProperty]
    private string _targetPath = string.Empty;

    [ObservableProperty]
    private string _targetInfo = "Choose a drive or folder to copy games to.";

    [ObservableProperty]
    private double _targetUsedPercent;

    [ObservableProperty]
    private string _targetSpaceText = string.Empty;

    [ObservableProperty]
    private string _selectionSummary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTargetGameCommand))]
    private TargetGameRow? _selectedTargetGame;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartTransferCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelTransferCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteTargetGameCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCoversCommand))]
    private bool _isTransferring;

    [ObservableProperty]
    private string _progressText = "Ready.";

    [ObservableProperty]
    private double _stagePercent;

    [ObservableProperty]
    private double _overallPercent;

    [ObservableProperty]
    private string _timeText = string.Empty;

    [ObservableProperty]
    private string _logText = string.Empty;

    public MainViewModel()
    {
        SourcesView = CollectionViewSource.GetDefaultView(Sources);
        SourcesView.Filter = row => MatchesFilter((SourceItemViewModel)row);

        // Rows are filtered again as each file is inspected, without re-filtering the whole list every time.
        if (SourcesView is ICollectionViewLiveShaping { CanChangeLiveFiltering: true } live)
        {
            live.LiveFilteringProperties.Add(nameof(SourceItemViewModel.CanTransfer));
            live.IsLiveFiltering = true;
        }

        ShowCovers = _settings.ShowCoverArt;
    }

    /// <summary>Height of a row in both game lists: tall enough for a cover when covers are shown.</summary>
    public double GameRowHeight => ShowCovers ? 48 : 34;

    public ObservableCollection<SourceItemViewModel> Sources { get; } = [];

    public ICollectionView SourcesView { get; }

    public ObservableCollection<TargetDrive> Drives { get; } = [];

    public ObservableCollection<TargetGameRow> TargetGames { get; } = [];

    public bool IsIdle => !IsTransferring;

    /// <summary>Loads the last-used source and target (or the ones given) and starts scanning.</summary>
    public async Task InitializeAsync(string? sourceOverride, string? targetOverride)
    {
        if (_host is null)
            AddLog($"{NKitHostClient.UnavailableMessage} Archives and NKit images can't be opened without it.");

        IncludeSubfolders = _settings.IncludeSubfolders;
        SourceFolder = sourceOverride ?? _settings.LastSourceFolder ?? string.Empty;
        RefreshDrives();

        // With nothing remembered, a plugged-in card or stick is the likeliest place to copy to.
        string? target = targetOverride ?? _settings.LastTarget
            ?? Drives.FirstOrDefault(d => d.Type == DriveType.Removable)?.Root;
        if (!string.IsNullOrEmpty(target))
        {
            SelectedDrive = Drives.FirstOrDefault(d => SamePath(d.Root, target));
            TargetPath = target;
        }

        _initialized = true;
        _titles = await Task.Run(() => GameTitles.Load(NKitLocator.Locate(_settings.NKitFolder).GameTdb));
        await Task.WhenAll(RefreshTargetAsync(), RefreshSourcesAsync());
    }

    /// <summary>True while a game may be half-written; the window asks before closing.</summary>
    public bool HasWorkInProgress => _copyingGames;

    public void CancelEverything()
    {
        _scanCancellation?.Cancel();
        _transferCancellation?.Cancel();
    }

    /// <summary>
    /// Re-reads the list of drives. If a drive was plugged in or removed, the target is re-read too, so a card
    /// that comes back is picked up again without having to be reselected.
    /// </summary>
    public void RefreshDrives()
    {
        List<TargetDrive> drives = TargetLibrary.ListDrives();
        if (drives.SequenceEqual(Drives))
            return;

        bool sameDrives = drives.Select(d => d.Root).SequenceEqual(Drives.Select(d => d.Root));
        Drives.Clear();
        foreach (TargetDrive drive in drives)
            Drives.Add(drive);
        SelectedDrive = Drives.FirstOrDefault(d => SamePath(d.Root, TargetPath));

        if (!sameDrives && _initialized && !IsTransferring)
            _ = RefreshTargetAsync();
    }

    [RelayCommand]
    private void BrowseSource()
    {
        if (SettingsViewModel.PickFolder("Select the folder containing your games", SourceFolder) is { } folder)
        {
            SourceFolder = folder;
            _ = RefreshSourcesAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshSourcesAsync()
    {
        _scanCancellation?.Cancel();
        CancellationTokenSource cancellation = _scanCancellation = new CancellationTokenSource();

        foreach (SourceItemViewModel row in Sources)
            row.PropertyChanged -= OnRowChanged;
        Sources.Clear();
        UpdateSelectionSummary();

        string folder = SourceFolder.Trim();
        if (folder.Length == 0)
            return;
        if (!Directory.Exists(folder) && !File.Exists(folder))
        {
            AddLog($"Source folder not found: {folder}");
            return;
        }

        _settings.LastSourceFolder = folder;
        _settings.IncludeSubfolders = IncludeSubfolders;
        SaveSettings();

        IsScanning = true;
        try
        {
            bool recurse = IncludeSubfolders;
            List<SourceItem> items = await Task.Run(() => SourceScanner.Enumerate(folder, recurse), cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;

            Dictionary<SourceItem, SourceItemViewModel> rows = [];
            foreach (SourceItem item in items)
            {
                SourceItemViewModel row = new(item, id => _titles.Find(id), _covers.For);
                row.PropertyChanged += OnRowChanged;
                rows[item] = row;
                Sources.Add(row);
            }

            SourceScanner scanner = new(_host, _cache);
            await scanner.InspectAsync(
                items,
                item => _dispatcher.BeginInvoke(() =>
                {
                    SourceItemViewModel row = rows[item];
                    row.Refresh();
                    row.OnTarget = IsOnTarget(row);
                }),
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog($"Could not read {folder}: {ex.Message}");
        }
        finally
        {
            if (_scanCancellation == cancellation)
            {
                IsScanning = false;
                UpdateSelectionSummary();
            }
        }
    }

    [RelayCommand]
    private void SelectAll() => SetSelection(row => !row.OnTarget);

    [RelayCommand]
    private void SelectNone() => SetSelection(_ => false);

    [RelayCommand]
    private void BrowseTarget()
    {
        if (SettingsViewModel.PickFolder("Select the drive or folder to copy games to", TargetPath) is { } folder)
        {
            SelectedDrive = Drives.FirstOrDefault(d => SamePath(d.Root, folder));
            TargetPath = folder;
        }
    }

    [RelayCommand]
    private async Task RefreshTargetAsync()
    {
        int version = ++_targetRefreshVersion;
        RefreshDrives();

        string path = TargetPath.Trim();
        TargetLibrary? target = null;
        List<InstalledGame> games = [];
        string? problem = null;
        if (path.Length > 0)
        {
            try
            {
                if (!Directory.Exists(path))
                    throw new DirectoryNotFoundException($"{path} is not available.");

                target = TargetLibrary.Open(path);
                games = await Task.Run(target.Scan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                target = null;
                problem = ex.Message;
            }
        }

        // A newer refresh was started while this one was reading the drive.
        if (version != _targetRefreshVersion)
            return;

        _target = target;
        _covers.SetDevice(target is null ? null : CoverDestination.RootFor(_settings.CoverRoot, target), games.Select(g => g.Id6));
        TargetGames.Clear();
        foreach (InstalledGame game in games)
            TargetGames.Add(new TargetGameRow(game, _titles.Find(game.Id6) ?? game.Title, _covers.For(game.Id6)));

        if (target is null)
        {
            TargetInfo = problem ?? "Choose a drive or folder to copy games to.";
            TargetSpaceText = string.Empty;
            TargetUsedPercent = 0;
        }
        else
        {
            string fileSystem = target.FileSystem.Length == 0 ? "unknown file system" : target.FileSystem;
            string splitting = target.SplitSizeFor(_settings.Split) > 0 ? "files are split at 4 GB" : "no splitting needed";
            string layout = target.StoresGamesDirectly ? "  ·  no wbfs folder: games sit directly in this folder" : string.Empty;
            TargetInfo = $"{target.GamesRoot}  ·  {fileSystem}, {splitting}{layout}";
            if (target.TotalBytes > 0)
            {
                TargetUsedPercent = 100.0 * (target.TotalBytes - target.FreeBytes) / target.TotalBytes;
                TargetSpaceText = $"{TargetLibrary.FormatSize(target.FreeBytes)} free of {TargetLibrary.FormatSize(target.TotalBytes)}"
                    + $"  ·  {games.Count} game(s), {TargetLibrary.FormatSize(games.Sum(g => g.Size))}";
            }
            else
            {
                TargetUsedPercent = 0;
                TargetSpaceText = $"{games.Count} game(s), {TargetLibrary.FormatSize(games.Sum(g => g.Size))}";
            }

            _settings.LastTarget = path;
            SaveSettings();
        }

        foreach (SourceItemViewModel row in Sources)
            row.OnTarget = IsOnTarget(row);
        UpdateSelectionSummary();
        StartTransferCommand.NotifyCanExecuteChanged();
        DownloadCoversCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteTargetGame))]
    private async Task DeleteTargetGameAsync()
    {
        if (SelectedTargetGame is not { Game: { } game } || _target is not { } target)
            return;

        MessageBoxResult answer = MessageBox.Show(
            $"Delete {SelectedTargetGame.Title} [{game.Id6}] ({TargetLibrary.FormatSize(game.Size)}) from the target?\n\n{game.Folder}",
            "Delete game",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            await Task.Run(() => target.Delete(game));
            AddLog($"Deleted {game.Title} [{game.Id6}] from {target.GamesRoot}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog($"Could not delete {game.Title}: {ex.Message}");
        }

        await RefreshTargetAsync();
    }

    private bool CanDeleteTargetGame() => SelectedTargetGame is not null && !IsTransferring;

    [RelayCommand(CanExecute = nameof(CanStartTransfer))]
    private async Task StartTransferAsync()
    {
        if (_target is not { } target)
            return;

        List<SourceItemViewModel> rows = [.. Sources.Where(r => r.IsSelected && r.CanTransfer)];
        if (rows.Count == 0)
        {
            MessageBox.Show("Tick the games you want to copy first.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CancellationTokenSource cancellation = _transferCancellation = new CancellationTokenSource();
        IsTransferring = true;
        _copyingGames = true;
        OverallPercent = 0;
        StagePercent = 0;
        foreach (SourceItemViewModel row in rows)
            row.Status = "Queued";

        Dictionary<SourceItem, SourceItemViewModel> byItem = rows.ToDictionary(r => r.Item);
        TransferEstimate estimate = new([.. rows.Select(r => r.Item.EstimateWbfsSize(_settings.Partitions))], _settings.VerifyAfterWrite);
        Stopwatch elapsed = Stopwatch.StartNew();
        Progress<TransferProgress> progress = new(p => ShowProgress(p, byItem[p.Item], estimate, elapsed.Elapsed));
        TransferEngine engine = new(_settings, _host, SevenZip.TryLocate(), _titles, (message, detail) =>
        {
            if (!detail)
                AddLog(message);
        });

        AddLog($"Transferring {rows.Count} game(s) to {target.GamesRoot}");
        try
        {
            List<TransferResult> results = await engine.RunAsync([.. rows.Select(r => r.Item)], target, overwrite: false, progress, cancellation.Token);
            int done = results.Count(r => r.Outcome == TransferStage.Done);
            int failed = results.Count(r => r.Outcome == TransferStage.Failed);
            int skipped = results.Count(r => r.Outcome == TransferStage.Skipped);
            int cancelled = results.Count(r => r.Outcome == TransferStage.Cancelled);
            string summary = $"Finished in {FormatDuration(elapsed.Elapsed)}: {done} transferred, {skipped} skipped, {failed} failed"
                + (cancelled > 0 ? $", {cancelled} cancelled" : string.Empty)
                + $"; {TargetLibrary.FormatSize(results.Sum(r => r.BytesWritten))} written.";
            AddLog(summary);
            ProgressText = summary;
            _copyingGames = false;

            List<string> copied = [.. results.Where(r => r.Outcome == TransferStage.Done).Select(r => r.Item.Id6!)];
            if (_settings.DownloadCoversAfterTransfer && copied.Count > 0 && !cancellation.IsCancellationRequested)
            {
                TimeText = string.Empty;
                if (await FetchCoversAsync(target, copied, cancellation.Token) is { } covers)
                    ProgressText = $"{summary}  Cover art: {covers}.";
                else
                    ProgressText = summary;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddLog($"Transfer stopped: {ex.Message}");
            ProgressText = "Transfer stopped.";
        }
        finally
        {
            _copyingGames = false;
            IsTransferring = false;
            TimeText = string.Empty;
            await RefreshTargetAsync();
        }
    }

    private bool CanStartTransfer() => !IsTransferring && _target is not null;

    [RelayCommand(CanExecute = nameof(CanDownloadCovers))]
    private async Task DownloadCoversAsync()
    {
        if (_target is not { } target)
            return;

        CancellationTokenSource cancellation = _transferCancellation = new CancellationTokenSource();
        IsTransferring = true;
        try
        {
            await FetchCoversAsync(target, [.. TargetGames.Select(g => g.Id6)], cancellation.Token);
        }
        finally
        {
            IsTransferring = false;
        }
    }

    private bool CanDownloadCovers() => !IsTransferring && _target is not null && TargetGames.Count > 0;

    /// <summary>
    /// Downloads whatever cover art is missing for the given games. Returns the summary, or null if nothing
    /// could be done (the reason is logged).
    /// </summary>
    private async Task<CoverSummary?> FetchCoversAsync(TargetLibrary target, IReadOnlyCollection<string> gameIds, CancellationToken cancellationToken)
    {
        CoverDestination? destination = CoverDestination.Resolve(_settings.CoverLoader, _settings.CoverRoot, target, out string? reason);
        if (destination is null)
        {
            AddLog($"Cover art: {reason}");
            ProgressText = $"Cover art: {reason}";
            return null;
        }

        AddLog($"Downloading cover art for {gameIds.Count} game(s) to {destination.Root} ({string.Join(", ", destination.Layouts.Select(l => l.Name))})");
        StagePercent = 0;
        OverallPercent = 0;

        // Progress reports are queued to this thread and can still arrive after the download has returned.
        bool finished = false;
        Progress<CoverProgress> progress = new(p =>
        {
            if (finished)
                return;

            StagePercent = OverallPercent = 100.0 * p.Completed / p.Total;
            ProgressText = $"Downloading cover art  —  {p.Completed} of {p.Total}";
        });

        try
        {
            using HttpClient http = CoverDownloader.CreateHttpClient();
            CoverSummary summary = await new CoverDownloader(http).DownloadAsync(gameIds, destination, overwrite: false, progress, cancellationToken);
            finished = true;

            AddLog($"Cover art: {summary}.");
            if (summary.GamesWithoutArt.Count > 0)
            {
                AddLog($"  GameTDB has no art for: {string.Join(", ", summary.GamesWithoutArt)}");
                AddLog($"  Anyone can add the missing covers at {GameTdbArt.Site}.");
            }
            StagePercent = OverallPercent = 100;
            ProgressText = $"Cover art: {summary}.";
            return summary;
        }
        catch (OperationCanceledException)
        {
            finished = true;
            AddLog("Cover art download cancelled.");
            ProgressText = "Cover art download cancelled.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            finished = true;
            AddLog($"Cover art download stopped: {ex.Message}");
            ProgressText = "Cover art download stopped.";
            return null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsTransferring))]
    private void CancelTransfer()
    {
        _transferCancellation?.Cancel();
        ProgressText = "Cancelling: removing the unfinished game…";
    }

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsViewModel viewModel = new(_settings);
        SettingsWindow window = new() { DataContext = viewModel, Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true)
            return;

        string previousNKitFolder = _settings.NKitFolder;
        viewModel.ApplyTo(_settings);
        SaveSettings();
        ShowCovers = _settings.ShowCoverArt;

        if (!string.Equals(previousNKitFolder, _settings.NKitFolder, StringComparison.OrdinalIgnoreCase))
        {
            _titles = GameTitles.Load(NKitLocator.Locate(_settings.NKitFolder).GameTdb);
            foreach (SourceItemViewModel row in Sources)
                row.Refresh();
        }

        _ = RefreshTargetAsync();
    }

    partial void OnSelectedDriveChanged(TargetDrive? value)
    {
        if (value is not null)
            TargetPath = value.Root;
    }

    partial void OnTargetPathChanged(string value)
    {
        if (_initialized)
            _ = RefreshTargetAsync();
    }

    partial void OnFilterChanged(string value) => SourcesView.Refresh();

    partial void OnShowOtherFilesChanged(bool value)
    {
        SourcesView.Refresh();
        UpdateSelectionSummary();
    }

    partial void OnShowCoversChanged(bool value) => _covers.Enabled = value;

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        if (_initialized)
            _ = RefreshSourcesAsync();
    }

    private void ShowProgress(TransferProgress progress, SourceItemViewModel row, TransferEstimate estimate, TimeSpan elapsed)
    {
        estimate.Report(progress, elapsed);

        // The next game being prepared behind the current one only shows in its own row.
        if (progress.Background)
        {
            row.Status = $"Preparing {(int)(progress.Fraction * 100)}% (next)";
            return;
        }

        string position = $"Game {progress.Index + 1} of {progress.Count}: {row.Title}";
        switch (progress.Stage)
        {
            case TransferStage.Done:
                row.Status = "Done";
                row.IsSelected = false;
                break;
            case TransferStage.Skipped:
                row.Status = progress.Message ?? "Skipped";
                break;
            case TransferStage.Failed:
                row.Status = "Failed: " + progress.Message;
                break;
            case TransferStage.Cancelled:
                row.Status = "Cancelled";
                break;
            default:
                string stage = progress.Stage switch
                {
                    TransferStage.Extracting => "Extracting",
                    TransferStage.Converting => "Rebuilding ISO",
                    TransferStage.Analyzing => "Analysing",
                    TransferStage.Writing => "Writing",
                    TransferStage.Verifying => "Verifying",
                    _ => progress.Stage.ToString(),
                };
                int percent = (int)(progress.Fraction * 100);
                row.Status = $"{stage} {percent}%";
                StagePercent = progress.Fraction * 100;

                // How fast the drive is going explains the time left better than anything else.
                string speed = estimate.BytesPerSecond is { } bytesPerSecond ? $" at {TargetLibrary.FormatSize((long)bytesPerSecond)}/s" : string.Empty;
                if (!(_transferCancellation?.IsCancellationRequested ?? false))
                    ProgressText = $"{position}  —  {stage} {percent}%{speed}";
                break;
        }

        // The overall bar and the time left both count what has been written to the target and read back,
        // which is what takes the time; they cover every game in the queue, not just the current one.
        OverallPercent = estimate.Fraction * 100;
        string games = progress.Count > 1 ? $" for all {progress.Count} games" : string.Empty;
        TimeText = estimate.Remaining(elapsed) is { } left && left > TimeSpan.Zero
            ? $"{FormatDuration(elapsed)} elapsed, about {FormatDuration(left)} left{games}"
            : $"{FormatDuration(elapsed)} elapsed";
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        // An empty name means "everything changed" (the row has just been inspected).
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(SourceItemViewModel.IsSelected) or nameof(SourceItemViewModel.OnTarget))
        {
            RequestSummaryUpdate();
        }
    }

    /// <summary>
    /// Recomputes the summary once the current burst of row changes has been applied. Rows change one at a time
    /// (ticking "select all", a scan filling in a large folder), and the summary only needs the end state.
    /// </summary>
    private void RequestSummaryUpdate()
    {
        if (_summaryUpdatePending)
            return;

        _summaryUpdatePending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _summaryUpdatePending = false;
            UpdateSelectionSummary();
        });
    }

    private void UpdateSelectionSummary()
    {
        List<SourceItemViewModel> selected = [.. Sources
            .Where(r => r.IsSelected && r.CanTransfer)
            .DistinctBy(r => r.Id, StringComparer.OrdinalIgnoreCase)];
        if (selected.Count == 0)
        {
            List<SourceItemViewModel> games = [.. Sources.Where(r => r.CanTransfer).DistinctBy(r => r.Id, StringComparer.OrdinalIgnoreCase)];
            int others = Sources.Count(r => r.Item.Inspected && !r.CanTransfer);
            string otherFiles = others == 0 ? string.Empty
                : ShowOtherFiles ? $"  {others} other file(s) can't be copied."
                : $"  {others} other file(s) hidden.";
            SelectionSummary = Sources.Count == 0 ? string.Empty
                : $"{games.Count} game(s), {games.Count(r => !r.OnTarget)} not yet on the target.{otherFiles}";
            return;
        }

        long needed = selected.Sum(r => r.Item.EstimateWbfsSize(_settings.Partitions));
        string fit = _target is null || _target.TotalBytes == 0 ? string.Empty
            : needed <= _target.FreeBytes ? "  —  fits"
            : $"  —  does NOT fit ({TargetLibrary.FormatSize(needed - _target.FreeBytes)} too much)";
        SelectionSummary = $"{selected.Count} game(s) selected, about {TargetLibrary.FormatSize(needed)} needed{fit}";
    }

    private void SetSelection(Func<SourceItemViewModel, bool> choose)
    {
        // Only rows the filter currently shows are touched, so "select all" follows the search box.
        foreach (SourceItemViewModel row in SourcesView.Cast<SourceItemViewModel>())
            row.IsSelected = row.CanTransfer && choose(row);
    }

    private bool MatchesFilter(SourceItemViewModel row)
    {
        // Files that turned out not to be Wii discs (WiiWare, DLC, manuals) stay out of the way unless asked for.
        if (!ShowOtherFiles && row.Item.Inspected && !row.CanTransfer)
            return false;

        string filter = Filter.Trim();
        return filter.Length == 0
            || row.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Id.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsOnTarget(SourceItemViewModel row) =>
        row.Id.Length > 0 && TargetGames.Any(g => string.Equals(g.Id6, row.Id, StringComparison.OrdinalIgnoreCase));

    private void AddLog(string message)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => AddLog(message));
            return;
        }

        // Keep the log bounded on very long runs by dropping its older half.
        if (_log.Length > MaxLogLength)
        {
            int cut = _log.ToString().IndexOf('\n', MaxLogLength / 2);
            _log.Remove(0, cut + 1);
        }

        _log.Append($"{DateTime.Now:HH:mm:ss}  {message}").AppendLine();
        LogText = _log.ToString();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog($"Could not save settings: {ex.Message}");
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m"
        : duration.TotalMinutes >= 1 ? $"{duration.Minutes}m {duration.Seconds:00}s" : $"{duration.Seconds}s";
}
