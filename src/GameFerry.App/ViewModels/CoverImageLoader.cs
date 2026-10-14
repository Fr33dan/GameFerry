using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameFerry.Core.Covers;
using GameFerry.Engine.Disc;

namespace GameFerry.App.ViewModels;

/// <summary>
/// Supplies the covers shown in the game lists. They are fetched in the background, a few at a time, and
/// only for games whose rows have been scrolled into view.
/// </summary>
public sealed class CoverImageLoader
{
    private const int MaxParallel = 4;

    // Twice the height a thumbnail is drawn at, so that it stays sharp on a high-DPI screen.
    private const int ThumbnailPixelHeight = 84;

    // Covers asked for within this long of each other belong to the same screenful of rows.
    private const int BatchGapMilliseconds = 200;

    private readonly CoverCache _cache;
    private readonly Dictionary<string, CoverImage> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CoverImage> _waiting = new();
    private readonly HashSet<CoverImage> _putOff = [];
    private readonly DispatcherTimer _retryTimer;
    private LinkedListNode<CoverImage>? _batchEnd;
    private long _lastRequest;
    private int _running;
    private bool _enabled;
    private string? _deviceRoot;
    private HashSet<string> _onDevice = new(StringComparer.OrdinalIgnoreCase);

    public CoverImageLoader(CoverCache cache)
    {
        _cache = cache;

        // The cache refuses to ask GameTDB again until its own delay has passed.
        _retryTimer = new DispatcherTimer { Interval = CoverCache.RetryDelay + TimeSpan.FromSeconds(2) };
        _retryTimer.Tick += (_, _) =>
        {
            _retryTimer.Stop();
            PromptRows();
        };
    }

    /// <summary>Whether covers are fetched at all. Switching this off leaves the ones already loaded alone.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;

            _enabled = value;
            if (value)
            {
                PromptRows();
                return;
            }

            // Nothing more is started; what was waiting is asked for again if covers are switched back on.
            foreach (CoverImage image in _waiting)
            {
                image.Forget();
                _putOff.Add(image);
            }

            _waiting.Clear();
            _batchEnd = null;
        }
    }

    /// <summary>The cover of a game, shared by every row showing that game; null if the ID isn't a game ID.</summary>
    public CoverImage? For(string? gameId)
    {
        if (gameId is null || !DiscHeader.IsValidId(gameId))
            return null;

        if (!_images.TryGetValue(gameId, out CoverImage? image))
            _images[gameId] = image = new CoverImage(this, gameId);
        return image;
    }

    /// <summary>
    /// Says which games are on the target and where its loader keeps covers, so that a cover the loader
    /// already has is copied from there rather than downloaded.
    /// </summary>
    public void SetDevice(string? root, IEnumerable<string> gameIds)
    {
        _deviceRoot = root;
        _onDevice = new HashSet<string>(gameIds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads an image file, scaled to the given height or at its own size. The whole file is read at once,
    /// so it is not left open. Returns null if it can't be read as an image.
    /// </summary>
    internal static ImageSource? TryDecode(string file, int? pixelHeight)
    {
        try
        {
            using FileStream stream = File.OpenRead(file);
            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            if (pixelHeight is { } height)
                bitmap.DecodePixelHeight = height;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // WPF reports a damaged image in more ways than can usefully be listed.
            return null;
        }
    }

    /// <summary>Queues a cover to be fetched, or puts it off while covers are switched off.</summary>
    internal void Request(CoverImage image)
    {
        if (!_enabled)
        {
            image.Forget();
            _putOff.Add(image);
            return;
        }

        // A screenful of rows is filled from the top down, and ahead of anything asked for before it:
        // after a long scroll, the rows now on screen are the ones worth fetching first.
        long now = Environment.TickCount64;
        _batchEnd = _batchEnd is not null && now - _lastRequest < BatchGapMilliseconds
            ? _waiting.AddAfter(_batchEnd, image)
            : _waiting.AddFirst(image);
        _lastRequest = now;
        StartWaiting();
    }

    private void StartWaiting()
    {
        while (_running < MaxParallel && _waiting.First is { } next)
        {
            _waiting.RemoveFirst();
            if (next == _batchEnd)
                _batchEnd = null;

            _running++;
            _ = LoadAsync(next.Value);
        }
    }

    private async Task LoadAsync(CoverImage image)
    {
        string? deviceRoot = _onDevice.Contains(image.GameId) ? _deviceRoot : null;
        try
        {
            Loaded loaded = await Task.Run(() => FetchAsync(image.GameId, deviceRoot));
            if (loaded.Availability == CoverAvailability.Unreachable)
            {
                image.Forget();
                _putOff.Add(image);
                _retryTimer.IsEnabled = true;
            }
            else
            {
                image.Show(loaded.Thumbnail, loaded.File);
            }
        }
        finally
        {
            _running--;
            StartWaiting();
        }
    }

    private async Task<Loaded> FetchAsync(string gameId, string? deviceRoot)
    {
        CachedCover cover = await _cache.GetAsync(gameId, deviceRoot, CancellationToken.None);
        if (cover.File is null)
            return new Loaded(cover.Availability, null, null);

        if (TryDecode(cover.File, ThumbnailPixelHeight) is { } thumbnail)
            return new Loaded(CoverAvailability.Cached, thumbnail, cover.File);

        _cache.Discard(gameId);
        return new Loaded(CoverAvailability.None, null, null);
    }

    /// <summary>
    /// Has the rows whose covers were put off (covers switched off, GameTDB out of reach) ask for them again.
    /// Only rows still on screen do; the others ask when they are next scrolled into view.
    /// </summary>
    private void PromptRows()
    {
        CoverImage[] images = [.. _putOff];
        _putOff.Clear();
        foreach (CoverImage image in images)
            image.Prompt();
    }

    private readonly record struct Loaded(CoverAvailability Availability, ImageSource? Thumbnail, string? File);
}
