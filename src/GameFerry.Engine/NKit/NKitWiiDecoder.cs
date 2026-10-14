using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GameFerry.Engine.Disc;

namespace GameFerry.Engine.NKit;

/// <summary>
/// Restores the original encrypted Wii disc from an NKit v1 image read front to back, the way NKit 1.4's
/// <c>NkitReaderWii</c> does: files put back at their original offsets, junk regenerated, hashes recalculated and
/// every group re-encrypted. Nothing is written twice: where NKit patches its output afterwards (the disc header,
/// each partition's file table, groups whose hashes it stored), those parts are held back and written once, final.
/// An update partition that NKit removed is not restored; its space is left as zeros, which no loader reads.
/// </summary>
public sealed class NKitWiiDecoder
{
    public const int HeaderAreaSize = 0x50000;
    private const int PartitionHeaderSize = 0x20000;
    private const int H3TableSize = 0x18000;
    private const long GroupDataSize = 0x7C00L * 64;
    private const uint DataType = 0;
    private const uint UpdateType = 1;
    private const uint NoPartition = uint.MaxValue;
    private const long WbfsBlockSize = 0x200000;

    private static readonly byte[] ZeroChunk = new byte[0x200000];

    private readonly NKitInput _input;
    private readonly long _inputLength;
    private readonly IRestoredDiscSink _sink;
    private readonly PartitionSelection _selection;
    private readonly IProgress<long>? _progress;
    private readonly Func<uint, Stream?>? _updatePartition;
    private readonly byte[] _buffer = new byte[0x200000];
    private readonly List<string> _warnings = [];
    private readonly List<WiiPartitionEntry> _kept = [];
    private readonly byte[] _lastHashes = new byte[WiiGroup.Sectors * WiiGroup.HashSize];
    private readonly bool[] _marked = new bool[WiiConstants.MaxDiscSectors];
    private bool _lastHashesReal = true;
    private int _junkSkippedIn = -1;
    private long _position;
    private int _groupsChecked;
    private int _groupsFailing;
    private int _groupsSkipped;

    private NKitWiiDecoder(
        Stream input, long inputLength, IRestoredDiscSink sink, PartitionSelection selection, IProgress<long>? progress, Func<uint, Stream?>? updatePartition)
    {
        _input = new NKitInput(input);
        _inputLength = inputLength;
        _sink = sink;
        _selection = selection;
        _progress = progress;
        _updatePartition = updatePartition;
    }

    /// <param name="input">The NKit image, read once from its first byte.</param>
    /// <param name="inputLength">Size of the NKit image.</param>
    /// <param name="selection">Which partitions to mark as used; all of them are restored either way.</param>
    /// <param name="progress">Receives how many bytes of the NKit image have been read.</param>
    /// <param name="updatePartition">
    /// Given the CRC NKit recorded for a removed update partition, a stream of that partition (one of NKit's recovery
    /// files), or null to leave its space as zeros. A restored update partition is never marked as used.
    /// </param>
    public static Task<NKitDecodeResult> DecodeAsync(
        Stream input, long inputLength, IRestoredDiscSink sink, PartitionSelection selection,
        IProgress<long>? progress = null, Func<uint, Stream?>? updatePartition = null, CancellationToken cancellationToken = default) =>
        new NKitWiiDecoder(input, inputLength, sink, selection, progress, updatePartition).RunAsync(cancellationToken);

