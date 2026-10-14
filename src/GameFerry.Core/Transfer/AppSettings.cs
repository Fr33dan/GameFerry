using System.Text.Json;
using System.Text.Json.Serialization;
using GameFerry.Core.Covers;
using GameFerry.Core.Library;
using GameFerry.Engine.Disc;

namespace GameFerry.Core.Transfer;

/// <summary>Where the title in a game's folder name comes from.</summary>
public enum TitleSource
{
    /// <summary>The GameTDB title for the game ID, e.g. "Example Game Deluxe".</summary>
    GameTdb,

    /// <summary>The title stored on the disc, e.g. "EXAMPLE DX".</summary>
    Disc,

    /// <summary>The source file's name, e.g. "Example Game Deluxe (USA) (En,Fr,Es) (Rev 1)".</summary>
    FileName,
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// NKit installation supplying the Redump dat, GameTDB titles and update-partition recovery files; empty until
    /// one is chosen in Settings.
    /// </summary>
    public string NKitFolder { get; set; } = string.Empty;

    /// <summary>
    /// Local folder each game is unpacked and rebuilt in. A dual-layer game needs about 16 GB while it is being
    /// rebuilt, and with <see cref="PrepareAhead"/> the previous game's 8 GB ISO may still be there too.
    /// </summary>
    public string WorkFolder { get; set; } = AppPaths.DefaultWorkFolder;

    public PartitionSelection Partitions { get; set; } = PartitionSelection.All;

    public SplitMode Split { get; set; } = SplitMode.Auto;

    /// <summary>
    /// Re-read every game from the target after writing and compare it with what was sent. Off by default: it takes
    /// about as long again, and a bad copy shows up anyway as a game that won't start.
    /// </summary>
    public bool VerifyAfterWrite { get; set; }

    /// <summary>Have NKit re-read the rebuilt ISO from disk as well (it always checks the checksum while building).</summary>
    public bool NKitFullVerify { get; set; }

    /// <summary>Extract and rebuild the next game while the current one is being written (needs room for two games in the work folder).</summary>
    public bool PrepareAhead { get; set; } = true;

    public TitleSource TitleSource { get; set; } = TitleSource.GameTdb;

    /// <summary>Which loader's cover folders cover art is downloaded into.</summary>
    public CoverLoaderChoice CoverLoader { get; set; } = CoverLoaderChoice.Auto;

    /// <summary>Fetch cover art for each game once it has been copied.</summary>
    public bool DownloadCoversAfterTransfer { get; set; } = true;

    /// <summary>
    /// Drive or folder the loader is on, when that isn't where the games go (a loader on the SD card with
    /// games on a USB drive, say). Empty means the same place as the games.
    /// </summary>
    public string? CoverRoot { get; set; }

    /// <summary>
    /// Show each game's front cover in the game lists. The covers come from GameTDB the first time a game is
    /// listed and are kept on this PC, whatever <see cref="CoverLoader"/> says about the target.
    /// </summary>
    public bool ShowCoverArt { get; set; } = true;

    public string? LastSourceFolder { get; set; }

    public bool IncludeSubfolders { get; set; }

    public string? LastTarget { get; set; }

    /// <summary>Loads the saved settings; anything missing or unreadable falls back to the defaults.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) is { } settings)
                return settings;
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
