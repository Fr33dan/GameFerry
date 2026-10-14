using System.Runtime.InteropServices.JavaScript;

namespace GameFerry.Web.Engine;

/// <summary>The functions the worker script provides (module "gameferry").</summary>
internal static partial class Interop
{
    /// <summary>Reads part of a File the page passed in, synchronously; returns the bytes read.</summary>
    [JSImport("readFile", "gameferry")]
    internal static partial int ReadFile(int fileId, double offset, [JSMarshalAs<JSType.MemoryView>] Span<byte> buffer);

    /// <summary>
    /// Writes bytes into part <paramref name="part"/> of the game being copied (0 is the .wbfs, 1 the .wbf1...). The
    /// script copies the bytes before returning, and the promise settles once they are written.
    /// </summary>
    [JSImport("writeChunk", "gameferry")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    internal static partial Task WriteChunk(int part, double offset, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> data);

    /// <summary>How far the copy has got: bytes of the source read, bytes of WBFS written.</summary>
    [JSImport("progress", "gameferry")]
    internal static partial void Progress(double sourceBytes, double writtenBytes);
}