    private async Task<NKitDecodeResult> RunAsync(CancellationToken cancellationToken)
    {
        byte[] header = _input.ReadBytes(HeaderAreaSize);
        string version = Encoding.ASCII.GetString(header, 0x200, 8);
        if (version != "NKIT v01")
            throw new InvalidDataException(version.StartsWith("NKIT", StringComparison.Ordinal) ? $"{version} images are not supported." : "Not an NKit image.");
        if (DiscHeader.Parse(header).Kind != DiscKind.Wii)
            throw new InvalidDataException("Not a Wii NKit image.");

        uint discCrc = ReadUInt32(header, 0x208);
        long discSize = ReadUInt32(header, 0x210) * 4L;
        uint updateCrc = ReadUInt32(header, 0x218);
        bool updateRemoved = updateCrc != 0;
        List<TableEntry> partitions = ReadTable(header, (int)WiiConstants.PartitionTableOffset);
        if (partitions.Count == 0)
            throw new InvalidDataException("NKit image lists no partitions.");

        // NKit's own fields, and the flags it sets to say the partitions are stored decrypted.
        header.AsSpan(0x200, 0x1C).Clear();
        header[0x60] = 0;
        header[0x61] = 0;

        Mark(0, HeaderAreaSize);
        _position = HeaderAreaSize;
        byte[]? updateFiller = null;
        byte[] lastId = header[..4];
        uint lastType = NoPartition;

        foreach (TableEntry partition in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (updateRemoved && _input.Position == HeaderAreaSize)
            {
                // What NKit kept in the update partition's place: the original partition table, then padding.
                updateFiller = _input.ReadBytes(partitions[0].Offset - _input.Position);
                long firstOffset = ReadTable(updateFiller, 0).FirstOrDefault(p => p.Type != UpdateType)?.Offset
                    ?? throw new InvalidDataException("NKit image's original partition table has no partition besides the update.");

                using Stream? update = _updatePartition?.Invoke(updateCrc);
                if (update is null)
                {
                    _warnings.Add("The update partition was removed when this NKit image was made, so its space is left empty.");
                }
                else
                {
                    if (_selection == PartitionSelection.All)
                        throw new NotSupportedException("A restored update partition cannot be kept in a streamed copy; drop the update partition.");
                    await CopyAsync(update, cancellationToken);
                }

                await WriteZerosAsync(firstOffset - _position, cancellationToken);
            }

            if (partition.Offset > _input.Position)
            {
                JunkGenerator junk = new(lastType == DataType ? lastId : header, header[6], lastType == UpdateType ? 0 : discSize);
                await WriteFillerAsync(junk, _position + 0x1C, cancellationToken);
                _input.Skip(partition.Offset - _input.Position);
            }

            partition.Restored = _position;
            lastId = await DecodePartitionAsync(partition, cancellationToken);
            lastType = partition.Type;
        }

        if (_input.Position < _inputLength)
        {
            JunkGenerator junk = new(lastType == DataType ? lastId : header, header[6], lastType == UpdateType ? 0 : discSize);
            await WriteFillerAsync(junk, lastType == UpdateType ? discSize : _position + 0x1C, cancellationToken);
        }

        if (_position != discSize)
            throw new InvalidDataException($"NKit image restored to {_position:N0} bytes instead of {discSize:N0}.");

        if (updateFiller is not null)
            updateFiller.AsSpan(0, 0x100).CopyTo(header.AsSpan((int)WiiConstants.PartitionTableOffset));
        else
        {
            foreach (TableEntry partition in partitions)
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(partition.EntryPosition), (uint)(partition.Restored / 4));
        }

        List<WiiPartitionEntry> table = [.. ReadTable(header, (int)WiiConstants.PartitionTableOffset).Select(p => new WiiPartitionEntry(p.Group, p.Offset, p.Type))];
        foreach (WiiPartitionEntry kept in _kept)
        {
            if (!table.Contains(kept))
                throw new InvalidDataException($"The {kept.TypeName} partition was restored at 0x{kept.Offset:X}, which the original partition table does not list.");
        }

