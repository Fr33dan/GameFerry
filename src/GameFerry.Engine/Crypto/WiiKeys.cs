using System.Security.Cryptography;

namespace GameFerry.Engine.Crypto;

/// <summary>Where the Wii's common keys come from. They decrypt each partition's title key.</summary>
public interface IWiiKeySource
{
    /// <summary>The common key with the given index (0 standard, 1 Korean), or null if this source lacks it.</summary>
    byte[]? GetCommonKey(int index);
}

/// <summary>
/// The key source every partition read goes through. Builds made with <c>-p:GameFerryBuiltInKeys=false</c>
/// carry no keys; the application then sets <see cref="Current"/> from the user's own (see <see cref="KeysBinSource"/>).
/// </summary>
public static class WiiKeys
{
    public const int StandardIndex = 0;
    public const int KoreanIndex = 1;

    // SHA-1 of each common key, to recognise a genuine one without needing the key itself.
    private static readonly string[] Fingerprints =
    [
        "EBEAE6D2762D4D3EA160A6D8327FAC9A25F8062B",
        "5BACDB63DD28A1CC1B509A93712032BEFB9EADB5",
    ];

    /// <summary>True when this build carries the common keys, so nothing needs to be asked of the user.</summary>
    public static bool HasBuiltInKeys =>
#if NO_BUILTIN_KEYS
        false;
#else
        true;
#endif

    public static IWiiKeySource Current { get; set; } =
#if NO_BUILTIN_KEYS
        new CombinedWiiKeys();
#else
        BuiltInWiiKeys.Instance;
#endif

    /// <summary>The common key with <paramref name="index"/>, or an exception saying which one is missing.</summary>
    public static byte[] GetCommonKey(int index) =>
        Current.GetCommonKey(index) ?? throw new MissingWiiKeyException(index);

    /// <summary>True when <paramref name="key"/> is the Wii's common key with that index.</summary>
    public static bool IsGenuine(int index, ReadOnlySpan<byte> key)
    {
        if (index is < 0 or > KoreanIndex || key.Length != AesCbc.KeySize)
            return false;
        return Convert.ToHexString(SHA1.HashData(key)) == Fingerprints[index];
    }

    internal static string KeyName(int index) => index == KoreanIndex ? "Korean common key" : "Wii common key";
}

/// <summary>A disc needs a common key that hasn't been provided.</summary>
public sealed class MissingWiiKeyException(int index) : Exception(
    $"This disc needs the {WiiKeys.KeyName(index)}, which hasn't been provided."
    + (index == WiiKeys.KoreanIndex ? " Enter it in hex." : " Add the keys.bin from a BootMii NAND backup of your console."))
{
    public int Index { get; } = index;
}

#if !NO_BUILTIN_KEYS
internal sealed class BuiltInWiiKeys : IWiiKeySource
{
    public static readonly BuiltInWiiKeys Instance = new();

    private static ReadOnlySpan<byte> Standard =>
        [0xEB, 0xE4, 0x2A, 0x22, 0x5E, 0x85, 0x93, 0xE4, 0x48, 0xD9, 0xC5, 0x45, 0x73, 0x81, 0xAA, 0xF7];

    private static ReadOnlySpan<byte> Korean =>
        [0x63, 0xB8, 0x2B, 0xB4, 0xF4, 0x61, 0x4E, 0x2E, 0x13, 0xF2, 0xFE, 0xFB, 0xBA, 0x4C, 0x9B, 0x7E];

    public byte[]? GetCommonKey(int index) => index switch
    {
        WiiKeys.StandardIndex => Standard.ToArray(),
        WiiKeys.KoreanIndex => Korean.ToArray(),
        _ => null,
    };
}
#endif

/// <summary>
/// The common key from a BootMii NAND backup: <c>keys.bin</c>, or the same 1 KiB at the end of <c>nand.bin</c>.
/// Its first 0x100 bytes are a text header, then the console's OTP, which holds the common key at 0x14.
/// Korean consoles keep their extra key elsewhere, so this never has one.
/// </summary>
public sealed class KeysBinSource : IWiiKeySource
{
    public const int Size = 0x400;
    private const int CommonKeyOffset = 0x114;

    private readonly byte[] _commonKey;

    private KeysBinSource(byte[] commonKey) => _commonKey = commonKey;

    /// <exception cref="InvalidDataException">The data isn't a keys.bin, or not one from a Wii.</exception>
    public static KeysBinSource Parse(ReadOnlySpan<byte> keysBin)
    {
        if (keysBin.Length != Size)
            throw new InvalidDataException($"A keys.bin is {Size} bytes; this file is {keysBin.Length:N0}.");

        byte[] key = keysBin.Slice(CommonKeyOffset, AesCbc.KeySize).ToArray();
        if (!WiiKeys.IsGenuine(WiiKeys.StandardIndex, key))
            throw new InvalidDataException("This keys.bin does not hold the Wii common key.");
        return new KeysBinSource(key);
    }

    public byte[]? GetCommonKey(int index) => index == WiiKeys.StandardIndex ? (byte[])_commonKey.Clone() : null;
}

/// <summary>A common key typed in as hex.</summary>
public sealed class HexKeySource : IWiiKeySource
{
    private readonly int _index;
    private readonly byte[] _key;

    /// <exception cref="InvalidDataException">The text isn't 32 hex digits, or isn't that common key.</exception>
    public HexKeySource(int index, string hex)
    {
        byte[] key;
        try
        {
            key = Convert.FromHexString(string.Concat(hex.Where(c => !char.IsWhiteSpace(c))));
        }
        catch (FormatException)
        {
            throw new InvalidDataException("A key is 32 hexadecimal digits.");
        }

        if (!WiiKeys.IsGenuine(index, key))
            throw new InvalidDataException($"That is not the {WiiKeys.KeyName(index)}.");
        _index = index;
        _key = key;
    }

    public byte[]? GetCommonKey(int index) => index == _index ? (byte[])_key.Clone() : null;
}

/// <summary>Asks each source in turn, so a keys.bin and a typed-in Korean key can work together.</summary>
public sealed class CombinedWiiKeys(params IWiiKeySource[] sources) : IWiiKeySource
{
    public IReadOnlyList<IWiiKeySource> Sources { get; } = sources;

    public byte[]? GetCommonKey(int index) =>
        Sources.Select(source => source.GetCommonKey(index)).FirstOrDefault(key => key is not null);
}
