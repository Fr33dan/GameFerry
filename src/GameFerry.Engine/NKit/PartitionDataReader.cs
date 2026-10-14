using System.Buffers.Binary;
using System.Text;

namespace GameFerry.Engine.NKit;

/// <summary>
/// NKit's <c>partitionStreamWrite</c>: rebuilds one partition's decrypted data area (no hashes, 0x7C00 bytes per
/// sector) from the NKit image, putting every file back at its original offset and regenerating the junk between
/// them. The file table it carries is in NKit's packed layout; <see cref="Fst"/> is corrected as files are placed.
/// </summary>
internal sealed class PartitionDataReader : RestoredStream
{
    public const int BootHeaderSize = 0x440;
    private const long GroupDataSize = 0x7C00L * 64;

    private readonly long _packedSize;
    private readonly long _start;

    /// <param name="packedSize">Size of the partition's data in the NKit image, from its (NKit-modified) header.</param>
    public PartitionDataReader(NKitInput input, ScrubTracker scrub, long packedSize)
        : base(input, scrub, start: 0)
    {
        _packedSize = packedSize;
        _start = input.Position;
    }

    /// <summary>Raised once the boot header, everything up to the file table, and the file table have been read.</summary>
    public event Action? HeaderRead;

    /// <summary>Raised as each file is put back: its offset in the data area and its length, as the file table will say.</summary>
    public event Action<long, long>? FilePlaced;

    /// <summary>Size of the rebuilt data area; known once <see cref="HeaderRead"/> has been raised.</summary>
    public long DataSize { get; private set; }

    /// <summary>True for a partition whose data starts with an all-zero ID (NKit then stores it as one gap).</summary>
    public bool HasNoId { get; private set; }

    public byte[] BootHeader { get; private set; } = [];

    /// <summary>Everything between the boot header and the file table (bi2, apploader, main DOL), unchanged.</summary>
    public byte[] HeaderToFst { get; private set; } = [];

    /// <summary>The file table: NKit's packed one until the last file is placed, the original one after that.</summary>
    public byte[] Fst { get; private set; } = [];

    public long FstOffset { get; private set; }

    /// <summary>One bit per 2 MiB group (hashed coordinates), set where NKit stored the group's hashes.</summary>
    public byte[] PreservedHashFlags { get; private set; } = [];

    public bool IsPreserved(long hashedOffset)
    {
        long group = hashedOffset / WiiGroup.Size;
        long index = group / 8;
        return index < PreservedHashFlags.Length && (PreservedHashFlags[index] & (1 << (7 - (int)(group % 8)))) != 0;
    }

    private long Consumed => Input.Position - _start;

    protected override IEnumerable<Segment> Produce()
    {
        byte[] header = Input.ReadBytes(BootHeaderSize);
        BootHeader = header;

        if (BinaryPrimitives.ReadUInt32BigEndian(header) == 0)
        {
            HasNoId = true;
            long hashedSize = Input.ReadUInt32() * 4L;
            DataSize = HashedToData(hashedSize);
            Junk = new JunkGenerator(header, header[6], DataSize);
            HeaderRead?.Invoke();

            yield return FromMemory(header);
            GapState whole = new() { FileLength = -1, GapLength = -1, NullsPos = 0 };
            foreach (Segment segment in Gap(whole, firstOrLastFile: true))
                yield return segment;
            yield break;
        }

        string version = Encoding.ASCII.GetString(header, 0x200, 8);
        if (version != "NKIT v01")
            throw new InvalidDataException($"Partition data is '{version.TrimEnd('\0')}', not an NKit v1 partition.");

        DataSize = HashedToData(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x210)) * 4L);
        Junk = new JunkGenerator(header, header[6], DataSize);

        FstOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x424)) * 4L;
        HeaderToFst = Input.ReadBytes(FstOffset - BootHeaderSize);
        byte[] fst = Input.ReadBytes(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x428)) * 4L);
        Fst = fst;
        PreservedHashFlags = Input.ReadBytes(HashFlagsLength(DataSize));
        HeaderRead?.Invoke();

        yield return FromMemory(header);
        yield return FromMemory(HeaderToFst);
        yield return FromMemory(fst);

        long nullsPos = Position + 0x1C;
        List<PackedFile>? files = NKitFst.PackedLayout(_packedSize, FstOffset, fst);
        if (files is null)
        {
            // NKit treats an unreadable file table as one gap covering the rest of the partition.
            GapState rest = new() { FileLength = fst.Length, GapLength = DataSize - Consumed, NullsPos = nullsPos };
            foreach (Segment segment in Gap(rest, firstOrLastFile: true))
                yield return segment;
            yield break;
        }

        // The hash flags sit in the gap after the file table, which the packed offsets don't know about.
        files[0].GapLength -= PreservedHashFlags.Length;
        for (int i = 0; i < files.Count; i++)
        {
            PackedFile file = files[i];
            bool first = i == 0;
            if (!first)
            {
                if (Consumed < file.Offset)
                    Input.Skip(file.Offset - Consumed);

                BinaryPrimitives.WriteUInt32BigEndian(fst.AsSpan(file.TableEntry), (uint)(Position / 4));
                if (file.Length > 0)
                {
                    FilePlaced?.Invoke(Position, file.Length);
                    long size = file.Length + (file.Length % 4 == 0 ? 0 : 4 - file.Length % 4);
                    yield return FromInput(size);
                    nullsPos = Position + 0x1C;
                }
            }

            if (Position < DataSize)
            {
                GapState gap = new() { FileLength = file.Length, GapLength = file.GapLength, NullsPos = nullsPos };
                foreach (Segment segment in Gap(gap, first || i == files.Count - 1))
                {
                    if (gap.JunkFileAt is { } at && !first)
                    {
                        // A file made entirely of junk: NKit dropped it and recorded only its length.
                        FilePlaced?.Invoke(at, gap.FileLength);
                        gap.JunkFileAt = null;
                    }

                    yield return segment;
                }

                nullsPos = gap.NullsPos;
                file.Length = gap.FileLength;
                file.GapLength = gap.GapLength;
                if (!first)
                    BinaryPrimitives.WriteUInt32BigEndian(fst.AsSpan(file.TableEntry + 4), (uint)file.Length);
            }
        }
    }

    /// <summary>NKit's hash flags: one bit per group of the hashed partition, rounded up to whole 32-bit words.</summary>
    private static int HashFlagsLength(long dataSize)
    {
        long hashed = dataSize / 0x7C00 * 0x8000;
        long groups = hashed / WiiGroup.Size + (hashed % WiiGroup.Size == 0 ? 0 : 1);
        return (int)(groups / 32 + (groups % 32 == 0 ? 0 : 1)) * 4;
    }

    public static long HashedToData(long hashed) => hashed / 0x8000 * 0x7C00 + hashed % 0x8000;

    public static long DataToHashed(long data) => data / 0x7C00 * 0x8000 + data % 0x7C00;

    /// <summary>NKit's first and last group of the file table, as its patch pass computes them.</summary>
    public (int First, int Last) FstGroups()
    {
        int first = (int)(FstOffset / GroupDataSize);
        int last = (int)((FstOffset + Fst.Length) / GroupDataSize);
        if ((FstOffset + Fst.Length) % GroupDataSize == 0)
            last--;
        return (first, last);
    }
}

