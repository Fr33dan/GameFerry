using System.Buffers.Binary;
using System.Text;

namespace GameFerry.Engine.Archives;

/// <summary>One step of a 7z folder's decoding pipeline: a method ID (LZMA is 03 01 01, LZMA2 is 21) and its properties.</summary>
public sealed record SevenZipCoder(byte[] MethodId, byte[] Properties)
{
    public static readonly byte[] Lzma = [0x03, 0x01, 0x01];
    public static readonly byte[] Lzma2 = [0x21];
    public static readonly byte[] Copy = [0x00];

    public bool Is(byte[] method) => MethodId.AsSpan().SequenceEqual(method);
}

/// <summary>A 7z "folder": a stream packed as one unit, holding one file or, in a solid archive, several in a row.</summary>
/// <param name="PackOffset">Where the packed stream starts in the archive file.</param>
/// <param name="IsSimple">One coder reading one packed stream: what a single LZMA or LZMA2 decoder can unpack.</param>
public sealed record SevenZipFolder(IReadOnlyList<SevenZipCoder> Coders, long PackOffset, long PackSize, long UnpackSize, uint? Crc, bool IsSimple);

/// <param name="OffsetInFolder">How far into its folder's unpacked stream the file starts.</param>
public sealed record SevenZipEntry(string Name, long Size, uint? Crc, int Folder, long OffsetInFolder, bool IsDirectory);

/// <summary>
/// Reads the table of contents of a 7z archive (7zFormat.txt), enough to stream one file out of it without a general
/// 7z library: which folder it is in, where that folder's packed bytes are and which coder packed them.
/// </summary>
public sealed class SevenZipArchive
{
    private const int SignatureHeaderSize = 32;
    private static readonly byte[] Signature = [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C];

    private SevenZipArchive(List<SevenZipFolder> folders, List<SevenZipEntry> entries)
    {
        Folders = folders;
        Entries = entries;
    }

    public IReadOnlyList<SevenZipFolder> Folders { get; }

    public IReadOnlyList<SevenZipEntry> Entries { get; }

    public static bool HasSignature(ReadOnlySpan<byte> start) => start.Length >= Signature.Length && start[..Signature.Length].SequenceEqual(Signature);

    /// <param name="archive">The archive, seekable.</param>
    /// <param name="unpack">
    /// Unpacks a simple folder completely: needed when the archive's own table of contents is compressed, as it is by
    /// default. Without one, such archives are refused.
    /// </param>
    public static SevenZipArchive Read(Stream archive, Func<SevenZipFolder, Stream, byte[]>? unpack)
    {
        byte[] start = new byte[SignatureHeaderSize];
        archive.Position = 0;
        archive.ReadExactly(start);
        if (!HasSignature(start))
            throw new InvalidDataException("Not a 7z archive.");

        long nextOffset = BinaryPrimitives.ReadInt64LittleEndian(start.AsSpan(12));
        long nextSize = BinaryPrimitives.ReadInt64LittleEndian(start.AsSpan(20));
        if (nextOffset < 0 || nextSize <= 0 || nextSize > 64 << 20 || SignatureHeaderSize + nextOffset + nextSize > archive.Length)
            throw new InvalidDataException("7z archive's table of contents is out of range.");

        byte[] header = new byte[nextSize];
        archive.Position = SignatureHeaderSize + nextOffset;
        archive.ReadExactly(header);

        for (int depth = 0; ; depth++)
        {
            Reader reader = new(header);
            byte id = reader.Byte();
            if (id == PropertyId.Header)
                return ParseHeader(reader);
            if (id != PropertyId.EncodedHeader || depth > 3)
                throw new InvalidDataException($"7z table of contents starts with 0x{id:X2}.");

            // A compressed table of contents: a streams description, whose single folder unpacks to the real one.
            (List<SevenZipFolder> folders, _) = ParseStreamsInfo(reader);
            if (folders.Count == 0 || !folders[0].IsSimple)
                throw new InvalidDataException("7z table of contents is packed in a way this reader does not handle.");
            if (unpack is null)
                throw new NotSupportedException("This 7z archive's table of contents is compressed.");
            header = unpack(folders[0], archive);
        }
    }