        _sink.UsageFinalBefore(discSize);
        await _sink.WriteAsync(0, header, cancellationToken);
        _progress?.Report(_input.Position);
        return new NKitDecodeResult(discSize, discCrc, table, [.. _kept], updateRemoved, _groupsChecked, _groupsFailing, _groupsSkipped, [.. _warnings]);
    }

    /// <returns>The partition's own four-character ID, which seeds the junk after it if it is the game partition.</returns>
    private async Task<byte[]> DecodePartitionAsync(TableEntry partition, CancellationToken cancellationToken)
    {
        long start = _position;
        byte[] header = _input.ReadBytes(PartitionHeaderSize);
        long dataOffset = ReadUInt32(header, 0x2B8) * 4L;
        int h3Offset = (int)ReadUInt32(header, 0x2B4) * 4;
        if (dataOffset != PartitionHeaderSize || h3Offset < 0 || h3Offset + H3TableSize > PartitionHeaderSize)
            throw new InvalidDataException($"Partition at 0x{start:X} has a layout NKit does not store (data at 0x{dataOffset:X}).");

        WiiPartitionEntry entry = new(partition.Group, start, partition.Type);
        bool blank = !header.AsSpan(0, 0x2C0).ContainsAnyExcept((byte)0);
        bool keep = Selects(_selection, entry) && !blank;
        if (blank)
            _warnings.Add($"{entry.TypeName} partition at 0x{start:X} is blank in the source image and was left out.");

        byte[] titleKey = WiiPartition.DecryptTitleKey(header);
        byte[] h3 = header.AsSpan(h3Offset, H3TableSize).ToArray();
        CheckTmd(header, h3, entry);

        long dataStart = start + dataOffset;
        ScrubTracker scrub = new(titleKey);
        PartitionDataReader reader = new(_input, scrub, packedSize: ReadUInt32(header, 0x2BC) * 4L);
        PartitionUsage? usage = null;
        if (keep)
        {
            _kept.Add(entry);
            Mark(start, dataOffset);
            reader.HeaderRead += () => usage = PartitionUsage.Start(entry, dataStart, reader, Mark, _warnings);
            reader.FilePlaced += (offset, length) => usage?.MarkFile(offset, length);
        }

        if (_sink.KeepsOnlyUsedBlocks)
            reader.SkipJunk = (offset, length) => usage?.Deferred != true && CanSkipJunk(reader, dataStart, offset);

        // Two group buffers, so that a group is rebuilt only once the next one has been read: by then it is known
        // whether the blocks it shares with its neighbours are kept, and a group nobody keeps is never hashed or
        // encrypted at all. Group buffers start out zeroed for each partition, as NKit's does.
        using WiiGroup even = new(titleKey, h3);
        using WiiGroup odd = new(titleKey, h3);
        Array.Clear(_lastHashes);
        _lastHashesReal = true;
        _junkSkippedIn = -1;
        List<HeldRange> held = [];
        HeldRange? holding = null;
        PendingGroup? pending = null;
        byte[] id = new byte[4];
        long remaining = long.MaxValue;
        int firstFstGroup = 0;
        int lastFstGroup = 0;
        for (int index = 0; remaining > 0; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WiiGroup group = index % 2 == 0 ? even : odd;
            int sectors = (int)Math.Min(64L, remaining / 0x7C00);
            for (int s = 0; s < sectors; s++)
            {
                long sectorData = reader.Position;
                Span<byte> sector = group.Decrypted.AsSpan(s * WiiGroup.SectorSize + WiiGroup.HashSize, WiiGroup.SectorDataSize);
                reader.Read(sector);
                if (usage?.Deferred == true)
                    usage.Capture(sectorData, sector);
                if (remaining != long.MaxValue)
                    continue;

                // The partition's boot header carries NKit's record of the real data size; the header gets it back.
                remaining = reader.DataSize;
                Span<byte> boot = group.Decrypted.AsSpan(WiiGroup.HashSize, PartitionDataReader.BootHeaderSize);
                boot[..4].CopyTo(id);
                if (reader.HasNoId)
                {
                    firstFstGroup = lastFstGroup = -1;
                    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x2BC), (uint)(PartitionDataReader.DataToHashed(remaining) / 4));
                }
                else
                {
                    long fstOffset = ReadUInt32(boot, 0x424) * 4L;
                    long fstSize = ReadUInt32(boot, 0x428) * 4L;
                    firstFstGroup = (int)(fstOffset / GroupDataSize);
                    lastFstGroup = (int)((fstOffset + fstSize) / GroupDataSize);

                    // Sic: NKit adds the file table's size to the partition's disc offset here, not to its own offset.
                    if ((int)((start + fstSize) % GroupDataSize) == 0)
                        lastFstGroup--;

                    boot.Slice(0x210, 4).CopyTo(header.AsSpan(0x2BC));
                    boot.Slice(0x200, 0x1C).Clear();
                }

                sectors = (int)Math.Min(64L, remaining / 0x7C00);
                await _sink.WriteAsync(start, header, cancellationToken);
            }

            if (sectors < WiiGroup.Sectors)
                group.Decrypted.AsSpan(sectors * WiiGroup.SectorSize).Clear();

            group.Begin(index, sectors);
            int scrubbed = 0;
            for (int s = 0; s < sectors; s++)
            {
                if (scrub.IsScrubbedInOrder((long)index * WiiGroup.Size + s * WiiGroup.SectorSize, out byte value))
                {
                    group.MarkScrubbed(s, value);
                    scrubbed++;
                }
            }

            bool preserved = reader.IsPreserved((long)index * WiiGroup.Size);

            // NKit's patch ranges: from the file table's first group, or any group with stored hashes, onwards while
            // the groups are in the file table or have stored hashes.
            bool inFst = index >= firstFstGroup && index <= lastFstGroup;
            if (holding is null && (index == firstFstGroup || preserved))
                held.Add(holding = new HeldRange(index));
            else if (holding is not null && !inFst && !preserved)
                holding = null;

            PendingGroup current = new(group, scrubbed, preserved, holding) { JunkSkipped = _junkSkippedIn == index };
            remaining -= sectors * 0x7C00L;
            if (usage?.Deferred != true)
                _sink.UsageFinalBefore(dataStart + reader.Position / 0x7C00 * WiiGroup.SectorSize);
            if (pending is not null)
                await RebuildAsync(pending, current.InheritsHashes, dataStart, cancellationToken);
            pending = current;
            _progress?.Report(_input.Position);
        }

        reader.Finish();
        long hashedSize = ReadUInt32(header, 0x2BC) * 4L;
        _sink.UsageFinalBefore(dataStart + hashedSize);
        if (pending is not null)
            await RebuildAsync(pending, nextInherits: false, dataStart, cancellationToken);

        Dictionary<int, byte[]> storedHashes = ReadStoredHashes(reader);
        await WriteHeldAsync(held, reader, scrub, titleKey, h3, hashedSize, storedHashes, dataStart, cancellationToken);

        usage?.Verify(reader);
        _position = dataStart + hashedSize;
        return id;
    }

    /// <summary>
    /// Hashes and encrypts a group the way NKit 1.4 does, then writes it, or holds it for patching. A group whose
    /// blocks are all dropped is skipped, unless the next group inherits its hash blocks (NKit reuses one buffer, so a
    /// partly scrubbed group, whose hashes are not recalculated, encrypts the hash blocks the group before it left).
    /// </summary>
    private async Task RebuildAsync(PendingGroup pending, bool nextInherits, long dataStart, CancellationToken cancellationToken)
    {
        WiiGroup group = pending.Group;
        long discOffset = dataStart + (long)group.Index * WiiGroup.Size;
        bool needed = pending.Range is not null || _sink.IsNeeded(discOffset, group.UsedSize);

        // Junk is only skipped where nothing can be kept; if that ever turned out wrong, the copy would be wrong.
        if (needed && pending.JunkSkipped)
            throw new InvalidOperationException($"Group {group.Index} at 0x{discOffset:X} is needed, but its junk was not generated.");

        if (needed || nextInherits)
        {
            if (pending.Scrubbed == 0)
            {
                group.RecomputeHashes();
            }
            else if (pending.Scrubbed == group.UsedSectors)
            {
                group.CopyScrubbedHashes();
            }
            else
            {
                if (needed && !pending.Preserved && !_lastHashesReal)
                    throw new InvalidOperationException($"Group {group.Index} at 0x{discOffset:X} inherits hashes from a group whose junk was not generated.");
                group.SetHashBlocks(_lastHashes);
            }

            group.CopyHashBlocks(_lastHashes);
            _lastHashesReal = !pending.JunkSkipped;
        }

        if (!needed)
        {
            _groupsSkipped++;
            return;
        }

        group.Encrypt();
        bool checkable = pending.Scrubbed == 0 && !pending.Preserved;
        if (pending.Range is not null)
        {
            pending.Range.Groups.Add(new HeldGroup(group.Index, group.Encrypted.AsSpan(0, group.UsedSize).ToArray(), pending.Preserved)
            {
                Checkable = checkable,
                MatchesH3 = group.MatchesH3,
            });
        }
        else
        {
            CountHashCheck(checkable, group.MatchesH3);
            await _sink.WriteAsync(discOffset, group.Encrypted.AsMemory(0, group.UsedSize), cancellationToken);
        }
    }

    /// <summary>NKit's <c>ReadPatchData</c>: after the data, 0x400 bytes per sector of every group whose hashes it stored.</summary>
    private Dictionary<int, byte[]> ReadStoredHashes(PartitionDataReader reader)
    {
        Dictionary<int, byte[]> hashes = [];
        long hashedSize = reader.DataSize / 0x7C00 * 0x8000;
        for (long offset = 0; offset < hashedSize; offset += WiiGroup.Size)
        {
            if (!reader.IsPreserved(offset))
                continue;
            int sectors = (int)Math.Min(WiiGroup.Size, hashedSize - offset) / WiiGroup.SectorSize;
            hashes[(int)(offset / WiiGroup.Size)] = _input.ReadBytes(sectors * WiiGroup.HashSize);
        }

        return hashes;
    }

    /// <summary>
    /// NKit's <c>patchGroups</c>: the held groups, with the file table put back (its rebuilt hashes too) and stored
    /// hashes restored, written once final.
    /// </summary>
    private async Task WriteHeldAsync(
        List<HeldRange> held, PartitionDataReader reader, ScrubTracker scrub, byte[] titleKey, byte[] h3,
        long hashedSize, Dictionary<int, byte[]> storedHashes, long dataStart, CancellationToken cancellationToken)
    {
        if (held.Count == 0)
            return;

        using WiiGroup patcher = new(titleKey, h3);
        (int fstFirst, int fstLast) = reader.HasNoId ? (-1, -2) : reader.FstGroups();
        foreach (HeldRange range in held)
        {
            if (range.Start >= fstFirst && range.Start <= fstLast)
            {
                if (range.Start != fstFirst)
                    throw new InvalidDataException("NKit 1.4 cannot rebuild this image either: its file table patch would start part-way through the table.");
                PatchFileTable(patcher, range, reader, scrub, hashedSize);
            }

            foreach (HeldGroup group in range.Groups)
            {
                if (group.Preserved)
                {
                    patcher.Load(group.Index, group.Encrypted);
                    patcher.ApplyStoredHashes(storedHashes[group.Index], patcher.UsedSectors);
                    patcher.Encrypt(storedHashes: true);
                    patcher.Encrypted.AsSpan(0, patcher.UsedSize).CopyTo(group.Encrypted);
                }
                else if (!group.Patched)
                {
                    // Held only because it sits in a patch range; NKit rewrites it unchanged.
                    CountHashCheck(group.Checkable, group.MatchesH3);
                }

                await _sink.WriteAsync(dataStart + (long)group.Index * WiiGroup.Size, group.Encrypted, cancellationToken);
            }
        }
    }

    /// <summary>NKit's <c>fstPatch</c>: writes the corrected file table into its groups and rebuilds their hashes.</summary>
    private void PatchFileTable(WiiGroup patcher, HeldRange range, PartitionDataReader reader, ScrubTracker scrub, long hashedSize)
    {
        (int first, int last) = reader.FstGroups();
        byte[] fst = reader.Fst;
        long copied = 0;
        long target = PartitionDataReader.DataToHashed(reader.FstOffset) % WiiGroup.Size;
        for (int index = first; index <= last; index++)
        {
            int position = index - range.Start;
            if (position >= range.Groups.Count)
                break;

            HeldGroup group = range.Groups[position];
            int length = (int)Math.Min(WiiGroup.Size, hashedSize - (long)WiiGroup.Size * index);
            patcher.Load(index, group.Encrypted.AsSpan(0, Math.Min(length, group.Encrypted.Length)));
            while (copied != fst.Length && target != WiiGroup.Size)
            {
                target += WiiGroup.HashSize;
                int count = (int)Math.Min(fst.Length - copied, WiiGroup.SectorSize - target % WiiGroup.SectorSize);
                fst.AsSpan((int)copied, count).CopyTo(patcher.Decrypted.AsSpan((int)target, count));
                target += count;
                copied += count;
            }

            int sectors = patcher.UsedSectors;
            int scrubbed = 0;
            for (int s = 0; s < sectors; s++)
            {
                if (scrub.IsScrubbed((long)index * WiiGroup.Size + s * WiiGroup.SectorSize, out byte value))
                {
                    patcher.MarkScrubbed(s, value);
                    scrubbed++;
                }
            }

            if (scrubbed == 0)
                patcher.RecomputeHashes();
            else if (scrubbed == sectors)
                patcher.CopyScrubbedHashes();
            patcher.Encrypt();
            patcher.Encrypted.AsSpan(0, patcher.UsedSize).CopyTo(group.Encrypted);
            group.Patched = true;
            CountHashCheck(scrubbed == 0 && !group.Preserved, patcher.MatchesH3);
            target = 0;
        }
    }

    private void CountHashCheck(bool checkable, bool matches)
    {
        if (!checkable)
            return;
        _groupsChecked++;
        if (!matches)
            _groupsFailing++;
    }

    /// <summary>The disc's own guarantee for its hash table: the TMD holds the H3 table's SHA-1.</summary>
    private void CheckTmd(byte[] header, byte[] h3, WiiPartitionEntry entry)
    {
        int tmd = (int)ReadUInt32(header, 0x2A8) * 4;
        if (tmd <= 0 || tmd + 0x1F4 + 20 > header.Length)
            return;
        if (!SHA1.HashData(h3).AsSpan().SequenceEqual(header.AsSpan(tmd + 0x1E4 + 0x10, 20)))
            _warnings.Add($"{entry.TypeName} partition at 0x{entry.Offset:X}: its hash table does not match its TMD.");
    }

    private async Task WriteFillerAsync(JunkGenerator junk, long nullsPos, CancellationToken cancellationToken)
    {
        DiscFiller filler = new(_input, junk, _position, nullsPos);
        int count;
        while ((count = filler.ReadAvailable(_buffer)) > 0)
        {
            await _sink.WriteAsync(_position, _buffer.AsMemory(0, count), cancellationToken);
            _position += count;
            _sink.UsageFinalBefore(_position);
            _progress?.Report(_input.Position);
        }
    }

    private async Task CopyAsync(Stream source, CancellationToken cancellationToken)
    {
        int count;
        while ((count = await source.ReadAsync(_buffer, cancellationToken)) > 0)
        {
            await _sink.WriteAsync(_position, _buffer.AsMemory(0, count), cancellationToken);
            _position += count;
            _sink.UsageFinalBefore(_position);
        }
    }

    private async Task WriteZerosAsync(long length, CancellationToken cancellationToken)
    {
        while (length > 0)
        {
            int count = (int)Math.Min(length, ZeroChunk.Length);
            await _sink.WriteAsync(_position, ZeroChunk.AsMemory(0, count), cancellationToken);
            _position += count;
            length -= count;
            _sink.UsageFinalBefore(_position);
        }
    }

    private static bool Selects(PartitionSelection selection, WiiPartitionEntry partition) => selection switch
    {
        PartitionSelection.RemoveUpdate => !partition.IsUpdate,
        PartitionSelection.GameOnly => partition.IsData,
        _ => true,
    };

    /// <summary>A partition table read from <paramref name="data"/>, where <paramref name="tableArea"/> holds disc offset 0x40000.</summary>
    private static List<TableEntry> ReadTable(byte[] data, int tableArea)
    {
        List<TableEntry> entries = [];
        for (int group = 0; group < WiiPartitionTable.GroupCount; group++)
        {
            uint count = ReadUInt32(data, tableArea + group * 8);
            if (count == 0)
                continue;
            long table = ReadUInt32(data, tableArea + group * 8 + 4) * 4L;
            int at = (int)(tableArea + table - WiiConstants.PartitionTableOffset);
            if (count > 64 || at < tableArea || at + count * 8 > data.Length)
                throw new InvalidDataException($"Partition table group {group} is not valid.");

            for (int i = 0; i < count; i++)
            {
                entries.Add(new TableEntry
                {
                    Group = group,
                    Offset = ReadUInt32(data, at + i * 8) * 4L,
                    Type = ReadUInt32(data, at + i * 8 + 4),
                    EntryPosition = (int)(table + i * 8),
                });
            }
        }

        return entries;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

    private sealed class TableEntry
    {
        public int Group;

        /// <summary>The partition's offset in the NKit image (or, read from the original table, on the disc).</summary>
        public long Offset;
        public uint Type;

        /// <summary>Where the entry's offset field is in the disc header.</summary>
        public int EntryPosition;

        /// <summary>The partition's offset on the restored disc.</summary>
        public long Restored;
    }

    /// <summary>
    /// Whether junk at <paramref name="offset"/> (in the partition's data area) can only land in blocks that will be
    /// dropped: the blocks its group touches have nothing marked in them, and they all end before the current gap
    /// does, so no file can be put back in them later either.
    /// </summary>
    private bool CanSkipJunk(PartitionDataReader reader, long dataStart, long offset)
    {
        int group = (int)(offset / GroupDataSize);
        long groupStart = dataStart + (long)group * WiiGroup.Size;
        long firstBlock = groupStart / WbfsBlockSize;
        long blocksEnd = ((groupStart + WiiGroup.Size - 1) / WbfsBlockSize + 1) * WbfsBlockSize;

        // The first sector a file placed after this gap could mark.
        long firstPossible = dataStart + reader.GapEnd / 0x7C00 * WiiGroup.SectorSize;
        if (blocksEnd > firstPossible)
            return false;

        int firstSector = (int)(firstBlock * WbfsBlockSize / WiiGroup.SectorSize);
        int lastSector = (int)Math.Min(blocksEnd / WiiGroup.SectorSize, _marked.Length);
        if (_marked.AsSpan(firstSector, lastSector - firstSector).Contains(true))
            return false;

        _junkSkippedIn = group;
        return true;
    }

    private void Mark(long discOffset, long length)
    {
        if (length <= 0)
            return;
        long first = discOffset / WiiGroup.SectorSize;
        long last = Math.Min((discOffset + length - 1) / WiiGroup.SectorSize, _marked.Length - 1);
        if (first <= last)
            _marked.AsSpan((int)first, (int)(last - first + 1)).Fill(true);
        _sink.MarkUsed(discOffset, length);
    }

    /// <summary>A group read but not yet rebuilt.</summary>
    private sealed record PendingGroup(WiiGroup Group, int Scrubbed, bool Preserved, HeldRange? Range)
    {
        /// <summary>A partly scrubbed group: NKit keeps whatever hash blocks its buffer already held.</summary>
        public bool InheritsHashes => Scrubbed > 0 && Scrubbed < Group.UsedSectors;

        /// <summary>Some of the group's junk was left as zeros because nothing in its blocks can be kept.</summary>
        public bool JunkSkipped { get; init; }
    }

    private sealed class HeldRange(int start)
    {
        public int Start { get; } = start;

        public List<HeldGroup> Groups { get; } = [];
    }

    private sealed class HeldGroup(int index, byte[] encrypted, bool preserved)
    {
        public int Index { get; } = index;

        public byte[] Encrypted { get; } = encrypted;

        public bool Preserved { get; } = preserved;

        public bool Patched { get; set; }

        /// <summary>Whether the first pass rebuilt this group's hashes, and so whether its H3 result means anything.</summary>
        public bool Checkable { get; init; }

        public bool MatchesH3 { get; init; }
    }
}
