namespace GameFerry.Core;

/// <summary>Per-user locations the application keeps its own files in.</summary>
public static class AppPaths
{
    private const string AppFolderName = "GameFerry";

    public static string LocalData { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string SettingsFile { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName, "settings.json");

    public static string DefaultWorkFolder { get; } = Path.Combine(LocalData, "work");

    public static string ScanCacheFile { get; } = Path.Combine(LocalData, "scan-cache.json");

    /// <summary>Where the covers shown in the game lists are kept.</summary>
    public static string CoverCacheFolder { get; } = Path.Combine(LocalData, "covers");

    /// <summary>Where the NKit helper process is unpacked to, in a sub-folder per version.</summary>
    public static string NKitHostFolder { get; } = Path.Combine(LocalData, "nkithost");
}