    private static SevenZipArchive ParseHeader(Reader reader)
    {
        List<SevenZipFolder> folders = [];
        List<(long Size, uint? Crc)> substreams = [];
        List<int> substreamFolders = [];
        List<string> names = [];
        bool[]? emptyStream = null;
        bool[]? emptyFile = null;
        int fileCount = 0;

        for (byte id = reader.Byte(); id != PropertyId.End; id = reader.Byte())
        {
            switch (id)
            {
                case PropertyId.ArchiveProperties:
                    for (byte property = reader.Byte(); property != PropertyId.End; property = reader.Byte())
                        reader.Skip(reader.Number());
                    break;
                case PropertyId.AdditionalStreamsInfo:
                    ParseStreamsInfo(reader);
                    break;
                case PropertyId.MainStreamsInfo:
                    (folders, List<(long, uint?, int)> streams) = ParseStreamsInfo(reader);
                    foreach ((long size, uint? crc, int folder) in streams)
                    {
                        substreams.Add((size, crc));
                        substreamFolders.Add(folder);
                    }

                    break;
                case PropertyId.FilesInfo:
                    fileCount = (int)reader.Number();
                    for (byte property = (byte)reader.Number(); property != PropertyId.End; property = (byte)reader.Number())
                    {
                        long size = reader.Number();
                        int end = reader.Position + (int)size;
                        switch (property)
                        {
                            case PropertyId.EmptyStream:
                                emptyStream = reader.Bits(fileCount);
                                break;
                            case PropertyId.EmptyFile:
                                emptyFile = reader.Bits(emptyStream?.Count(e => e) ?? 0);
                                break;
                            case PropertyId.Name:
                                if (reader.Byte() != 0)
                                    throw new InvalidDataException("7z file names stored outside the header are not supported.");
                                for (int i = 0; i < fileCount; i++)
                                    names.Add(reader.Utf16String());
                                break;
                        }

                        reader.Position = end;
                    }

                    break;
                default:
                    throw new InvalidDataException($"Unexpected 0x{id:X2} in the 7z table of contents.");
            }
        }

        // Files with data take the unpacked streams in order; the others (folders, empty files) have none.
        List<SevenZipEntry> entries = [];
        int stream = 0;
        int emptyIndex = 0;
        long offsetInFolder = 0;
        int lastFolder = -1;
        for (int i = 0; i < fileCount; i++)
        {
            string name = i < names.Count ? names[i] : $"file{i}";
            if (emptyStream is not null && emptyStream[i])
            {
                bool isFile = emptyFile is not null && emptyIndex < emptyFile.Length && emptyFile[emptyIndex];
                emptyIndex++;
                entries.Add(new SevenZipEntry(name, 0, null, -1, 0, IsDirectory: !isFile));
                continue;
            }

            if (stream >= substreams.Count)
                throw new InvalidDataException("7z archive lists more files than it has data for.");
            int folder = substreamFolders[stream];
            if (folder != lastFolder)
            {
                offsetInFolder = 0;
                lastFolder = folder;
            }

            (long size, uint? crc) = substreams[stream++];
            entries.Add(new SevenZipEntry(name, size, crc, folder, offsetInFolder, IsDirectory: false));
            offsetInFolder += size;
        }

        return new SevenZipArchive(folders, entries);
    }

