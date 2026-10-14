using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GameFerry.Engine.Wbfs;
using Microsoft.Win32.SafeHandles;

namespace GameFerry.Core.Wbfs;

/// <summary>Re-reads written files from the device to confirm they hold what was sent.</summary>
public static class WbfsVerifier
{
    private const int ChunkSize = WbfsFormat.BlockSize;
    private const int MemoryAlignment = 4096;

    // FILE_FLAG_NO_BUFFERING: bypass the OS file cache, so the bytes really come back from the drive.
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    /// <summary>Computes the SHA-256 of the files' concatenated contents, reading around the OS cache when possible.</summary>
    /// <param name="progress">Receives the running count of bytes read.</param>
    public static unsafe byte[] HashFiles(IReadOnlyList<string> paths, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        // Unbuffered reads need a sector-aligned buffer.
        byte* memory = (byte*)NativeMemory.AlignedAlloc(ChunkSize, MemoryAlignment);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            foreach (string path in paths)
            {
                using SafeFileHandle handle = OpenForVerify(path);
                long length = RandomAccess.GetLength(handle);
                long position = 0;
                while (position < length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Span<byte> chunk = new(memory, ChunkSize);
                    int read = RandomAccess.Read(handle, chunk, position);
                    if (read <= 0)
                        throw new EndOfStreamException($"Unexpected end of '{path}' during verification.");

                    hash.AppendData(chunk[..read]);
                    position += read;
                    total += read;
                    progress?.Report(total);
                }
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            NativeMemory.AlignedFree(memory);
        }
    }

    private static SafeFileHandle OpenForVerify(string path)
    {
        try
        {
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, NoBuffering | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            // Some file systems refuse unbuffered access; a cached read still checks the file's logical contents.
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        }
    }
}
