namespace GameFerry.Engine.Disc;

/// <summary>Random-access view of a disc image, addressed by disc offset.</summary>
public interface IDiscReader : IDisposable
{
    /// <summary>Logical size of the disc image in bytes.</summary>
    long Length { get; }

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="offset"/>; bytes the image doesn't hold read as zero.</summary>
    void ReadAt(long offset, Span<byte> buffer);
}

/// <summary>A reader over a scrubbed container (such as WBFS) that knows which sectors it actually stores.</summary>
public interface ISparseDiscReader : IDiscReader
{
    bool IsSectorPresent(long sectorIndex);
}