    /// <returns>The folders, and every unpacked stream as (size, CRC, folder index).</returns>
    private static (List<SevenZipFolder> Folders, List<(long Size, uint? Crc, int Folder)> Streams) ParseStreamsInfo(Reader reader)
    {
        long packPosition = 0;
        List<long> packSizes = [];
        List<FolderLayout> layouts = [];
        List<long> unpackSizes = [];
        uint?[] folderCrcs = [];
        int[]? streamsPerFolder = null;
        List<long>? substreamSizes = null;
        uint?[]? substreamCrcs = null;

        for (byte id = reader.Byte(); id != PropertyId.End; id = reader.Byte())
        {
            switch (id)
            {
                case PropertyId.PackInfo:
                    packPosition = reader.Number();
                    int packCount = (int)reader.Number();
                    for (byte property = reader.Byte(); property != PropertyId.End; property = reader.Byte())
                    {
                        if (property == PropertyId.Size)
                        {
                            for (int i = 0; i < packCount; i++)
                                packSizes.Add(reader.Number());
                        }
                        else if (property == PropertyId.Crc)
                        {
                            reader.Digests(packCount);
                        }
                    }

                    break;
                case PropertyId.UnpackInfo:
                    if (reader.Byte() != PropertyId.Folder)
                        throw new InvalidDataException("7z unpack information has no folders.");
                    int folderCount = (int)reader.Number();
                    if (reader.Byte() != 0)
                        throw new InvalidDataException("7z folders stored outside the header are not supported.");
                    for (int i = 0; i < folderCount; i++)
                        layouts.Add(FolderLayout.Read(reader));
                    for (byte property = reader.Byte(); property != PropertyId.End; property = reader.Byte())
                    {
                        if (property == PropertyId.CodersUnpackSize)
                        {
                            foreach (FolderLayout layout in layouts)
                            {
                                for (int i = 0; i < layout.OutStreams; i++)
                                    layout.UnpackSizes.Add(reader.Number());
                            }
                        }
                        else if (property == PropertyId.Crc)
                        {
                            folderCrcs = reader.Digests(folderCount);
                        }
                    }

                    break;
                case PropertyId.SubStreamsInfo:
                    streamsPerFolder = [.. Enumerable.Repeat(1, layouts.Count)];
                    for (byte property = reader.Byte(); property != PropertyId.End; property = reader.Byte())
                    {
                        if (property == PropertyId.NumUnpackStream)
                        {
                            for (int i = 0; i < layouts.Count; i++)
                                streamsPerFolder[i] = (int)reader.Number();
                        }
                        else if (property == PropertyId.Size)
                        {
                            substreamSizes = [];
                            for (int i = 0; i < layouts.Count; i++)
                            {
                                for (int j = 1; j < streamsPerFolder[i]; j++)
                                    substreamSizes.Add(reader.Number());
                            }
                        }
                        else if (property == PropertyId.Crc)
                        {
                            int unknown = 0;
                            for (int i = 0; i < layouts.Count; i++)
                            {
                                if (streamsPerFolder[i] != 1 || i >= folderCrcs.Length || folderCrcs[i] is null)
                                    unknown += streamsPerFolder[i];
                            }

                            substreamCrcs = reader.Digests(unknown);
                        }
                    }

                    break;
                default:
                    throw new InvalidDataException($"Unexpected 0x{id:X2} in a 7z streams description.");
            }
        }

        List<SevenZipFolder> folders = [];
        long packOffset = SignatureHeaderSize + packPosition;
        int packIndex = 0;
        for (int i = 0; i < layouts.Count; i++)
        {
            FolderLayout layout = layouts[i];
            long packSize = 0;
            long folderPackOffset = packOffset;
            for (int p = 0; p < layout.PackedStreams; p++)
            {
                long size = packIndex < packSizes.Count ? packSizes[packIndex++] : 0;
                packSize += size;
                packOffset += size;
            }

            folders.Add(new SevenZipFolder(
                layout.Coders, folderPackOffset, packSize, layout.FinalUnpackSize, i < folderCrcs.Length ? folderCrcs[i] : null, layout.IsSimple));
        }

        // Without a substreams description each folder holds exactly one stream.
        List<(long, uint?, int)> streams = [];
        int sizeIndex = 0;
        int crcIndex = 0;
        for (int i = 0; i < folders.Count; i++)
        {
            int count = streamsPerFolder?[i] ?? 1;
            long left = folders[i].UnpackSize;
            bool folderCrcKnown = count == 1 && folders[i].Crc is not null;
            for (int j = 0; j < count; j++)
            {
                long size = j < count - 1 ? substreamSizes![sizeIndex++] : left;
                left -= size;
                uint? crc = folderCrcKnown ? folders[i].Crc : substreamCrcs is not null && crcIndex < substreamCrcs.Length ? substreamCrcs[crcIndex++] : null;
                streams.Add((size, crc, i));
            }
        }

        return (folders, streams);
    }

    private sealed class FolderLayout
    {
        public List<SevenZipCoder> Coders { get; } = [];

        public List<long> UnpackSizes { get; } = [];

        public int OutStreams { get; private set; }

        public int PackedStreams { get; private set; }

        public bool IsSimple { get; private set; }

        private readonly HashSet<int> _boundOut = [];

        public long FinalUnpackSize
        {
            get
            {
                for (int i = 0; i < UnpackSizes.Count; i++)
                {
                    if (!_boundOut.Contains(i))
                        return UnpackSizes[i];
                }

                return UnpackSizes.Count > 0 ? UnpackSizes[^1] : 0;
            }
        }

