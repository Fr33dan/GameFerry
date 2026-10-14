using GameFerry.Core.Disc;
using GameFerry.Core.NKit;
using GameFerry.Core.Wbfs;
using GameFerry.Engine.Disc;
using GameFerry.Engine.Library;

namespace GameFerry.Core.Library;

/// <summary>Finds candidate game files and works out what each one contains.</summary>
public sealed class SourceScanner
{
    private static readonly string[] Extensions = [".7z", ".zip", ".rar", ".iso", ".gcz", ".wbfs"];

    // What the NKit host reports for an archive with no disc image in it (WiiWare, DLC and the like).
    private const string NoImageMessage = "No disc image found.";

    private readonly NKitHostClient? _host;
    private readonly ScanCache _cache;

    public SourceScanner(NKitHostClient? host, ScanCache cache)
    {
        _host = host;
        _cache = cache;
    }

    /// <summary>Lists the candidate files in a folder (or the single file given). Nothing is opened yet.</summary>
    public static List<SourceItem> Enumerate(string path, bool recurse)
    {
        IEnumerable<FileInfo> files;
        if (File.Exists(path))
        {
            files = [new FileInfo(path)];
        }
        else
        {
            EnumerationOptions options = new() { RecurseSubdirectories = recurse, IgnoreInaccessible = true };
            files = new DirectoryInfo(path).EnumerateFiles("*", options)
                .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase));
        }

        return [.. files
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new SourceItem(f.FullName, f.Length, f.LastWriteTimeUtc))];
    }

    /// <summary>
    /// Identifies the image in each item that hasn't been inspected yet. Loose ISO and WBFS files are read
    /// directly; archives and compressed images are identified by NKit, which only decompresses the header.
    /// </summary>
    /// <param name="inspected">Called as each item is identified.</param>
    public async Task InspectAsync(IReadOnlyList<SourceItem> items, Action<SourceItem>? inspected, CancellationToken cancellationToken)
    {
        Dictionary<string, SourceItem> needNKit = new(StringComparer.OrdinalIgnoreCase);

        await Task.Run(
            () =>
            {
                foreach (SourceItem item in items.Where(i => !i.Inspected))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_cache.TryApply(item) || InspectDirectly(item))
                        inspected?.Invoke(item);
                    else
                        needNKit[item.Path] = item;
                }
            },
            cancellationToken);

        if (needNKit.Count > 0)
        {
            if (_host is null)
            {
                foreach (SourceItem item in needNKit.Values)
                {
                    item.Problem = "The NKit helper is unavailable, so this file can't be opened.";
                    item.Inspected = true;
                    inspected?.Invoke(item);
                }
            }
            else
            {
                await _host.InspectAsync(
                    [.. needNKit.Keys],
                    info =>
                    {
                        // An archive can list several images; the first one stands for the file.
                        if (!needNKit.Remove(info.Path, out SourceItem? item))
                            return;

                        Apply(item, info);

                        // A read error may be temporary (the share dropped out), so only settled answers are kept.
                        if (info.Ok || info.Message == NoImageMessage)
                            _cache.Store(item);
                        inspected?.Invoke(item);
                    },
                    cancellationToken);

                foreach (SourceItem item in needNKit.Values)
                {
                    item.Problem = "NKit gave no answer for this file.";
                    item.Inspected = true;
                    inspected?.Invoke(item);
                }
            }
        }

        _cache.Save();
    }

    private bool InspectDirectly(SourceItem item)
    {
        string extension = Path.GetExtension(item.Path);
        if (extension.Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            DiscHeader? header = DiscFiles.TryReadHeader(item.Path);
            if (header is null)
            {
                item.Problem = "Not a Wii or GameCube disc image.";
            }
            else
            {
                item.Id6 = header.Id6;
                item.DiscTitle = header.Title;
                item.Kind = header.Kind;
                item.Format = header.IsNkit ? SourceFormat.NkitIso : SourceFormat.Iso;
                item.SourceSize = item.FileSize;
                item.ImageSize = header.NkitImageSize ?? item.FileSize;
                item.UpdateRemoved = header.NkitUpdateRemoved;
            }
        }
        else if (extension.Equals(".wbfs", StringComparison.OrdinalIgnoreCase))
        {
            WbfsFileInfo? info = WbfsReader.TryReadInfo(item.Path);
            if (info is null)
            {
                item.Problem = "Not a valid WBFS file.";
            }
            else
            {
                item.Id6 = info.Disc.Id6;
                item.DiscTitle = info.Disc.Title;
                item.Kind = DiscKind.Wii;
                item.Format = SourceFormat.Wbfs;
                item.SourceSize = info.TotalSize;
                item.ImageSize = info.TotalSize;
            }
        }
        else
        {
            return false;
        }

        MarkUnsupported(item);
        item.Inspected = true;
        _cache.Store(item);
        return true;
    }

    private static void Apply(SourceItem item, NKitImageInfo info)
    {
        item.Inspected = true;
        if (!info.Ok)
        {
            item.Problem = info.Message switch
            {
                NoImageMessage => "No disc image inside (not a Wii disc).",
                { Length: > 0 } message => message.Trim(),
                _ => "Could not be read.",
            };
            return;
        }

        item.EntryName = info.Entry;
        item.Id6 = info.Id;
        item.DiscTitle = info.Title;
        item.Kind = info.IsWii ? DiscKind.Wii : info.IsGameCube ? DiscKind.GameCube : DiscKind.Unknown;
        item.Format = (info.IsNkit, info.IsGcz, info.IsWbfs) switch
        {
            (true, true, _) => SourceFormat.NkitGcz,
            (true, false, _) => SourceFormat.NkitIso,
            (false, true, _) => SourceFormat.Gcz,
            (false, false, true) => SourceFormat.Wbfs,
            _ => SourceFormat.Iso,
        };
        item.SourceSize = info.SourceSize;
        item.ImageSize = info.ImageSize;
        item.UpdateRemoved = info.UpdateRemoved;
        MarkUnsupported(item);
    }

    private static void MarkUnsupported(SourceItem item)
    {
        if (item.Problem is not null)
            return;

        if (item.Kind == DiscKind.GameCube)
            item.Problem = "GameCube disc (only Wii discs are supported).";
        else if (item.Kind != DiscKind.Wii || item.Id6 is null || !DiscHeader.IsValidId(item.Id6))
            item.Problem = "Not a Wii disc image.";
    }
}
