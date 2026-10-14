using System.Buffers.Binary;
using System.Text;

namespace GameFerry.Engine.Disc;

public enum DiscKind
{
    Unknown,
    Wii,
    GameCube,
}

/// <summary>The identifying fields at the start of a Wii or GameCube disc image.</summary>
public sealed record DiscHeader
{
    /// <summary>Bytes needed from the start of an image to parse everything here, including the NKit marker.</summary>
    public const int PeekSize = 0x220;

    private const int NkitMarkerOffset = 0x200;
    private const int NkitImageSizeOffset = 0x210;
    private const int NkitUpdateCrcOffset = 0x218;

    public required string Id6 { get; init; }
    public required byte DiscNumber { get; init; }
    public required byte Version { get; init; }
    public required string Title { get; init; }
    public required DiscKind Kind { get; init; }

    /// <summary>True when the image is in NKit format (shrunk; partitions stored decrypted without hashes).</summary>
    public bool IsNkit { get; init; }

    /// <summary>Size of the full disc an NKit image restores to.</summary>
    public long? NkitImageSize { get; init; }

    /// <summary>True when the NKit image was made with its update partition stripped.</summary>
    public bool NkitUpdateRemoved { get; init; }

    public static DiscHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x60)
            throw new InvalidDataException("Disc header is too short.");

        DiscKind kind = DiscKind.Unknown;
        if (BinaryPrimitives.ReadUInt32BigEndian(data[0x18..]) == WiiConstants.WiiMagic)
            kind = DiscKind.Wii;
        else if (BinaryPrimitives.ReadUInt32BigEndian(data[0x1C..]) == WiiConstants.GameCubeMagic)
            kind = DiscKind.GameCube;

        bool isNkit = data.Length >= PeekSize && data.Slice(NkitMarkerOffset, 4).SequenceEqual("NKIT"u8);
        long? nkitImageSize = null;
        bool updateRemoved = false;
        if (isNkit)
        {
            // Wii sizes are stored divided by four, like every other Wii disc offset.
            long stored = BinaryPrimitives.ReadUInt32BigEndian(data[NkitImageSizeOffset..]);
            nkitImageSize = kind == DiscKind.Wii ? stored << 2 : stored;
            updateRemoved = BinaryPrimitives.ReadUInt32BigEndian(data[NkitUpdateCrcOffset..]) != 0;
        }

        return new DiscHeader
        {
            Id6 = DecodeId(data[..6]),
            DiscNumber = data[6],
            Version = data[7],
            Title = DecodeTitle(data.Slice(0x20, 0x40)),
            Kind = kind,
            IsNkit = isNkit,
            NkitImageSize = nkitImageSize,
            NkitUpdateRemoved = updateRemoved,
        };
    }

    public static DiscHeader Read(IDiscReader disc)
    {
        Span<byte> data = stackalloc byte[PeekSize];
        disc.ReadAt(0, data);
        return Parse(data);
    }

    /// <summary>
    /// Parses the start of a loose image; returns null if it isn't a recognisable disc image.
    /// <paramref name="data"/> should hold <see cref="PeekSize"/> bytes, or the whole file if it is shorter.
    /// </summary>
    public static DiscHeader? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x60)
            return null;

        DiscHeader header = Parse(data);
        return header.Kind != DiscKind.Unknown && IsValidId(header.Id6) ? header : null;
    }

    public static bool IsValidId(string id) =>
        id.Length == 6 && id.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    private static string DecodeId(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = stackalloc char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
            chars[i] = bytes[i] is >= 0x20 and < 0x7F ? (char)bytes[i] : '?';
        return new string(chars);
    }

    private static string DecodeTitle(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end >= 0)
            bytes = bytes[..end];

        // Titles are ASCII except on some Japanese discs, which use Shift-JIS.
        Encoding encoding = Encoding.ASCII;
        if (bytes.ContainsAnyExceptInRange((byte)0x20, (byte)0x7E))
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = Encoding.GetEncoding(932);
        }

        string title = encoding.GetString(bytes);
        return new string(title.Where(c => !char.IsControl(c)).ToArray()).Trim();
    }
}
