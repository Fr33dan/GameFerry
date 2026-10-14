using System.Net;

namespace GameFerry.Core.Covers;

/// <summary>What GameTDB answered when asked for one cover.</summary>
/// <param name="Image">The PNG file; null when GameTDB has none, or when it could not be fetched.</param>
/// <param name="Failed">True when there is no image because of a network or server error, not because GameTDB has none.</param>
public readonly record struct GameTdbImage(byte[]? Image, bool Failed);

/// <summary>
/// Cover art on GameTDB, which is at <c>https://art.gametdb.com/wii/{kind}/{language}/{GAMEID}.png</c>.
/// Art is filed under the language of the release, so a US game is only found under <c>US</c> and a
/// European one under <c>EN</c>, <c>FR</c>, <c>DE</c> and so on.
/// </summary>
public static class GameTdbArt
{
    /// <summary>
    /// GameTDB's own site, where anyone can add a missing game or cover. GameTDB asks programs that use its art to
    /// link to it, so users know where to add what is missing.
    /// </summary>
    public const string Site = "https://www.gametdb.com";

    private const string BaseUrl = "https://art.gametdb.com/wii";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Language tried first for European releases, which exist in several languages.</summary>
    private const string EuropeanLanguage = "EN";

    /// <summary>GameTDB's folder names for a kind of cover, best first.</summary>
    public static IReadOnlyList<string> Kinds(CoverType type) => type switch
    {
        CoverType.Front => ["cover"],
        CoverType.Box3D => ["cover3D"],
        CoverType.Disc => ["disc", "disccustom"],
        _ => ["coverfullHQ", "coverfull"],
    };

    /// <summary>
    /// The languages to look under for a game, likeliest first, worked out from the region letter of its ID.
    /// This is the order USB Loader GX searches in.
    /// </summary>
    public static IReadOnlyList<string> Languages(string gameId)
    {
        char region = gameId.Length > 3 ? char.ToUpperInvariant(gameId[3]) : ' ';
        List<string> languages =
        [
            region switch
            {
                'E' or 'N' => "US",
                'J' => "JA",
                'K' or 'Q' or 'T' => "KO",
                'W' => "ZH",
                'P' or 'D' or 'F' or 'H' or 'I' or 'L' or 'M' or 'R' or 'S' or 'U' or 'V' or 'X' or 'Y' or 'Z' => EuropeanLanguage,
                _ => "EN",
            },
        ];

        // A release for one country may only have art in that country's language.
        switch (region)
        {
            case 'D': languages.Add("DE"); break;
            case 'F': languages.Add("FR"); break;
            case 'H': languages.Add("NL"); break;
            case 'I': languages.Add("IT"); break;
            case 'R': languages.Add("RU"); break;
            case 'S': languages.Add("ES"); break;
            case 'U': languages.Add("AU"); break;
            case 'V': languages.Add("DK"); break;
        }

        // X, Y and Z are used for special releases on both sides of the Atlantic.
        if (region is 'X' or 'Y' or 'Z')
            languages.Add("US");

        languages.Add("EN");
        if (region == 'V')
            languages.AddRange(["FI", "SE"]);

        return [.. languages.Distinct()];
    }

    /// <summary>Every address worth trying for one cover, best first.</summary>
    public static IEnumerable<Uri> Candidates(CoverType type, string gameId)
    {
        foreach (string kind in Kinds(type))
        {
            foreach (string language in Languages(gameId))
                yield return new Uri($"{BaseUrl}/{kind}/{language}/{gameId}.png");
        }
    }

    /// <summary>True if the data starts the way every PNG file does.</summary>
    public static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith(PngSignature);

    /// <summary>Downloads one cover, trying each of its addresses in turn.</summary>
    public static async Task<GameTdbImage> DownloadAsync(HttpClient http, CoverType type, string gameId, CancellationToken cancellationToken)
    {
        bool serverTrouble = false;
        foreach (Uri address in Candidates(type, gameId))
        {
            try
            {
                using HttpResponseMessage response = await http.GetAsync(address, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    continue;
                if (!response.IsSuccessStatusCode)
                {
                    serverTrouble = true;
                    continue;
                }

                // Anything that isn't an image (an error page, say) is passed over.
                byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (IsPng(body))
                    return new GameTdbImage(body, Failed: false);
            }
            catch (HttpRequestException)
            {
                // No connection: the other addresses on the same server won't fare any better.
                return new GameTdbImage(null, Failed: true);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new GameTdbImage(null, Failed: true);
            }
        }

        return new GameTdbImage(null, serverTrouble);
    }
}
