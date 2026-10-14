using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GameFerry.App.ViewModels;

/// <summary>
/// A game's cover as the lists show it. Every row for one game shares one of these, and the picture is not
/// fetched until a row that is on screen asks for it.
/// </summary>
public sealed class CoverImage : ObservableObject
{
    private readonly CoverImageLoader _loader;
    private ImageSource? _thumbnail;
    private string? _file;
    private bool _requested;

    internal CoverImage(CoverImageLoader loader, string gameId)
    {
        _loader = loader;
        GameId = gameId;
    }

    public string GameId { get; }

    /// <summary>The cover at the size of a list row; null while it is being fetched, and when the game has none.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            // Rows are only bound once they scroll into view, so being read is what says the cover is wanted.
            if (!_requested)
            {
                _requested = true;
                _loader.Request(this);
            }

            return _thumbnail;
        }
    }

    /// <summary>Reads the cover at its full size from the cache; null when there is none.</summary>
    public ImageSource? LoadFullSize() => _file is null ? null : CoverImageLoader.TryDecode(_file, pixelHeight: null);

    internal void Show(ImageSource? thumbnail, string? file)
    {
        _thumbnail = thumbnail;
        _file = file;
        OnPropertyChanged(nameof(Thumbnail));
    }

    /// <summary>Lets the cover be asked for again, after a request that was put off.</summary>
    internal void Forget() => _requested = false;

    /// <summary>Has any row that is showing this cover ask for it again.</summary>
    internal void Prompt() => OnPropertyChanged(nameof(Thumbnail));
}
