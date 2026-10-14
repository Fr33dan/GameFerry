using GameFerry.Engine.Disc;

namespace GameFerry.Core.Disc;

public static class DiscFiles
{
    /// <summary>Reads the header of a loose image file; returns null if it isn't a recognisable disc image.</summary>
    public static DiscHeader? TryReadHeader(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] data = new byte[DiscHeader.PeekSize];
            int read = stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
            return DiscHeader.TryParse(data.AsSpan(0, read));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
