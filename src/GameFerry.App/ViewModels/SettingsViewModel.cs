using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameFerry.Core;
using GameFerry.Core.Covers;
using GameFerry.Core.Library;
using GameFerry.Core.NKit;
using GameFerry.Core.Transfer;
using GameFerry.Engine.Disc;
using Microsoft.Win32;

namespace GameFerry.App.ViewModels;

/// <summary>A labelled choice for a combo box.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Editable copy of the settings; nothing changes until <see cref="ApplyTo"/> is called.</summary>
public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NKitStatus))]
    private string _nKitFolder;

    [ObservableProperty]
    private string _workFolder;

    [ObservableProperty]
    private Choice<PartitionSelection> _partitions;

    [ObservableProperty]
    private Choice<SplitMode> _split;

    [ObservableProperty]
    private Choice<TitleSource> _folderNaming;

    [ObservableProperty]
    private bool _verifyAfterWrite;

    [ObservableProperty]
    private bool _nKitFullVerify;

    [ObservableProperty]
    private bool _prepareAhead;

    [ObservableProperty]
    private Choice<CoverLoaderChoice> _coverLoader;

    [ObservableProperty]
    private bool _downloadCoversAfterTransfer;

    [ObservableProperty]
    private string _coverRoot;

    [ObservableProperty]
    private bool _showCoverArt;

    public SettingsViewModel(AppSettings settings)
    {
        _prepareAhead = settings.PrepareAhead;
        _coverLoader = CoverChoices.First(c => c.Value == settings.CoverLoader);
        _downloadCoversAfterTransfer = settings.DownloadCoversAfterTransfer;
        _coverRoot = settings.CoverRoot ?? string.Empty;
        _showCoverArt = settings.ShowCoverArt;
        _nKitFolder = settings.NKitFolder;
        _workFolder = settings.WorkFolder;
        _partitions = PartitionChoices.First(c => c.Value == settings.Partitions);
        _split = SplitChoices.First(c => c.Value == settings.Split);
        _folderNaming = TitleChoices.First(c => c.Value == settings.TitleSource);
        _verifyAfterWrite = settings.VerifyAfterWrite;
        _nKitFullVerify = settings.NKitFullVerify;
    }

    public IReadOnlyList<Choice<PartitionSelection>> PartitionChoices { get; } =
    [
        new(PartitionSelection.All, "Keep everything (update, game, channels)"),
        new(PartitionSelection.RemoveUpdate, "Remove the update partition (saves about 150 MB per game)"),
        new(PartitionSelection.GameOnly, "Game partition only (drops the extra channels and demos some discs carry)"),
    ];

    public IReadOnlyList<Choice<SplitMode>> SplitChoices { get; } =
    [
        new(SplitMode.Auto, "Automatic (split at 4 GB only where the drive needs it, e.g. FAT32)"),
        new(SplitMode.Always, "Always split at 4 GB"),
        new(SplitMode.Never, "Never split"),
    ];

    public IReadOnlyList<Choice<TitleSource>> TitleChoices { get; } =
    [
        new(TitleSource.GameTdb, "GameTDB title (Example Game Deluxe)"),
        new(TitleSource.Disc, "Title stored on the disc (EXAMPLE DX)"),
        new(TitleSource.FileName, "Source file name (Example Game Deluxe (USA) (En,Fr,Es) (Rev 1))"),
    ];

    public IReadOnlyList<Choice<CoverLoaderChoice>> CoverChoices { get; } =
    [
        new(CoverLoaderChoice.Auto, "Automatic (for whichever loaders are found on the drive)"),
        new(CoverLoaderChoice.WiiFlow, @"WiiFlow  —  wiiflow\boxcovers and wiiflow\covers"),
        new(CoverLoaderChoice.UsbLoaderGx, @"USB Loader GX  —  apps\usbloader_gx\images"),
        new(CoverLoaderChoice.ConfigurableUsbLoader, @"Configurable USB Loader  —  usb-loader\covers"),
        new(CoverLoaderChoice.All, "All three loaders"),
        new(CoverLoaderChoice.None, "Don't download cover art"),
    ];

    /// <summary>What the chosen NKit folder actually provides.</summary>
    public string NKitStatus
    {
        get
        {
            if (!Directory.Exists(NKitFolder))
                return "Folder not found. NKit images whose update partition was removed can't be rebuilt without its Recovery files.";

            NKitData data = NKitLocator.Locate(NKitFolder);
            List<string> found = [];
            if (data.RedumpDat is not null)
                found.Add("Redump database");
            if (data.GameTdb is not null)
                found.Add("GameTDB titles");
            if (data.RecoveryDirectory is not null)
                found.Add("Recovery files");
            return found.Count == 0
                ? "No Dats or Recovery folders were found in this folder."
                : "Found: " + string.Join(", ", found) + (data.RecoveryDirectory is null ? ". Recovery files are missing." : ".");
        }
    }

    [RelayCommand]
    private void BrowseNKitFolder()
    {
        if (PickFolder("Select the NKit folder (the one containing Dats and Recovery)", NKitFolder) is { } folder)
            NKitFolder = folder;
    }

    [RelayCommand]
    private void BrowseWorkFolder()
    {
        if (PickFolder("Select a local folder for temporary files (needs up to 8 GB free)", WorkFolder) is { } folder)
            WorkFolder = folder;
    }

    [RelayCommand]
    private void BrowseCoverRoot()
    {
        if (PickFolder("Select the drive or folder the loader is on (the one containing wiiflow, apps or usb-loader)", CoverRoot) is { } folder)
            CoverRoot = folder;
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.CoverLoader = CoverLoader.Value;
        settings.DownloadCoversAfterTransfer = DownloadCoversAfterTransfer;
        settings.CoverRoot = string.IsNullOrWhiteSpace(CoverRoot) ? null : CoverRoot.Trim();
        settings.ShowCoverArt = ShowCoverArt;
        settings.NKitFolder = NKitFolder.Trim();
        settings.WorkFolder = string.IsNullOrWhiteSpace(WorkFolder) ? AppPaths.DefaultWorkFolder : WorkFolder.Trim();
        settings.Partitions = Partitions.Value;
        settings.Split = Split.Value;
        settings.TitleSource = FolderNaming.Value;
        settings.VerifyAfterWrite = VerifyAfterWrite;
        settings.NKitFullVerify = NKitFullVerify;
        settings.PrepareAhead = PrepareAhead;
    }

    internal static string? PickFolder(string title, string? initial)
    {
        OpenFolderDialog dialog = new() { Title = title };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
            dialog.InitialDirectory = initial;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
