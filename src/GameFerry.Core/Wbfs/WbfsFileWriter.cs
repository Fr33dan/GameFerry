using System.Security.Cryptography;
using GameFerry.Engine.Disc;
using GameFerry.Engine.Wbfs;

namespace GameFerry.Core.Wbfs;

/// <summary>Writes the engine's WBFS image to files, split as the target needs.</summary>
public static class WbfsFileWriter
{
    /// <summary>
    /// Writes the planned WBFS image and returns the SHA-256 of everything written, for read-back verification.
    /// </summary>
    /// <param name="progress">Receives the running count of bytes written.</param>
    public static byte[] Write(IDiscReader disc, WbfsPlan plan, SplitFileWriter output, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (WbfsChunk chunk in WbfsWriter.Produce(disc, plan, cancellationToken))
        {
            // The split writer only appends, and the engine produces the image in file order.
            if (chunk.Position != output.Length)
                throw new InvalidOperationException($"WBFS data for 0x{chunk.Position:X} arrived out of order.");

            output.Write(chunk.Data.Span);
            hash.AppendData(chunk.Data.Span);
            progress?.Report(output.Length);
        }

        return hash.GetHashAndReset();
    }
}
