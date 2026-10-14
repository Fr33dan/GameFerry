using System.Buffers.Binary;

namespace GameFerry.Engine.NKit;

/// <summary>A forward-only reader over an NKit image that knows how far it has got.</summary>
internal sealed class NKitInput(Stream stream)
{
    private readonly byte[] _scratch = new byte[0x10000];

    public long Position { get; private set; }

    public void ReadExactly(Span<byte> buffer)
    {
        stream.ReadExactly(buffer);
        Position += buffer.Length;
    }

    public byte[] ReadBytes(long count)
    {
        if (count < 0 || count > int.MaxValue)
            throw new InvalidDataException($"NKit image asks for a {count:N0}-byte section.");
        byte[] bytes = new byte[count];
        ReadExactly(bytes);
        return bytes;
    }

    public uint ReadUInt32()
    {
        Span<byte> word = stackalloc byte[4];
        ReadExactly(word);
        return BinaryPrimitives.ReadUInt32BigEndian(word);
    }

    public void Skip(long count)
    {
        while (count > 0)
        {
            int chunk = (int)Math.Min(count, _scratch.Length);
            ReadExactly(_scratch.AsSpan(0, chunk));
            count -= chunk;
        }
    }
}