/// <summary>A file as NKit's packed file table lists it, with the packed gap that follows it.</summary>
internal sealed class PackedFile
{
    public long Offset;
    public long Length;
    public long GapLength;

    /// <summary>Byte offset of the file's entry offset field in the file table; -1 for the file table itself.</summary>
    public int TableEntry;
}

internal static class NKitFst
{
    private const int EntrySize = 12;

    /// <summary>
    /// NKit's <c>GetConvertFstFiles</c>: the files in packed order (by offset, then length), each with the gap that
    /// follows it, starting with the file table itself. Null where NKit would treat the partition as unreadable.
    /// </summary>
    public static List<PackedFile>? PackedLayout(long packedSize, long fstOffset, byte[] fst)
    {
        try
        {
            List<PackedFile> sorted = [.. Files(fst).OrderBy(f => DataToOffset(f.Offset)).ThenBy(f => f.Length)];
            List<PackedFile> layout = [];
            PackedFile previous = new() { Offset = fstOffset, Length = fst.Length, TableEntry = -1 };
            foreach (PackedFile file in sorted)
            {
                long end = Align4(previous.Offset + previous.Length);
                long gap = file.Offset - end;
                if (gap < 0)
                    return null;
                previous.GapLength = gap;
                layout.Add(previous);
                previous = file;
            }

            PackedFile last = sorted[^1];
            long tail = packedSize - Align4(last.Offset + last.Length);
            if (tail is >= -3 and < 0)
                tail = 0;
            if (tail < 0)
                return null;
            last.GapLength = tail;
            layout.Add(last);
            return layout;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The files of a file table in the order NKit's FileSystem flattens them: each folder's own files, then its
    /// sub-folders depth first. The order only matters for ties, which a stable sort then keeps.
    /// </summary>
    private static List<PackedFile> Files(byte[] fst)
    {
        long count = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(8));
        if (EntrySize * count > fst.Length)
            throw new InvalidOperationException("file table too short");

        Folder root = new();
        Walk(fst, root, 0);
        List<PackedFile> files = [];
        Flatten(root, files);
        return files;
    }

    private static uint Walk(byte[] fst, Folder folder, uint index)
    {
        ReadOnlySpan<byte> entry = fst.AsSpan((int)(EntrySize * index), EntrySize);
        bool isFolder = entry[0] == 1;
        uint size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
        if (isFolder)
        {
            Folder target = folder;
            if (index != 0)
            {
                target = new Folder();
                folder.Folders.Add(target);
            }

            for (uint next = index + 1; next < size;)
            {
                uint after = Walk(fst, target, next);
                if (after <= next)
                    throw new InvalidOperationException("file table folder does not advance");
                next = after;
            }

            return size;
        }

        folder.Files.Add(new PackedFile
        {
            Offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]) * 4L,
            Length = size,
            TableEntry = (int)(EntrySize * index + 4),
        });
        return index + 1;
    }

    private static void Flatten(Folder folder, List<PackedFile> files)
    {
        files.AddRange(folder.Files);
        foreach (Folder child in folder.Folders)
            Flatten(child, files);
    }

    private static long DataToOffset(long data) => data / 0x7C00 * 0x8000 + data % 0x7C00 + 0x400;

    private static long Align4(long value) => value + (value % 4 == 0 ? 0 : 4 - value % 4);

    private sealed class Folder
    {
        public List<PackedFile> Files { get; } = [];

        public List<Folder> Folders { get; } = [];
    }
}
