namespace GameFerry.Core.Covers;

/// <summary>The kinds of artwork GameTDB has for a game.</summary>
public enum CoverType
{
    /// <summary>Flat front cover, 160×224.</summary>
    Front,

    /// <summary>Front cover drawn as a 3D box, 176×248.</summary>
    Box3D,

    /// <summary>Disc label, 160×160.</summary>
    Disc,

    /// <summary>Full wrap-around cover (back, spine and front), 1024×680.</summary>
    Full,
}

/// <summary>Which loader's cover folders to fill.</summary>
public enum CoverLoaderChoice
{
    /// <summary>Whichever loaders already have a folder on the drive.</summary>
    Auto,
    WiiFlow,
    UsbLoaderGx,
    ConfigurableUsbLoader,
    All,
    None,
}

/// <summary>A folder a loader reads one kind of cover from, relative to the root of the drive.</summary>
public sealed record CoverFolder(CoverType Type, string RelativePath);

/// <summary>
/// Where one loader keeps its covers. Every loader names the files <c>GAMEID.png</c>; what differs is which
/// kinds of cover it uses and which folders it looks in. The folders are each loader's defaults.
/// </summary>
/// <param name="LoaderFolder">The loader's own folder, whose presence shows the loader is installed on a drive.</param>
public sealed record CoverLayout(string Name, string LoaderFolder, IReadOnlyList<CoverFolder> Folders)
{
    public static CoverLayout WiiFlow { get; } = new(
        "WiiFlow",
        "wiiflow",
        [
            new CoverFolder(CoverType.Full, @"wiiflow\boxcovers"),
            new CoverFolder(CoverType.Front, @"wiiflow\covers"),
        ]);

    public static CoverLayout UsbLoaderGx { get; } = new(
        "USB Loader GX",
        @"apps\usbloader_gx",
        [
            new CoverFolder(CoverType.Box3D, @"apps\usbloader_gx\images"),
            new CoverFolder(CoverType.Front, @"apps\usbloader_gx\images\2D"),
            new CoverFolder(CoverType.Full, @"apps\usbloader_gx\images\full"),
            new CoverFolder(CoverType.Disc, @"apps\usbloader_gx\images\disc"),
        ]);

    // Configurable USB Loader reads flat covers from the covers folder itself unless told otherwise.
    public static CoverLayout ConfigurableUsbLoader { get; } = new(
        "Configurable USB Loader",
        "usb-loader",
        [
            new CoverFolder(CoverType.Front, @"usb-loader\covers"),
            new CoverFolder(CoverType.Box3D, @"usb-loader\covers\3d"),
            new CoverFolder(CoverType.Disc, @"usb-loader\covers\disc"),
            new CoverFolder(CoverType.Full, @"usb-loader\covers\full"),
        ]);

    public static IReadOnlyList<CoverLayout> All { get; } = [WiiFlow, UsbLoaderGx, ConfigurableUsbLoader];

    /// <summary>The loaders that have their folder on the given drive.</summary>
    public static List<CoverLayout> Detect(string deviceRoot) =>
        [.. All.Where(layout => Directory.Exists(Path.Combine(deviceRoot, layout.LoaderFolder)))];

    /// <summary>The layouts a setting stands for on the given drive.</summary>
    public static IReadOnlyList<CoverLayout> For(CoverLoaderChoice choice, string deviceRoot) => choice switch
    {
        CoverLoaderChoice.Auto => Detect(deviceRoot),
        CoverLoaderChoice.WiiFlow => [WiiFlow],
        CoverLoaderChoice.UsbLoaderGx => [UsbLoaderGx],
        CoverLoaderChoice.ConfigurableUsbLoader => [ConfigurableUsbLoader],
        CoverLoaderChoice.All => All,
        _ => [],
    };
}
