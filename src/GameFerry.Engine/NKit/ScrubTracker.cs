using GameFerry.Engine.Crypto;

namespace GameFerry.Engine.NKit;

/// <summary>
/// NKit's ScrubManager: writes runs that a scrubbing tool blanked, and inside a partition remembers which sectors
/// they covered. A scrubbed partition sector was all 00 or FF while encrypted, so its decrypted form is a 16-byte
/// pattern (the title key's decryption of that block) repeated; those sectors later get special hashes and IVs.
/// </summary>
internal sealed class ScrubTracker
{
    private readonly byte[]? _pattern00;
    private readonly byte[]? _patternFF;
    private readonly List<Region> _regions = [];
    private Region? _last;
    private Region? _next;
    private int _taken;

    /// <summary>Outside partitions: scrubbed runs are just the byte itself.</summary>
    public ScrubTracker()
    {
    }

    public ScrubTracker(byte[] titleKey)
    {
        using AesCbc aes = AesCbc.Create(titleKey);
        _pattern00 = new byte[16];
        aes.Decrypt(new byte[16], new byte[16], _pattern00);

        byte[] ones = [.. Enumerable.Repeat((byte)0xFF, 16)];
        _patternFF = new byte[16];
        aes.Decrypt(ones, ones, _patternFF);
    }

    /// <summary>Writes a scrubbed run starting at <paramref name="dataOffset"/> (partition data coordinates).</summary>
    public void Fill(long dataOffset, Span<byte> target, byte value)
    {
        if (_pattern00 is null)
        {
            target.Fill(value);
            return;
        }

        byte[] pattern = value switch
        {
            0x00 => _pattern00,
            0xFF => _patternFF!,
            _ => throw new InvalidDataException($"NKit image is scrubbed with 0x{value:X2} inside a partition, which NKit itself does not support."),
        };

        Add(dataOffset, target.Length, value);
        int x = (int)(dataOffset % 16);
        for (int i = 0; i < target.Length; i++)
        {
            target[i] = pattern[x++];
            if (x == 16)
                x = 0;
        }
    }

    /// <summary>
    /// The check NKit makes while rebuilding, walking forward through the runs: <paramref name="offset"/> is a
    /// sector's position in the partition's hashed data area, and calls must come in increasing order.
    /// </summary>
    public bool IsScrubbedInOrder(long offset, out byte value)
    {
        if (_next is null || _next.Offset + _next.Length < offset)
            _next = _taken < _regions.Count ? _regions[_taken++] : null;
        return Covers(_next, offset, out value);
    }

    /// <summary>The check NKit makes when it goes back to patch a group: any run, in any order.</summary>
    public bool IsScrubbed(long offset, out byte value)
    {
        foreach (Region region in _regions)
        {
            if (Covers(region, offset, out value))
                return true;
        }

        value = 0;
        return false;
    }

    private static bool Covers(Region? region, long offset, out byte value)
    {
        value = 0;
        if (region is null || offset < region.Offset || offset >= region.Offset + region.Length)
            return false;
        value = region.Byte;
        return true;
    }

    /// <summary>Records a run, widened to whole sectors and converted to hashed offsets, merging it with the last.</summary>
    private void Add(long offset, long length, byte value)
    {
        const long data = 0x7C00;
        if (offset % data != 0)
        {
            length += offset % data;
            offset -= offset % data;
        }

        if (length % data != 0)
            length += data - length % data;

        offset = offset / data * 0x8000;
        length = length / data * 0x8000;
        if (_last is not null && _last.Byte == value && offset >= _last.Offset && offset <= _last.Offset + _last.Length)
        {
            if (offset + length > _last.Offset + _last.Length)
                _last.Length = offset + length - _last.Offset;
        }
        else
        {
            _last = new Region { Offset = offset, Length = length, Byte = value };
            _regions.Add(_last);
        }
    }

    private sealed class Region
    {
        public long Offset;
        public long Length;
        public byte Byte;
    }
}
