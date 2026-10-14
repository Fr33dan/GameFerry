namespace GameFerry.Engine.NKit;

/// <summary>
/// Bytes NKit regenerates, produced on demand: a subclass describes the output as a sequence of segments (copy from
/// the image, literal bytes, zeros, junk, scrub fill), parsing the image's gap records as it goes, and the read
/// methods play them back. Segments are only asked for once the previous one has been fully read, so the image is
/// consumed in exactly the order NKit's own reader consumes it.
/// </summary>
internal abstract class RestoredStream
{
    private const long GapBlockSize = 0x100;

    private IEnumerator<Segment>? _segments;
    private Segment _current;
    private long _done;
    private bool _hasCurrent;
    private bool _ended;

    /// <param name="start">Position of the first byte, in whatever address space junk is seeded from.</param>
    protected RestoredStream(NKitInput input, ScrubTracker scrub, long start)
    {
        Input = input;
        Scrub = scrub;
        Position = start;
    }

    protected enum Kind
    {
        Input,
        Memory,
        Zeros,
        Junk,
        Scrub,
    }

    protected enum GapType
    {
        AllJunk = 0b00,
        AllScrubbed = 0b01,
        Mixed = 0b10,
        JunkFile = 0b11,
    }

    private enum GapBlockType
    {
        Junk = 0b00,
        NonJunk = 0b01,
        ByteFill = 0b10,
        Repeat = 0b11,
    }

    /// <summary>Position of the next byte to be produced.</summary>
    public long Position { get; private set; }

    /// <summary>Where the gap being produced ends; nothing but gap bytes comes before it.</summary>
    public long GapEnd { get; private set; }

    /// <summary>
    /// Asked before junk is generated, with the position and length of the run: true means nobody will use these
    /// bytes, so zeros will do. Junk is the bulk of most discs and generating it is the costliest part of a restore.
    /// </summary>
    public Func<long, int, bool>? SkipJunk { get; set; }

    protected NKitInput Input { get; }

    protected ScrubTracker Scrub { get; }

    protected JunkGenerator? Junk { get; set; }

    /// <summary>Fills <paramref name="destination"/> completely; the description must not run out first.</summary>
    public void Read(Span<byte> destination)
    {
        if (ReadAvailable(destination) != destination.Length)
            throw new InvalidDataException("NKit image ended before the data it describes.");
    }

    /// <summary>Fills as much of <paramref name="destination"/> as the description still covers; 0 once it is over.</summary>
    public int ReadAvailable(Span<byte> destination)
    {
        _segments ??= Produce().GetEnumerator();
        int total = 0;
        while (!destination.IsEmpty)
        {
            if (!_hasCurrent)
            {
                if (_ended || !_segments.MoveNext())
                {
                    _ended = true;
                    break;
                }

                _current = _segments.Current;
                if (_current.Length < 0)
                    throw new InvalidDataException("NKit image describes a run of negative length.");
                _done = 0;
                _hasCurrent = true;
            }

            long left = _current.Length - _done;
            if (left == 0)
            {
                _hasCurrent = false;
                continue;
            }

            int count = (int)Math.Min(left, destination.Length);
            Span<byte> part = destination[..count];
            switch (_current.Kind)
            {
                case Kind.Input:
                    Input.ReadExactly(part);
                    break;
                case Kind.Memory:
                    _current.Bytes.AsSpan((int)_done, count).CopyTo(part);
                    break;
                case Kind.Zeros:
                    part.Clear();
                    break;
                case Kind.Junk:
                    if (SkipJunk?.Invoke(Position, count) == true)
                        part.Clear();
                    else
                        (Junk ?? throw new InvalidDataException("NKit image asks for junk before saying how to make it.")).Read(Position, part);
                    break;
                default:
                    Scrub.Fill(Position, part, _current.Value);
                    break;
            }

            _done += count;
            Position += count;
            total += count;
            destination = destination[count..];
            if (_done == _current.Length)
                _hasCurrent = false;
        }

        return total;
    }

    /// <summary>Runs the description to its end, which may still read trailing records; nothing more may be produced.</summary>
    public void Finish()
    {
        _segments ??= Produce().GetEnumerator();
        if (_hasCurrent && _done != _current.Length)
            throw new InvalidDataException("NKit image describes more data than its header declares.");

        while (!_ended && _segments.MoveNext())
        {
            if (_segments.Current.Length != 0)
                throw new InvalidDataException("NKit image describes more data than its header declares.");
        }

        _ended = true;
    }

    protected abstract IEnumerable<Segment> Produce();

    protected static Segment FromInput(long length) => new(Kind.Input, length);

    protected static Segment FromMemory(byte[] bytes) => new(Kind.Memory, bytes.Length, bytes);

    protected static Segment Zeros(long length) => new(Kind.Zeros, length);

