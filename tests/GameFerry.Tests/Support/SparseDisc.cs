using GameFerry.Engine.Disc;

namespace GameFerry.Tests.Support;

/// <summary>An in-memory disc image that only stores the sectors written to it.</summary>
internal sealed class SparseDisc : IDiscReader
{
    private readonly Dictionary<long, byte[]> _sectors = [];

    public long Length { get; set; } = WiiConstants.SingleLayerSize;

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            long sector = Math.DivRem(offset, WiiConstants.SectorSize, out long within);
            if (!_sectors.TryGetValue(sector, out byte[]? bytes))
                _sectors[sector] = bytes = new byte[WiiConstants.SectorSize];

            int count = (int)Math.Min(data.Length, WiiConstants.SectorSize - within);
            data[..count].CopyTo(bytes.AsSpan((int)within));
            data = data[count..];
            offset += count;
        }
    }

    public void ReadAt(long offset, Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            long sector = Math.DivRem(offset, WiiConstants.SectorSize, out long within);
            int count = (int)Math.Min(buffer.Length, WiiConstants.SectorSize - within);
            if (offset < Length && _sectors.TryGetValue(sector, out byte[]? bytes))
                bytes.AsSpan((int)within, count).CopyTo(buffer);
            else
                buffer[..count].Clear();

            buffer = buffer[count..];
            offset += count;
        }
    }

    public byte[] ReadSector(long sector)
    {
        byte[] bytes = new byte[WiiConstants.SectorSize];
        ReadAt(sector * WiiConstants.SectorSize, bytes);
        return bytes;
    }

    public void Dispose()
    {
    }
}
