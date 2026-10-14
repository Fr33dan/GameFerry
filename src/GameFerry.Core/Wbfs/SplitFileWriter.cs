using GameFerry.Engine.Wbfs;

namespace GameFerry.Core.Wbfs;

/// <summary>
/// Writes one logical stream as <c>NAME.wbfs</c>, <c>NAME.wbf1</c>… with each part capped at the split size.
/// Parts are written under temporary names and only take their real names on <see cref="Commit"/>,
/// so an interrupted transfer never leaves something that looks like a finished game.
/// </summary>
public sealed class SplitFileWriter : IDisposable
{
    public const string TempSuffix = ".tmp";

    /// <summary>USB loaders look for at most ten parts.</summary>
    public const int MaxParts = 10;

    private readonly string _directory;
    private readonly string _baseName;
    private readonly long _splitSize;
    private readonly List<string> _finalPaths = [];
    private readonly List<string> _leftBehind = [];
    private FileStream? _current;
    private long _currentLength;
    private bool _committed;
    private bool _disposed;

    /// <param name="splitSize">Maximum bytes per part; 0 writes a single file.</param>
    public SplitFileWriter(string directory, string baseName, long splitSize)
    {
        if (splitSize < 0 || splitSize % WbfsFormat.HdSectorSize != 0)
            throw new ArgumentOutOfRangeException(nameof(splitSize), "Split size must be a multiple of 512 bytes.");

        _directory = directory;
        _baseName = baseName;
        _splitSize = splitSize;
    }

    public long Length { get; private set; }

    /// <summary>The temporary files written so far, in order.</summary>
    public IReadOnlyList<string> TempPaths => _finalPaths.Select(path => path + TempSuffix).ToList();

    /// <summary>Temporary files that <see cref="Dispose"/> could not delete, for the caller to retry or report.</summary>
    public IReadOnlyList<string> LeftBehind => _leftBehind;

    public void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_current is null || (_splitSize > 0 && _currentLength == _splitSize))
                OpenNextPart();

            int count = _splitSize > 0 ? (int)Math.Min(data.Length, _splitSize - _currentLength) : data.Length;
            _current!.Write(data[..count]);
            _currentLength += count;
            Length += count;
            data = data[count..];
        }
    }

    /// <summary>Flushes everything to the device and closes the files, leaving them under their temporary names.</summary>
    public void Close() => CloseCurrent(flushToDisk: true);

    /// <summary>Gives the parts their final names, replacing any previous copy of the same set.</summary>
    public IReadOnlyList<string> Commit()
    {
        Close();
        foreach (string path in _finalPaths)
            File.Move(path + TempSuffix, path, overwrite: true);

        // A previous, larger copy may have left higher-numbered parts behind.
        for (int index = _finalPaths.Count; index < MaxParts; index++)
            File.Delete(Path.Combine(_directory, WbfsFormat.PartFileName(_baseName, index)));

        _committed = true;
        return _finalPaths;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            CloseCurrent(flushToDisk: false);
        }
        catch (IOException)
        {
            // A drive that has dropped out can refuse even the close; the parts still need removing.
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (_committed)
            return;

        foreach (string path in TempPaths)
        {
            if (!TryDelete(path))
                _leftBehind.Add(path);
        }
    }

    /// <summary>Deletes a file, returning false rather than throwing when the drive refuses.</summary>
    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
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

    private void OpenNextPart()
    {
        CloseCurrent(flushToDisk: true);
        if (_finalPaths.Count >= MaxParts)
            throw new IOException($"The image needs more than {MaxParts} parts at this split size.");

        string path = Path.Combine(_directory, WbfsFormat.PartFileName(_baseName, _finalPaths.Count));
        _finalPaths.Add(path);
        _current = new FileStream(path + TempSuffix, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.SequentialScan);
        _currentLength = 0;
    }

    private void CloseCurrent(bool flushToDisk)
    {
        if (_current is null)
            return;

        try
        {
            if (flushToDisk)
                _current.Flush(flushToDisk: true);
        }
        finally
        {
            _current.Dispose();
            _current = null;
        }
    }
}
