using GameFerry.Engine.Disc;

namespace GameFerry.Core.Covers;

/// <summary>What came of asking for a game's cover.</summary>
public enum CoverAvailability
{
    /// <summary>The cover is in the cache.</summary>
    Cached,

    /// <summary>There is no cover: GameTDB has none for the game, or the ID is not a game ID.</summary>
    None,

    /// <summary>The cover could not be fetched this time (no connection, a server error); worth asking for again later.</summary>
    Unreachable,
}

/// <param name="File">The image file, when the cover is cached.</param>
public readonly record struct CachedCover(CoverAvailability Availability, string? File);

/// <summary>
/// Front covers kept on this PC for the game lists to show. A cover is copied from a loader's folder on the
/// target when it is already there, and downloaded from GameTDB otherwise; either way only once.
/// </summary>
public sealed class CoverCache
{
    private const string NoArtExtension = ".none";

    // GameTDB rarely gains art for a game it had none for, so "none" is taken to hold for a good while.
    private static readonly TimeSpan NoArtLifetime = TimeSpan.FromDays(30);

    private readonly string _folder;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private long _retryAtTicks;

    public CoverCache(string folder, HttpClient http, TimeProvider? time = null)
    {
        _folder = folder;
        _http = http;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How long GameTDB is left alone after it could not be reached.</summary>
    public static TimeSpan RetryDelay { get; } = TimeSpan.FromMinutes(1);

    /// <summary>Returns the game's cover, fetching it first if it has not been asked for before.</summary>
    /// <param name="deviceRoot">
    /// Drive or folder whose loader folders may already hold the cover; null to go straight to GameTDB.
    /// </param>
    public async Task<CachedCover> GetAsync(string gameId, string? deviceRoot, CancellationToken cancellationToken)
    {
        if (!DiscHeader.IsValidId(gameId))
            return new CachedCover(CoverAvailability.None, null);

        gameId = gameId.ToUpperInvariant();
        string file = FileFor(gameId);
        try
        {
            if (File.Exists(file))
                return new CachedCover(CoverAvailability.Cached, file);

            if (deviceRoot is not null && await ReadFromDeviceAsync(deviceRoot, gameId, cancellationToken) is { } copy)
            {
                await StoreAsync(file, copy, cancellationToken);
                return new CachedCover(CoverAvailability.Cached, file);
            }

            string noArtMarker = Path.ChangeExtension(file, NoArtExtension);
            DateTimeOffset now = _time.GetUtcNow();
            if (now.UtcDateTime - File.GetLastWriteTimeUtc(noArtMarker) < NoArtLifetime)
                return new CachedCover(CoverAvailability.None, null);
            if (now.UtcTicks < Interlocked.Read(ref _retryAtTicks))
                return new CachedCover(CoverAvailability.Unreachable, null);

            GameTdbImage download = await GameTdbArt.DownloadAsync(_http, CoverType.Front, gameId, cancellationToken);
            if (download.Image is { } image)
            {
                await StoreAsync(file, image, cancellationToken);
                return new CachedCover(CoverAvailability.Cached, file);
            }

            if (!download.Failed)
            {
                await StoreAsync(noArtMarker, [], cancellationToken);
                return new CachedCover(CoverAvailability.None, null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache folder can't be used. That is handled like an outage, so that GameTDB isn't asked
            // for the same cover again every time the game's row comes into view.
        }

        Interlocked.Exchange(ref _retryAtTicks, (_time.GetUtcNow() + RetryDelay).UtcTicks);
        return new CachedCover(CoverAvailability.Unreachable, null);
    }

    /// <summary>
    /// Removes a cached cover that turned out not to be a usable image, so that it is fetched afresh another time.
    /// </summary>
    public void Discard(string gameId)
    {
        if (!DiscHeader.IsValidId(gameId))
            return;

        try
        {
            File.Delete(FileFor(gameId.ToUpperInvariant()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It stays, and goes on not being shown.
        }
    }

    private string FileFor(string gameId) => Path.Combine(_folder, gameId + ".png");

    /// <summary>The game's flat cover from a loader's folder on the drive, if one of the loaders has it.</summary>
    private static async Task<byte[]?> ReadFromDeviceAsync(string deviceRoot, string gameId, CancellationToken cancellationToken)
    {
        IEnumerable<string> files = CoverLayout.All
            .SelectMany(layout => layout.Folders)
            .Where(folder => folder.Type == CoverType.Front)
            .Select(folder => Path.Combine(deviceRoot, folder.RelativePath, gameId + ".png"));
        try
        {
            foreach (string file in files)
            {
                if (!File.Exists(file))
                    continue;

                byte[] image = await File.ReadAllBytesAsync(file, cancellationToken);
                if (GameTdbArt.IsPng(image))
                    return image;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The drive has gone, or the file can't be read. GameTDB still has the cover.
        }

        return null;
    }

    private async Task StoreAsync(string file, byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_folder);

        // Written under another name first, so that half an image is never taken for a cover.
        string temporary = file + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken);
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
