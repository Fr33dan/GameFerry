using GameFerry.Engine.Wbfs;
using Microsoft.Win32.SafeHandles;

namespace GameFerry.Core.Wbfs;

/// <summary>Presents the parts of a split file as one contiguous, randomly readable stream.</summary>
public sealed class SplitFileReader : IDisposable
{
    private readonly SafeFileHandle[] _handles;
    private readonly long[] _starts;
    private readonly long[] _lengths;

    public SplitFileReader(IReadOnlyList<string> paths)
    {
        Paths = paths;
        _handles = new SafeFileHandle[paths.Count];
        _starts = new long[paths.Count];
        _lengths = new long[paths.Count];

        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                _handles[i] = File.OpenHandle(paths[i], FileMode.Open, FileAccess.Read, FileShare.Read);
                _starts[i] = Length;
                _lengths[i] = RandomAccess.GetLength(_handles[i]);
                Length += _lengths[i];
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IReadOnlyList<string> Paths { get; }

    public long Length { get; }

    /// <summary>Finds the existing parts of a split set, given its first file.</summary>
    public static List<string> FindParts(string wbfsPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(wbfsPath))!;
        string baseName = Path.GetFileNameWithoutExtension(wbfsPath);

        List<string> parts = [wbfsPath];
        for (int index = 1; ; index++)
        {
            string part = Path.Combine(directory, WbfsFormat.PartFileName(baseName, index));
            if (!File.Exists(part))
                return parts;
            parts.Add(part);
        }
    }

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="offset"/>; anything past the end reads as zero.</summary>
    public void ReadAt(long offset, Span<byte> buffer)
    {
        int part = 0;
        while (!buffer.IsEmpty)
        {
            while (part < _handles.Length && offset >= _starts[part] + _lengths[part])
                part++;
            if (part == _handles.Length)
            {
                buffer.Clear();
                return;
            }

            long within = offset - _starts[part];
            int count = (int)Math.Min(buffer.Length, _lengths[part] - within);
            int read = RandomAccess.Read(_handles[part], buffer[..count], within);
            if (read <= 0)
                throw new EndOfStreamException($"Unexpected end of '{Paths[part]}'.");

            buffer = buffer[read..];
            offset += read;
        }
    }

    public void Dispose()
    {
        foreach (SafeFileHandle? handle in _handles)
            handle?.Dispose();
    }
}
