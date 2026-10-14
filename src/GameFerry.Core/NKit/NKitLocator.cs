namespace GameFerry.Core.NKit;

/// <summary>Data files from an NKit installation that conversions make use of.</summary>
/// <param name="RedumpDat">Redump checksum list, used to confirm a rebuilt image is a known-good dump.</param>
/// <param name="GameTdb">GameTDB <c>ID = Title</c> list.</param>
/// <param name="RecoveryDirectory">Update partitions NKit needs to rebuild images that had theirs removed.</param>
public sealed record NKitData(string? RedumpDat, string? CustomDat, string? GameTdb, string? RecoveryDirectory)
{
    public static NKitData None { get; } = new(null, null, null, null);
}

public static class NKitLocator
{
    /// <summary>Finds the data files in an NKit folder. Both folder layouts NKit has shipped with are understood.</summary>
    public static NKitData Locate(string? nkitFolder)
    {
        if (string.IsNullOrWhiteSpace(nkitFolder) || !Directory.Exists(nkitFolder))
            return NKitData.None;

        return new NKitData(
            RedumpDat: LatestFile(nkitFolder, "*.dat", @"Dats\Redump\Wii", @"Dats\Wii_Redump"),
            CustomDat: LatestFile(nkitFolder, "*.dat", @"Dats\Custom\Wii", @"Dats\Wii_Custom"),
            GameTdb: LatestFile(nkitFolder, "*.txt", @"Dats\GameTdb"),
            RecoveryDirectory: FirstDirectory(nkitFolder, @"Recovery\Redump\Wii", @"Recovery\Wii"));
    }

    private static string? LatestFile(string root, string pattern, params string[] folders)
    {
        foreach (string folder in folders)
        {
            string path = Path.Combine(root, folder);
            if (!Directory.Exists(path))
                continue;

            FileInfo? latest = new DirectoryInfo(path).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latest is not null)
                return latest.FullName;
        }

        return null;
    }

    private static string? FirstDirectory(string root, params string[] folders) =>
        folders.Select(folder => Path.Combine(root, folder)).FirstOrDefault(Directory.Exists);
}
