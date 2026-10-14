using GameFerry.Core.Library;

namespace GameFerry.Core.Covers;

/// <summary>Where covers are to go and for which loaders.</summary>
public sealed record CoverDestination(string Root, IReadOnlyList<CoverLayout> Layouts)
{
    /// <summary>
    /// The drive or folder a loader's cover folders are in: the one named in the settings if there is one,
    /// otherwise the one the games are on.
    /// </summary>
    public static string RootFor(string? rootOverride, TargetLibrary target) =>
        string.IsNullOrWhiteSpace(rootOverride) ? target.DeviceRoot : rootOverride.Trim();

    /// <summary>
    /// Works out the destination for a target. Returns null, with the reason, when there is nothing to do:
    /// covers are switched off, the folder isn't there, or no loader could be identified.
    /// </summary>
    /// <param name="rootOverride">Drive or folder holding the loader, when it isn't the one holding the games.</param>
    public static CoverDestination? Resolve(CoverLoaderChoice choice, string? rootOverride, TargetLibrary target, out string? reason)
    {
        reason = null;
        if (choice == CoverLoaderChoice.None)
        {
            reason = "Cover art is switched off in Settings.";
            return null;
        }

        string root = RootFor(rootOverride, target);
        if (!Directory.Exists(root))
        {
            reason = $"The cover art folder {root} is not available.";
            return null;
        }

        IReadOnlyList<CoverLayout> layouts = CoverLayout.For(choice, root);
        if (layouts.Count == 0)
        {
            reason = $@"No loader folder (wiiflow, apps\usbloader_gx or usb-loader) was found in {root}, so there is nowhere to put cover art. "
                + "Choose your loader (Cover art in Settings, or --loader on the command line) to download it anyway.";
            return null;
        }

        return new CoverDestination(root, layouts);
    }
}

/// <param name="Completed">Covers dealt with so far, out of <paramref name="Total"/>.</param>
public readonly record struct CoverProgress(int Completed, int Total, string GameId);

/// <summary>What a cover download did, counted per cover (one game has several).</summary>
/// <param name="Present">Covers that were already there and left alone.</param>
/// <param name="NotFound">Covers GameTDB doesn't have.</param>
/// <param name="Failed">Covers that could not be fetched because of a network or server error.</param>
/// <param name="GamesWithoutArt">IDs of games for which GameTDB had none of the covers asked for.</param>
public sealed record CoverSummary(int Downloaded, int Present, int NotFound, int Failed, IReadOnlyList<string> GamesWithoutArt)
{
    public override string ToString()
    {
        List<string> parts = [$"{Downloaded} downloaded"];
        if (Present > 0)
            parts.Add($"{Present} already there");
        if (NotFound > 0)
            parts.Add($"{NotFound} not on GameTDB");
        if (Failed > 0)
            parts.Add($"{Failed} failed");
        return string.Join(", ", parts);
    }
}

/// <summary>Downloads cover art from GameTDB into the folders loaders read it from.</summary>
public sealed class CoverDownloader
{
    private const int MaxParallelDownloads = 4;

    private readonly HttpClient _http;

    public CoverDownloader(HttpClient http)
    {
        _http = http;
    }

    /// <summary>An HTTP client set up for GameTDB.</summary>
    public static HttpClient CreateHttpClient()
    {
        HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GameFerry/{typeof(CoverDownloader).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        return http;
    }

    /// <summary>
    /// Fetches the covers each layout uses for the given games. Covers already present are kept unless
    /// <paramref name="overwrite"/> is set, and an image needed by several loaders is only downloaded once.
    /// </summary>
    public async Task<CoverSummary> DownloadAsync(
        IEnumerable<string> gameIds,
        CoverDestination destination,
        bool overwrite,
        IProgress<CoverProgress>? progress,
        CancellationToken cancellationToken)
    {
        ILookup<CoverType, string> foldersByType = destination.Layouts
            .SelectMany(layout => layout.Folders)
            .ToLookup(folder => folder.Type, folder => Path.Combine(destination.Root, folder.RelativePath));

        List<Cover> covers = [];
        foreach (string id in gameIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (IGrouping<CoverType, string> folders in foldersByType)
                covers.Add(new Cover(id, folders.Key, [.. folders.Select(folder => Path.Combine(folder, id + ".png"))]));
        }

        int completed = 0;
        Dictionary<Cover, Outcome> outcomes = [];
        ParallelOptions options = new() { MaxDegreeOfParallelism = MaxParallelDownloads, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(covers, options, async (cover, token) =>
        {
            Outcome outcome = await FetchAsync(cover, overwrite, token);
            lock (outcomes)
                outcomes[cover] = outcome;
            progress?.Report(new CoverProgress(Interlocked.Increment(ref completed), covers.Count, cover.GameId));
        });

        List<string> withoutArt = [.. covers
            .GroupBy(cover => cover.GameId, StringComparer.OrdinalIgnoreCase)
            .Where(game => game.All(cover => outcomes[cover] == Outcome.NotFound))
            .Select(game => game.Key)];

        return new CoverSummary(
            outcomes.Values.Count(o => o == Outcome.Downloaded),
            outcomes.Values.Count(o => o == Outcome.Present),
            outcomes.Values.Count(o => o == Outcome.NotFound),
            outcomes.Values.Count(o => o == Outcome.Failed),
            withoutArt);
    }

    private async Task<Outcome> FetchAsync(Cover cover, bool overwrite, CancellationToken cancellationToken)
    {
        List<string> wanted = overwrite ? [.. cover.Files] : [.. cover.Files.Where(file => !File.Exists(file))];
        if (wanted.Count == 0)
            return Outcome.Present;

        GameTdbImage download = await GameTdbArt.DownloadAsync(_http, cover.Type, cover.GameId, cancellationToken);
        if (download.Image is not { } image)
            return download.Failed ? Outcome.Failed : Outcome.NotFound;

        foreach (string file in wanted)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            // Written under another name first, so a loader never finds half an image.
            string temporary = file + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, image, cancellationToken);
                File.Move(temporary, file, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        return Outcome.Downloaded;
    }

    private enum Outcome
    {
        Downloaded,
        Present,
        NotFound,
        Failed,
    }

    /// <summary>One image for one game, and every file it has to be saved as.</summary>
    private sealed record Cover(string GameId, CoverType Type, IReadOnlyList<string> Files);
}
