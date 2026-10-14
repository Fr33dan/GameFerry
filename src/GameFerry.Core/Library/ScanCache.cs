using System.Text.Json;
using GameFerry.Engine.Disc;
using GameFerry.Engine.Library;

namespace GameFerry.Core.Library;

/// <summary>
/// Remembers what was found inside source files, so a large library on a network share
/// doesn't have to be opened file by file on every scan.
/// </summary>
public sealed class ScanCache
{
    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;
    private bool _dirty;

    public ScanCache(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    /// <summary>Fills in an item from the cache if the file hasn't changed since it was inspected.</summary>
    public bool TryApply(SourceItem item)
    {
        lock (_entries)
        {
            if (!_entries.TryGetValue(item.Path, out Entry? entry)
                || entry.FileSize != item.FileSize
                || entry.LastWriteTicks != item.LastWriteUtc.Ticks)
            {
                return false;
            }

            item.EntryName = entry.EntryName;
            item.Id6 = entry.Id6;
            item.DiscTitle = entry.DiscTitle;
            item.Kind = entry.Kind;
            item.Format = entry.Format;
            item.SourceSize = entry.SourceSize;
            item.ImageSize = entry.ImageSize;
            item.UpdateRemoved = entry.UpdateRemoved;
            item.Problem = entry.Problem;
            item.Inspected = true;
            return true;
        }
    }

    public void Store(SourceItem item)
    {
        lock (_entries)
        {
            _entries[item.Path] = new Entry(
                item.FileSize, item.LastWriteUtc.Ticks, item.EntryName, item.Id6, item.DiscTitle,
                item.Kind, item.Format, item.SourceSize, item.ImageSize, item.UpdateRemoved, item.Problem);
            _dirty = true;
        }
    }

    public void Save()
    {
        lock (_entries)
        {
            if (!_dirty)
                return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_entries));
                _dirty = false;
            }
            catch (IOException)
            {
                // The cache is only an optimisation.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) is { } entries)
            {
                return new Dictionary<string, Entry>(entries, StringComparer.OrdinalIgnoreCase);
            }
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

        return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Entry(
        long FileSize, long LastWriteTicks, string? EntryName, string? Id6, string? DiscTitle,
        DiscKind Kind, SourceFormat Format, long SourceSize, long ImageSize, bool UpdateRemoved, string? Problem);
}
