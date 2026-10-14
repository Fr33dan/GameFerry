using GameFerry.Core.Disc;
using GameFerry.Core.Wbfs;
using GameFerry.Engine.Disc;
using GameFerry.Engine.Wbfs;

namespace GameFerry.Core.Library;

public enum SplitMode
{
    /// <summary>Split only where the file system can't hold files of 4 GiB or more.</summary>
    Auto,
    Always,
    Never,
}

/// <summary>A game already stored on a target.</summary>
public sealed record InstalledGame(string Id6, string Title, string MainFile, IReadOnlyList<string> Files, long Size)
{
    public string Folder => Path.GetDirectoryName(MainFile)!;
}

/// <summary>A drive that could be used as a target.</summary>
public sealed record TargetDrive(string Root, string Label, string FileSystem, DriveType Type, long TotalBytes, long FreeBytes)
{
    public override string ToString()
    {
        string name = string.IsNullOrEmpty(Label) ? Type.ToString() : Label;
        return $"{Root.TrimEnd('\\')} {name} ({FileSystem}, {TargetLibrary.FormatSize(FreeBytes)} free)";
    }
}

/// <summary>
/// The place games are written to: the <c>wbfs</c> folder of a drive, or of a folder that stands in for one
/// (a staging area whose contents are copied onto the drive later).
/// </summary>
public sealed class TargetLibrary
{
    /// <summary>The folder, at the root of a drive, that USB loaders look for games in.</summary>
    public const string WbfsFolderName = "wbfs";

    // File systems known to take files of 4 GiB and over; anything else gets split files.
    private static readonly string[] LargeFileSystems = ["NTFS", "exFAT", "ReFS"];

    private TargetLibrary(string root, string gamesRoot)
    {
        Root = root;
        GamesRoot = gamesRoot;
        Refresh();
    }

    /// <summary>The drive or folder that was chosen.</summary>
    public string Root { get; }

    /// <summary>The folder that holds the <c>Title [ID]</c> game folders.</summary>
    public string GamesRoot { get; }

    /// <summary>
    /// The folder a loader's own folders (<c>wiiflow</c>, <c>apps</c>, <c>usb-loader</c>) sit in: the one
    /// containing the <c>wbfs</c> games folder, or the chosen folder itself when games are stored directly in it.
    /// </summary>
    public string DeviceRoot =>
        IsNamedWbfs(GamesRoot) && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(GamesRoot)) is { } parent ? parent : Root;

    public string FileSystem { get; private set; } = string.Empty;

    public long TotalBytes { get; private set; }

    public long FreeBytes { get; private set; }

    public bool SupportsLargeFiles => LargeFileSystems.Contains(FileSystem, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when games sit directly in the chosen folder rather than in a <c>wbfs</c> folder: a collection
    /// kept that way already. A loader would not find games laid out like this on a drive.
    /// </summary>
    public bool StoresGamesDirectly => !IsNamedWbfs(GamesRoot);

    /// <summary>
    /// Opens a target. Whatever is chosen, drive or folder, is treated as the root of the device a loader
    /// reads: games go in a <c>wbfs</c> folder inside it, which is the only place loaders look. Two kinds of
    /// folder are taken to be the games folder itself instead: one that is named <c>wbfs</c>, and one that
    /// already has games directly in it and no <c>wbfs</c> folder.
    /// </summary>
    public static TargetLibrary Open(string path)
    {
        string full = Path.GetFullPath(path);
        string? driveRoot = Path.GetPathRoot(full);
        bool isDriveRoot = driveRoot is not null
            && string.Equals(full.TrimEnd('\\'), driveRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        string wbfsFolder = Path.Combine(full, WbfsFolderName);
        bool isGamesFolder = !isDriveRoot
            && (IsNamedWbfs(full) || (!Directory.Exists(wbfsFolder) && HoldsGamesDirectly(full)));
        return new TargetLibrary(full, isGamesFolder ? full : wbfsFolder);
    }

    /// <summary>Mounted drives that can take games, removable ones first.</summary>
    public static List<TargetDrive> ListDrives()
    {
        List<TargetDrive> drives = [];
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory)
                    continue;

                drives.Add(new TargetDrive(drive.Name, drive.VolumeLabel, drive.DriveFormat, drive.DriveType, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return [.. drives.OrderBy(d => d.Type == DriveType.Removable ? 0 : 1).ThenBy(d => d.Root)];
    }

    /// <summary>Re-reads the file system type and free space.</summary>
    public void Refresh()
    {
        try
        {
            DriveInfo drive = new(Path.GetPathRoot(Root)!);
            FileSystem = drive.DriveFormat;
            TotalBytes = drive.TotalSize;
            FreeBytes = drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // UNC paths and unready drives: unknown file system, so files get split to be safe.
            FileSystem = string.Empty;
            TotalBytes = 0;
            FreeBytes = long.MaxValue;
        }
    }

    /// <summary>Size at which files written here are split; 0 for no splitting.</summary>
    public long SplitSizeFor(SplitMode mode) => mode switch
    {
        SplitMode.Always => WbfsFormat.DefaultSplitSize,
        SplitMode.Never => 0,
        _ => SupportsLargeFiles ? 0 : WbfsFormat.DefaultSplitSize,
    };

    /// <summary>Finds the games stored here, both loose and in their own folders.</summary>
    public List<InstalledGame> Scan()
    {
        List<InstalledGame> games = [];
        if (!Directory.Exists(GamesRoot))
            return games;

        IEnumerable<string> folders = Directory.EnumerateDirectories(GamesRoot).Prepend(GamesRoot);
        foreach (string folder in folders)
        {
            foreach (string file in EnumerateFilesSafely(folder))
            {
                string extension = Path.GetExtension(file);
                if (extension.Equals(".wbfs", StringComparison.OrdinalIgnoreCase))
                {
                    if (WbfsReader.TryReadInfo(file) is { } info)
                        games.Add(new InstalledGame(info.Disc.Id6, info.Disc.Title, file, info.Parts, info.TotalSize));
                }
                else if (extension.Equals(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    if (DiscFiles.TryReadHeader(file) is { Kind: DiscKind.Wii, IsNkit: false } header)
                        games.Add(new InstalledGame(header.Id6, header.Title, file, [file], new FileInfo(file).Length));
                }
            }
        }

        return [.. games.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Deletes a game's files, and its folder if nothing else is left in it.</summary>
    public void Delete(InstalledGame game)
    {
        foreach (string file in game.Files)
            File.Delete(file);

        string folder = game.Folder;
        bool isOwnFolder = !string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), GamesRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (isOwnFolder && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private static bool IsNamedWbfs(string folder) =>
        string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), WbfsFolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True if the folder has WBFS games right in it: loose, or in the usual <c>Title [GAMEID]</c> folders.</summary>
    private static bool HoldsGamesDirectly(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                && (Directory.EnumerateFiles(folder, "*.wbfs").Any()
                    || Directory.EnumerateDirectories(folder, "*[??????]").Any(game => Directory.EnumerateFiles(game, "*.wbfs").Any()));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateFilesSafely(string folder)
    {
        try
        {
            return Directory.GetFiles(folder);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