    protected static Segment JunkRun(long length) => new(Kind.Junk, length);

    protected static Segment ScrubRun(long length, byte value) => new(Kind.Scrub, length, Value: value);

    /// <summary>
    /// NKit's <c>writeGap</c>: decodes one gap record (junk, scrubbed, or a mixed run list, possibly preceded by a file
    /// made entirely of junk) and produces the bytes it stands for. Mirrors NKit 1.4 exactly, including how many zeros
    /// come before the junk that follows a file.
    /// </summary>
    protected IEnumerable<Segment> Gap(GapState state, bool firstOrLastFile)
    {
        if (state.GapLength == 0)
        {
            if (state.FileLength == 0)
                state.NullsPos = Position + 0x1C;
            yield break;
        }

        long recordLength = state.GapLength;
        long start = Position;
        long size = Input.ReadUInt32();
        GapType type = (GapType)(size & 0b11);
        size &= 0xFFFFFFFC;
        if (size == 0xFFFFFFFC)
            size = 0xFFFFFFFCL + Input.ReadUInt32();
        state.GapLength = size;

        long nulls;
        if (type == GapType.JunkFile)
        {
            state.NullsPos = Math.Min(state.NullsPos - Position, 0);
            nulls = (size & 0xFC) >> 2;
            long junkFileLength = Input.ReadUInt32();
            state.FileLength = junkFileLength;
            state.JunkFileAt = Position;
            junkFileLength += junkFileLength % 4 == 0 ? 0 : 4 - junkFileLength % 4;

            // A file made of junk is still a file (it is marked as used); only what follows it is gap.
            GapEnd = start + junkFileLength;
            yield return Zeros(nulls);
            yield return JunkRun(junkFileLength - nulls);

            if (recordLength <= 8)
                yield break;

            size = Input.ReadUInt32();
            type = (GapType)(size & 0b11);
            size &= 0xFFFFFFFC;
            state.GapLength = size;
        }
        else if (state.FileLength == 0)
        {
            state.NullsPos = Position + 0x1C;
        }

        GapEnd = Position + size;

        long maxNulls = Math.Max(0, state.NullsPos - Position);
        nulls = size < maxNulls ? size : size >= 0x40000 && !firstOrLastFile ? 0 : maxNulls;
        state.NullsPos = Position + nulls;

        if (type == GapType.AllJunk)
        {
            yield return Zeros(nulls);
            yield return JunkRun(size - nulls);
        }
        else if (type == GapType.AllScrubbed)
        {
            yield return ScrubRun(size, 0);
        }
        else
        {
            long left = size;
            byte fill = 0;
            GapBlockType blockType = GapBlockType.Junk;
            while (left > 0)
            {
                long word = Input.ReadUInt32();
                GapBlockType wordType = (GapBlockType)(word >> 30);
                bool repeat = wordType == GapBlockType.Repeat;
                if (!repeat)
                    blockType = wordType;

                long count = word & 0x3FFFFFFF;
                long bytes;
                if (blockType == GapBlockType.NonJunk)
                {
                    bytes = Math.Min(count * GapBlockSize, left);
                    yield return FromInput(bytes);
                }
                else if (blockType == GapBlockType.ByteFill)
                {
                    if (!repeat)
                    {
                        fill = (byte)(count & 0xFF);
                        count >>= 8;
                    }

                    bytes = Math.Min(count * GapBlockSize, left);
                    yield return ScrubRun(bytes, fill);
                }
                else
                {
                    bytes = Math.Min(count * GapBlockSize, left);
                    maxNulls = Math.Max(0, state.NullsPos - Position);
                    nulls = left < maxNulls ? bytes : bytes >= 0x40000 && !firstOrLastFile ? 0 : maxNulls;
                    yield return Zeros(nulls);
                    yield return JunkRun(bytes - nulls);
                }

                left -= bytes;
            }
        }
    }

    protected readonly record struct Segment(Kind Kind, long Length, byte[]? Bytes = null, byte Value = 0);

    /// <summary>What NKit passes to and gets back from <c>writeGap</c> by reference.</summary>
    protected sealed class GapState
    {
        public long FileLength;
        public long GapLength;
        public long NullsPos;

        /// <summary>Set when the gap turned out to begin with a file made of junk: where that file starts.</summary>
        public long? JunkFileAt;
    }
}

/// <summary>Filler between partitions and after the last one, which NKit stores as one gap record; read to its end.</summary>
internal sealed class DiscFiller(NKitInput input, JunkGenerator junk, long start, long nullsPos)
    : RestoredStream(input, new ScrubTracker(), start)
{
    protected override IEnumerable<Segment> Produce()
    {
        Junk = junk;
        GapState state = new() { FileLength = -1, GapLength = -1, NullsPos = nullsPos };
        foreach (Segment segment in Gap(state, firstOrLastFile: true))
            yield return segment;
    }
}
