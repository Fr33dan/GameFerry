namespace GameFerry.Engine.NKit;

/// <summary>
/// The filler Nintendo's mastering tools put in unused disc space: a lagged Fibonacci stream seeded from the game ID,
/// the disc number and the position, generated in 256 KiB blocks. NKit drops it and regenerates it on the way back,
/// so this must match NKit's <c>JunkStream</c> bit for bit, quirks included (each word's second byte comes from bit 18).
/// </summary>
internal sealed class JunkGenerator
{
    public const int BlockSize = 0x40000;
    private const int StateWords = 0x824;
    private const int SeedInterval = 0x8000;

    private readonly byte[] _id = new byte[4];
    private readonly byte _disc;
    private readonly long _junkLength;
    private readonly uint[] _state = new uint[StateWords];
    private readonly byte[] _block = new byte[BlockSize];
    private long _blockIndex = -1;

    /// <param name="id">The four-character game ID the junk is seeded with.</param>
    /// <param name="length">Where the junk stops; it is rounded down to a whole sector and reads as zeros beyond.</param>
    public JunkGenerator(ReadOnlySpan<byte> id, byte disc, long length)
    {
        id[..4].CopyTo(_id);
        _disc = disc;
        _junkLength = length / 0x8000 * 0x8000;
    }

    /// <summary>Fills <paramref name="buffer"/> with the junk found at <paramref name="position"/>.</summary>
    public void Read(long position, Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            long block = position / BlockSize;
            int offset = (int)(position % BlockSize);
            if (block != _blockIndex)
            {
                Fill((uint)block);
                _blockIndex = block;
            }

            int count = Math.Min(buffer.Length, BlockSize - offset);
            _block.AsSpan(offset, count).CopyTo(buffer);
            buffer = buffer[count..];
            position += count;
        }
    }

    private void Fill(uint blockIndex)
    {
        uint[] state = _state;
        Array.Clear(state);
        int index = 0;
        uint block = blockIndex * 8 * 0x1EF29123;
        for (int i = 0; i < BlockSize; i += 4)
        {
            if ((i & (SeedInterval - 1)) == 0)
            {
                // int arithmetic, as in NKit: the sums can carry into the neighbouring byte.
                uint sample = (uint)((((_id[2] << 8) | _id[1]) << 16) | ((_id[3] + _id[2]) << 8) | (_id[0] + _id[1]));
                sample = ((sample ^ _disc) * 0x260BCD5) ^ block;
                Seed(sample, state);
                index = 520;
                block += 0x1EF29123;
            }

            index++;
            if (index == 0x209)
            {
                Advance(state);
                index = 0;
            }

            uint word = state[index];
            _block[i] = (byte)(word >> 24);
            _block[i + 1] = (byte)(word >> 18);
            _block[i + 2] = (byte)(word >> 8);
            _block[i + 3] = (byte)word;
        }

        long junkSize = Math.Clamp(_junkLength - (long)blockIndex * BlockSize, 0, BlockSize);
        _block.AsSpan((int)junkSize).Clear();
    }

    private static void Seed(uint sample, uint[] state)
    {
        uint word = 0;
        for (int i = 0; i < 0x11; i++)
        {
            for (int bit = 0; bit < 0x20; bit++)
            {
                sample *= 0x5D588B65;
                word = (word >> 1) | (++sample & 0x80000000);
            }

            state[i] = word;
        }

        state[0x10] ^= (state[0] >> 9) ^ (state[0x10] << 23);
        for (int i = 1; i != 0x1F9; i++)
            state[i + 0x10] = (state[i - 1] << 23) ^ (state[i] >> 9) ^ state[i + 15];
        for (int i = 0; i < 3; i++)
            Advance(state);
    }

    private static void Advance(uint[] state)
    {
        int i = 0;
        for (; i != 0x20; i++)
            state[i] ^= state[i + 0x1E9];
        for (; i != 0x209; i++)
            state[i] ^= state[i - 0x20];
    }
}