        public static FolderLayout Read(Reader reader)
        {
            FolderLayout layout = new();
            int coderCount = (int)reader.Number();
            int inTotal = 0;
            for (int c = 0; c < coderCount; c++)
            {
                byte flags = reader.Byte();
                if ((flags & 0x80) != 0)
                    throw new InvalidDataException("7z alternative coders are not supported.");
                byte[] method = reader.Bytes(flags & 0x0F);
                int inStreams = 1;
                int outStreams = 1;
                if ((flags & 0x10) != 0)
                {
                    inStreams = (int)reader.Number();
                    outStreams = (int)reader.Number();
                }

                byte[] properties = (flags & 0x20) != 0 ? reader.Bytes((int)reader.Number()) : [];
                layout.Coders.Add(new SevenZipCoder(method, properties));
                inTotal += inStreams;
                layout.OutStreams += outStreams;
            }

            int bindPairs = layout.OutStreams - 1;
            for (int b = 0; b < bindPairs; b++)
            {
                reader.Number();
                layout._boundOut.Add((int)reader.Number());
            }

            layout.PackedStreams = inTotal - bindPairs;
            if (layout.PackedStreams > 1)
            {
                for (int p = 0; p < layout.PackedStreams; p++)
                    reader.Number();
            }

            layout.IsSimple = coderCount == 1 && inTotal == 1 && layout.OutStreams == 1;
            return layout;
        }
    }

    /// <summary>The 7z header's primitives: variable-length numbers, bit vectors, digests, UTF-16 names.</summary>
    private sealed class Reader(byte[] data)
    {
        public int Position { get; set; }

        public byte Byte() => Position < data.Length ? data[Position++] : throw new InvalidDataException("7z table of contents ends early.");

        public byte[] Bytes(int count)
        {
            if (count < 0 || Position + count > data.Length)
                throw new InvalidDataException("7z table of contents ends early.");
            byte[] bytes = data.AsSpan(Position, count).ToArray();
            Position += count;
            return bytes;
        }

        public void Skip(long count)
        {
            if (count < 0 || Position + count > data.Length)
                throw new InvalidDataException("7z table of contents ends early.");
            Position += (int)count;
        }

        /// <summary>7z's NUMBER: the first byte's leading 1-bits say how many little-endian bytes follow.</summary>
        public long Number()
        {
            byte first = Byte();
            byte mask = 0x80;
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((first & mask) == 0)
                {
                    value |= (ulong)(first & (mask - 1)) << (8 * i);
                    return checked((long)value);
                }

                value |= (ulong)Byte() << (8 * i);
                mask >>= 1;
            }

            return checked((long)value);
        }

        public bool[] Bits(int count)
        {
            bool[] bits = new bool[count];
            byte current = 0;
            byte mask = 0;
            for (int i = 0; i < count; i++)
            {
                if (mask == 0)
                {
                    current = Byte();
                    mask = 0x80;
                }

                bits[i] = (current & mask) != 0;
                mask >>= 1;
            }

            return bits;
        }

        public uint?[] Digests(int count)
        {
            bool[] defined = Byte() != 0 ? [.. Enumerable.Repeat(true, count)] : Bits(count);
            uint?[] digests = new uint?[count];
            for (int i = 0; i < count; i++)
            {
                if (defined[i])
                    digests[i] = BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
            }

            return digests;
        }

        public string Utf16String()
        {
            int start = Position;
            while (Position + 1 < data.Length && (data[Position] != 0 || data[Position + 1] != 0))
                Position += 2;
            string text = Encoding.Unicode.GetString(data, start, Position - start);
            Position += 2;
            return text;
        }
    }

    private static class PropertyId
    {
        public const byte End = 0x00;
        public const byte Header = 0x01;
        public const byte ArchiveProperties = 0x02;
        public const byte AdditionalStreamsInfo = 0x03;
        public const byte MainStreamsInfo = 0x04;
        public const byte FilesInfo = 0x05;
        public const byte PackInfo = 0x06;
        public const byte UnpackInfo = 0x07;
        public const byte SubStreamsInfo = 0x08;
        public const byte Size = 0x09;
        public const byte Crc = 0x0A;
        public const byte Folder = 0x0B;
        public const byte CodersUnpackSize = 0x0C;
        public const byte NumUnpackStream = 0x0D;
        public const byte EmptyStream = 0x0E;
        public const byte EmptyFile = 0x0F;
        public const byte Name = 0x11;
        public const byte EncodedHeader = 0x17;
    }
}
