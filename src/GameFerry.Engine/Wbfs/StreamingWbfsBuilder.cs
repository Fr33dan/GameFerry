using System.Buffers;
using System.Buffers.Binary;
using GameFerry.Engine.Disc;
using GameFerry.Engine.NKit;

namespace GameFerry.Engine.Wbfs;

/// <summary>
/// Builds a WBFS image from a disc that arrives as a stream rather than as a file to read from. It keeps the 2 MiB
/// blocks with something in them, numbered in disc order as <see cref="WbfsWriter.Plan"/> numbers them, and hands
/// each one out as soon as it is complete and known to be needed; the first disc block (which holds the partition
/// table) and the WBFS header come last. The result is byte for byte what <see cref="WbfsWriter"/> makes from the
/// same disc.
/// </summary>
public sealed class StreamingWbfsBuilder : IRestoredDiscSink
{
    private const int BlockSize = WbfsFormat.BlockSize;
    private const int SectorsPerBlock = WbfsFormat.SectorsPerBlock;

    private readonly Func<WbfsChunk, CancellationToken, ValueTask> _output;
    private readonly bool[] _sectorUsed = new bool[WiiConstants.MaxDiscSectors];
    private readonly ushort[] _blockMap = new ushort[WbfsFormat.BlocksPerDisc];
    private readonly Dictionary<int, Pending> _pending = [];
    private readonly Queue<int> _ready = new();
    private int _decided;
    private int _stored;
    private bool _complete;

    /// <param name="output">Receives each finished piece of the WBFS file, in no particular order; its memory is reused afterwards.</param>
    public StreamingWbfsBuilder(Func<WbfsChunk, CancellationToken, ValueTask> output) => _output = output;

    /// <summary>Size of the finished WBFS file, all parts together; final once <see cref="CompleteAsync"/> has run.</summary>
    public long OutputSize => (long)(_stored + 1) * BlockSize;

    /// <summary>Disc blocks held in memory at the moment, waiting for data or for their fate to be known.</summary>
    public int BlocksHeld => _pending.Count;

    public bool KeepsOnlyUsedBlocks => true;

    public void MarkUsed(long discOffset, long length)
    {
        if (length <= 0)
            return;

        long first = discOffset / WiiConstants.SectorSize;
        long last = Math.Min((discOffset + length - 1) / WiiConstants.SectorSize, _sectorUsed.Length - 1);
        if (first < (long)_decided * SectorsPerBlock)
            throw new InvalidOperationException($"Sector 0x{first:X} was marked after its block had been decided.");
        for (long sector = first; sector <= last; sector++)
            _sectorUsed[sector] = true;
    }

    public void UsageFinalBefore(long discOffset)
    {
        int limit = (int)Math.Min(discOffset / BlockSize, _blockMap.Length);
        while (_decided < limit)
            Decide(_decided++);
    }

    public bool IsNeeded(long discOffset, long length)
    {
        int first = (int)(discOffset / BlockSize);
        int last = (int)((discOffset + length - 1) / BlockSize);
        for (int index = first; index <= last && index < _blockMap.Length; index++)
        {
            if (index >= _decided || _blockMap[index] != 0)
                return true;
        }

        return false;
    }

    public async ValueTask WriteAsync(long discOffset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await FlushReadyAsync(cancellationToken);
        while (!data.IsEmpty)
        {
            int index = (int)(discOffset / BlockSize);
            int within = (int)(discOffset % BlockSize);
            int count = Math.Min(data.Length, BlockSize - within);
            ReadOnlySpan<byte> part = data.Span[..count];
            data = data[count..];
            discOffset += count;

            // Blocks past what WBFS can map, and blocks already known to be padding, are not kept.
            if (index >= _blockMap.Length || (index < _decided && _blockMap[index] == 0))
                continue;

            if (!_pending.TryGetValue(index, out Pending? block))
                _pending[index] = block = new Pending();
            part.CopyTo(block.Data.AsSpan(within));
            block.Filled += count;
            if (block.Filled > BlockSize)
                throw new InvalidOperationException($"Disc block {index} was written more than once.");
            if (block.Filled == BlockSize && index != 0 && index < _decided)
                _ready.Enqueue(index);
        }

        await FlushReadyAsync(cancellationToken);
    }

