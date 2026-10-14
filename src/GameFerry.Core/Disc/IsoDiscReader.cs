using GameFerry.Engine.Disc;
using Microsoft.Win32.SafeHandles;

namespace GameFerry.Core.Disc;

/// <summary>Reads a plain, full-layout ISO image.</summary>
public sealed class IsoDiscReader : IDiscReader
{
    private readonly SafeFileHandle _handle;

    public IsoDiscReader(string path)
    {
        Path = path;
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Length = RandomAccess.GetLength(_handle);
    }

    public string Path { get; }

    public long Length { get; }

    public void ReadAt(long offset, Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int read = offset < Length ? RandomAccess.Read(_handle, buffer, offset) : 0;
            if (read <= 0)
            {
                buffer.Clear();
                return;
            }

            buffer = buffer[read..];
            offset += read;
        }
    }

    public void Dispose() => _handle.Dispose();
}
