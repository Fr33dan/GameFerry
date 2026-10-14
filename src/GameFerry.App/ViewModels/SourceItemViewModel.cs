using CommunityToolkit.Mvvm.ComponentModel;
using GameFerry.Core.Library;
using GameFerry.Engine.Disc;
using GameFerry.Engine.Library;

namespace GameFerry.App.ViewModels;

/// <summary>One row of the list of games on the target, with the title and cover to show for it.</summary>
public sealed record TargetGameRow(InstalledGame Game, string Title, CoverImage? Cover)
{
    public string Id6 => Game.Id6;

    public long Size => Game.Size;
}

/// <summary>One row of the source list.</summary>
public partial class SourceItemViewModel : ObservableObject
{
    private readonly Func<string?, string?> _findTitle;
    private readonly Func<string?, CoverImage?> _findCover;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _onTarget;

    [ObservableProperty]
    private string _status = string.Empty;

    public SourceItemViewModel(SourceItem item, Func<string?, string?> findTitle, Func<string?, CoverImage?> findCover)
    {
        Item = item;
        _findTitle = findTitle;
        _findCover = findCover;
    }

    public SourceItem Item { get; }

    public string FileName => Item.FileName;

    public string Title => _findTitle(Item.Id6) ?? NameBuilder.TitleFromFileName(Item.Path);

    /// <summary>The game's cover; null for a file that isn't a Wii game, or hasn't been identified yet.</summary>
    public CoverImage? Cover => Item.Kind == DiscKind.Wii ? _findCover(Item.Id6) : null;

    public string Id => Item.Id6 ?? string.Empty;

    public string Format => !Item.Inspected ? "…" : Item.CanTransfer ? Item.FormatLabel : string.Empty;

    /// <summary>Size of the source file, for sorting.</summary>
    public long Size => Item.FileSize;

    public string SizeText => TargetLibrary.FormatSize(Item.FileSize);

    public bool CanTransfer => Item.CanTransfer;

    /// <summary>What to show in the status column when nothing is in progress.</summary>
    public string StatusText => Status.Length > 0 ? Status
        : !Item.Inspected ? "Reading…"
        : Item.Problem is not null ? Item.Problem
        : OnTarget ? "On target"
        : string.Empty;

    /// <summary>Called once the file has been inspected, or the title database has changed.</summary>
    public void Refresh()
    {
        if (!CanTransfer)
            IsSelected = false;
        OnPropertyChanged(string.Empty);
    }

    partial void OnOnTargetChanged(bool value)
    {
        if (value)
            IsSelected = false;
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));
}