    /// <summary>
    /// Writes what is still held back (the first disc block, its partition table trimmed to the kept partitions, and
    /// a last block the disc ends part-way through), then the WBFS header.
    /// </summary>
    public async ValueTask CompleteAsync(NKitDecodeResult disc, CancellationToken cancellationToken)
    {
        if (_complete)
            throw new InvalidOperationException("The WBFS image is already complete.");
        if (disc.DiscSize > (long)WiiConstants.MaxDiscSectors * WiiConstants.SectorSize)
            throw new InvalidDataException($"Image is larger than a dual-layer Wii disc ({disc.DiscSize:N0} bytes).");
        if (_sectorUsed.AsSpan(_blockMap.Length * SectorsPerBlock).Contains(true))
            throw new InvalidDataException("The image uses sectors beyond what a WBFS file can address.");

        UsageFinalBefore((disc.DiscSize + BlockSize - 1) / BlockSize * BlockSize);
        await FlushReadyAsync(cancellationToken);

        if (!_pending.TryGetValue(0, out Pending? first) || first.Filled != BlockSize)
            throw new InvalidDataException("The disc header was never written.");
        byte[] discStart = first.Data.AsSpan(0, WbfsFormat.DiscHeaderCopySize).ToArray();
        if (disc.KeptPartitions.Count != disc.Partitions.Count)
            WiiPartitionTable.Rewrite(first.Data.AsSpan(0, BlockSize), disc.KeptPartitions.Contains);

        foreach (int index in _pending.Keys.Order().ToArray())
        {
            Pending block = _pending[index];

            // The last block of the disc reads as zeros past the disc's end, as it does from an ISO file.
            long end = Math.Min((long)(index + 1) * BlockSize, disc.DiscSize) - (long)index * BlockSize;
            if (block.Filled != end)
                throw new InvalidDataException($"Disc block {index} was only partly restored ({block.Filled:N0} of {end:N0} bytes).");
            block.Data.AsSpan((int)end, BlockSize - (int)end).Clear();
            await EmitAsync(index, cancellationToken);
        }

        await _output(new WbfsChunk(0, BuildHeader(discStart)), cancellationToken);
        _complete = true;
    }

    private void Decide(int index)
    {
        if (!_sectorUsed.AsSpan(index * SectorsPerBlock, SectorsPerBlock).Contains(true))
        {
            if (_pending.Remove(index, out Pending? block))
                block.Dispose();
            return;
        }

        _blockMap[index] = (ushort)++_stored;
        if (index != 0 && _pending.TryGetValue(index, out Pending? ready) && ready.Filled == BlockSize)
            _ready.Enqueue(index);
    }

    private async ValueTask FlushReadyAsync(CancellationToken cancellationToken)
    {
        while (_ready.TryDequeue(out int index))
            await EmitAsync(index, cancellationToken);
    }

    private async ValueTask EmitAsync(int index, CancellationToken cancellationToken)
    {
        Pending block = _pending[index];
        await _output(new WbfsChunk((long)_blockMap[index] * BlockSize, block.Data.AsMemory(0, BlockSize)), cancellationToken);
        _pending.Remove(index);
        block.Dispose();
    }

    private byte[] BuildHeader(ReadOnlySpan<byte> discStart)
    {
        byte[] header = new byte[BlockSize];
        "WBFS"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)(OutputSize >> WbfsFormat.HdSectorShift));
        header[8] = WbfsFormat.HdSectorShift;
        header[9] = WbfsFormat.BlockShift;
        header[WbfsFormat.DiscTableOffset] = 1;

        Span<byte> discInfo = header.AsSpan(WbfsFormat.HdSectorSize);
        discStart.CopyTo(discInfo);
        Span<byte> map = discInfo[WbfsFormat.DiscHeaderCopySize..];
        for (int i = 0; i < _blockMap.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(map[(i * 2)..], _blockMap[i]);
        return header;
    }

    private sealed class Pending : IDisposable
    {
        public byte[] Data { get; } = ArrayPool<byte>.Shared.Rent(BlockSize);

        public int Filled { get; set; }

        public void Dispose() => ArrayPool<byte>.Shared.Return(Data);
    }
}
