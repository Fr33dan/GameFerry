using GameFerry.Cli;

const string Usage = """
    GameFerry - copies Wii games (NKit/ISO/WBFS, loose or inside 7z/zip/rar) to a drive as split WBFS files.

    Usage:
      ferry scan <folder|file> [--recurse]
          List the games found and what each file contains.

      ferry targets
          List the drives that can be used as a target.

      ferry list <drive|folder>
          List the games already on a target.

      ferry transfer <folder|file>... --to <drive|folder> [options]
          Convert and copy every game that is not already on the target.
            --recurse                    include sub-folders of source folders
            --partitions all|no-update|game   which disc partitions to keep (default: all)
            --split auto|always|never    split files at 4 GiB (default: auto = only where the file system needs it)
            --titles gametdb|disc|file   where folder names come from (default: gametdb)
            --no-verify                  skip re-reading the written files from the target
            --one-at-a-time              don't prepare the next game while the current one is being written
            --overwrite                  replace games that are already on the target
            --no-covers                  don't download cover art for the copied games
            --loader auto|wiiflow|gx|cfg|all   whose cover folders to fill (default: auto = loaders found on the drive)
            --cover-root <folder>        drive or folder the loader is on, if not the one the games go to
            --nkit <folder>              NKit folder holding Dats and Recovery files
            --work <folder>              local folder for the rebuilt ISO
            --dry-run                    show what would be transferred and stop
            --verbose                    show NKit's own output

      ferry covers <drive|folder> [--loader auto|wiiflow|gx|cfg|all] [--cover-root <folder>] [--force]
          Download cover art from GameTDB for every game on the target, into the folders the loader reads:
            WiiFlow                  wiiflow\boxcovers, wiiflow\covers
            USB Loader GX            apps\usbloader_gx\images (and its 2D, full and disc sub-folders)
            Configurable USB Loader  usb-loader\covers (and its 3d, disc and full sub-folders)
          Covers already there are kept unless --force is given. Anyone can add a missing cover at
          https://www.gametdb.com.

      ferry verify <file.wbfs> [--deep] [--nkit <folder>] [--work <folder>]
          Check that a WBFS file holds every sector its disc uses. --deep also has NKit rebuild
          the original disc from it and compare the result with the Redump database.

      ferry compare <first.wbfs> <second.wbfs>
          Compare two WBFS files of the same disc block by block.

    The target is treated as the root of the drive a loader reads: games go in its wbfs folder, whether it is
    a drive (D:) or a folder standing in for one. Pick the wbfs folder itself and games go straight into it.
    """;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
{
    Console.WriteLine(Usage);
    return 0;
}

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C stops cleanly (partial files are removed); a second one ends the process.
    if (!cancellation.IsCancellationRequested)
    {
        e.Cancel = true;
        cancellation.Cancel();
        Console.Error.WriteLine("Cancelling...");
    }
};

try
{
    string[] rest = args[1..];
    return args[0].ToLowerInvariant() switch
    {
        "scan" => await Commands.ScanAsync(rest, cancellation.Token),
        "targets" => Commands.Targets(),
        "list" => Commands.List(rest),
        "transfer" => await Commands.TransferAsync(rest, cancellation.Token),
        "covers" => await Commands.CoversAsync(rest, cancellation.Token),
        "verify" => await Commands.VerifyAsync(rest, cancellation.Token),
        "compare" => Commands.Compare(rest, cancellation.Token),
        _ => throw new UsageException($"Unknown command '{args[0]}'. Run 'ferry help' for usage."),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
