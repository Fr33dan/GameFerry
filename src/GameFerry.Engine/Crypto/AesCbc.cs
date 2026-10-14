using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace GameFerry.Engine.Crypto;

/// <summary>
/// AES-128 in CBC mode without padding, the only cipher Wii discs use. On the desktop this is the platform's
/// hardware-backed AES; the browser runtime has no AES at all, so there it is <see cref="ManagedAesCbc"/>.
/// </summary>
public abstract class AesCbc : IDisposable
{
    public const int KeySize = 16;
    public const int BlockSize = 16;

    public static AesCbc Create(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"AES-128 keys are {KeySize} bytes.", nameof(key));
        if (OperatingSystem.IsBrowser())
            return new ManagedAesCbc(key);
        return new PlatformAesCbc(key);
    }

    /// <summary>Encrypts whole blocks; <paramref name="output"/> may be <paramref name="input"/> itself.</summary>
    public abstract void Encrypt(ReadOnlySpan<byte> input, ReadOnlySpan<byte> iv, Span<byte> output);

    /// <summary>Decrypts whole blocks; <paramref name="output"/> may be <paramref name="input"/> itself.</summary>
    public abstract void Decrypt(ReadOnlySpan<byte> input, ReadOnlySpan<byte> iv, Span<byte> output);

    public virtual void Dispose()
    {
    }

    protected static void CheckLengths(ReadOnlySpan<byte> input, ReadOnlySpan<byte> iv, Span<byte> output)
    {
        if (input.Length % BlockSize != 0)
            throw new ArgumentException("CBC without padding works on whole 16-byte blocks.", nameof(input));
        if (iv.Length != BlockSize)
            throw new ArgumentException("The IV is one 16-byte block.", nameof(iv));
        if (output.Length < input.Length)
            throw new ArgumentException("The output is shorter than the input.", nameof(output));
    }
}

[UnsupportedOSPlatform("browser")]
internal sealed class PlatformAesCbc : AesCbc
{
    private readonly Aes _aes = Aes.Create();

    public PlatformAesCbc(ReadOnlySpan<byte> key) => _aes.Key = key.ToArray();

    public override void Encrypt(ReadOnlySpan<byte> input, ReadOnlySpan<byte> iv, Span<byte> output)
    {
        CheckLengths(input, iv, output);
        _aes.EncryptCbc(input, iv, output, PaddingMode.None);
    }

    public override void Decrypt(ReadOnlySpan<byte> input, ReadOnlySpan<byte> iv, Span<byte> output)
    {
        CheckLengths(input, iv, output);
        _aes.DecryptCbc(input, iv, output, PaddingMode.None);
    }

    public override void Dispose() => _aes.Dispose();
}

/// <summary>
/// Table-driven AES-128 (FIPS-197), in the style of the reference <c>rijndael-alg-fst.c</c>: four 1 KiB tables per
/// direction, each round a handful of lookups and XORs per column. Decryption uses the equivalent inverse cipher,
/// whose round keys have InvMixColumns applied up front.
/// </summary>
internal sealed class ManagedAesCbc : AesCbc
{
    private const int Rounds = 10;
    private const int RoundKeyWords = 4 * (Rounds + 1);

    private static readonly byte[] SBox = new byte[256];
    private static readonly byte[] InverseSBox = new byte[256];
    private static readonly uint[] Te0 = new uint[256];
    private static readonly uint[] Te1 = new uint[256];
    private static readonly uint[] Te2 = new uint[256];
    private static readonly uint[] Te3 = new uint[256];
    private static readonly uint[] Td0 = new uint[256];
    private static readonly uint[] Td1 = new uint[256];
    private static readonly uint[] Td2 = new uint[256];
    private static readonly uint[] Td3 = new uint[256];

    private readonly uint[] _encryptKeys = new uint[RoundKeyWords];
    private readonly uint[] _decryptKeys = new uint[RoundKeyWords];

    static ManagedAesCbc()
    {
        // The S-box built from its definition rather than typed in: p walks GF(2^8) by multiplying by 3, q by
        // dividing by 3, so q is always p's inverse; the affine transform of the inverse is the box entry.
        byte p = 1;
        byte q = 1;
        do
        {
            p = (byte)(p ^ (p << 1) ^ ((p & 0x80) != 0 ? 0x1B : 0));
            q ^= (byte)(q << 1);
            q ^= (byte)(q << 2);
            q ^= (byte)(q << 4);
            if ((q & 0x80) != 0)
                q ^= 0x09;
            SBox[p] = (byte)(q ^ RotateLeft(q, 1) ^ RotateLeft(q, 2) ^ RotateLeft(q, 3) ^ RotateLeft(q, 4) ^ 0x63);
        }
        while (p != 1);
        SBox[0] = 0x63;

        for (int i = 0; i < 256; i++)
        {
            InverseSBox[SBox[i]] = (byte)i;

            uint s = SBox[i];
            uint encrypt = (Multiply(s, 2) << 24) | (s << 16) | (s << 8) | Multiply(s, 3);
            Te0[i] = encrypt;
            Te1[i] = RotateRight(encrypt, 8);
            Te2[i] = RotateRight(encrypt, 16);
            Te3[i] = RotateRight(encrypt, 24);
        }

        for (int i = 0; i < 256; i++)
        {
            uint s = InverseSBox[i];
            uint decrypt = (Multiply(s, 14) << 24) | (Multiply(s, 9) << 16) | (Multiply(s, 13) << 8) | Multiply(s, 11);
            Td0[i] = decrypt;
            Td1[i] = RotateRight(decrypt, 8);
            Td2[i] = RotateRight(decrypt, 16);
            Td3[i] = RotateRight(decrypt, 24);
        }
    }

    public ManagedAesCbc(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"AES-128 keys are {KeySize} bytes.", nameof(key));

        uint[] ek = _encryptKeys;
        for (int i = 0; i < 4; i++)
            ek[i] = BinaryPrimitives.ReadUInt32BigEndian(key[(i * 4)..]);

        uint rcon = 1;
        for (int i = 4; i < RoundKeyWords; i++)
        {
            uint word = ek[i - 1];
            if (i % 4 == 0)
            {
                word = SubWord(RotateLeft(word, 8)) ^ (rcon << 24);
                rcon = Multiply(rcon, 2);
            }

            ek[i] = ek[i - 4] ^ word;
        }

        // The inverse cipher takes the round keys in reverse order, all but the outer two through InvMixColumns.
        uint[] dk = _decryptKeys;
        for (int round = 0; round <= Rounds; round++)
        {
            for (int column = 0; column < 4; column++)
            {
                uint word = ek[(Rounds - round) * 4 + column];
                dk[round * 4 + column] = round is 0 or Rounds ? word : InverseMixColumn(word);
            }
        }
    }

    public override void Encrypt(ReadOnlySpan<byte> input, ReadOnlySpan<byte> iv, Span<byte> output)
    {
        CheckLengths(input, iv, output);
        uint[] rk = _encryptKeys;
        uint c0 = BinaryPrimitives.ReadUInt32BigEndian(iv);
        uint c1 = BinaryPrimitives.ReadUInt32BigEndian(iv[4..]);
        uint c2 = BinaryPrimitives.ReadUInt32BigEndian(iv[8..]);
        uint c3 = BinaryPrimitives.ReadUInt32BigEndian(iv[12..]);

        for (int offset = 0; offset < input.Length; offset += BlockSize)
        {
            uint s0 = BinaryPrimitives.ReadUInt32BigEndian(input[offset..]) ^ c0 ^ rk[0];
            uint s1 = BinaryPrimitives.ReadUInt32BigEndian(input[(offset + 4)..]) ^ c1 ^ rk[1];
            uint s2 = BinaryPrimitives.ReadUInt32BigEndian(input[(offset + 8)..]) ^ c2 ^ rk[2];
            uint s3 = BinaryPrimitives.ReadUInt32BigEndian(input[(offset + 12)..]) ^ c3 ^ rk[3];

            for (int k = 4; k < Rounds * 4; k += 4)
            {
                uint t0 = Te0[s0 >> 24] ^ Te1[(s1 >> 16) & 0xFF] ^ Te2[(s2 >> 8) & 0xFF] ^ Te3[s3 & 0xFF] ^ rk[k];
                uint t1 = Te0[s1 >> 24] ^ Te1[(s2 >> 16) & 0xFF] ^ Te2[(s3 >> 8) & 0xFF] ^ Te3[s0 & 0xFF] ^ rk[k + 1];
                uint t2 = Te0[s2 >> 24] ^ Te1[(s3 >> 16) & 0xFF] ^ Te2[(s0 >> 8) & 0xFF] ^ Te3[s1 & 0xFF] ^ rk[k + 2];
                uint t3 = Te0[s3 >> 24] ^ Te1[(s0 >> 16) & 0xFF] ^ Te2[(s1 >> 8) & 0xFF] ^ Te3[s2 & 0xFF] ^ rk[k + 3];
                (s0, s1, s2, s3) = (t0, t1, t2, t3);
            }

            c0 = LastRound(SBox, s0, s1, s2, s3) ^ rk[40];
            c1 = LastRound(SBox, s1, s2, s3, s0) ^ rk[41];
            c2 = LastRound(SBox, s2, s3, s0, s1) ^ rk[42];
            c3 = LastRound(SBox, s3, s0, s1, s2) ^ rk[43];

            BinaryPrimitives.WriteUInt32BigEndian(output[offset..], c0);
            BinaryPrimitives.WriteUInt32BigEndian(output[(offset + 4)..], c1);
            BinaryPrimitives.WriteUInt32BigEndian(output[(offset + 8)..], c2);
            BinaryPrimitives.WriteUInt32BigEndian(output[(offset + 12)..], c3);
        }
    }

    public override void Decrypt(ReadOnlySpan<byte> input, ReadOnlySpan<byte> iv, Span<byte> output)
    {
        CheckLengths(input, iv, output);
        uint[] rk = _decryptKeys;
        uint p0 = BinaryPrimitives.ReadUInt32BigEndian(iv);
        uint p1 = BinaryPrimitives.ReadUInt32BigEndian(iv[4..]);
        uint p2 = BinaryPrimitives.ReadUInt32BigEndian(iv[8..]);
        uint p3 = BinaryPrimitives.ReadUInt32BigEndian(iv[12..]);

        for (int offset = 0; offset < input.Length; offset += BlockSize)
        {
            // Read the whole ciphertext block first: it is the next block's chaining value, and the output may overlap it.
            uint c0 = BinaryPrimitives.ReadUInt32BigEndian(input[offset..]);
            uint c1 = BinaryPrimitives.ReadUInt32BigEndian(input[(offset + 4)..]);
            uint c2 = BinaryPrimitives.ReadUInt32BigEndian(input[(offset + 8)..]);
            uint c3 = BinaryPrimitives.ReadUInt32BigEndian(input[(offset + 12)..]);

            uint s0 = c0 ^ rk[0];
            uint s1 = c1 ^ rk[1];
            uint s2 = c2 ^ rk[2];
            uint s3 = c3 ^ rk[3];

            for (int k = 4; k < Rounds * 4; k += 4)
            {
                uint t0 = Td0[s0 >> 24] ^ Td1[(s3 >> 16) & 0xFF] ^ Td2[(s2 >> 8) & 0xFF] ^ Td3[s1 & 0xFF] ^ rk[k];
                uint t1 = Td0[s1 >> 24] ^ Td1[(s0 >> 16) & 0xFF] ^ Td2[(s3 >> 8) & 0xFF] ^ Td3[s2 & 0xFF] ^ rk[k + 1];
                uint t2 = Td0[s2 >> 24] ^ Td1[(s1 >> 16) & 0xFF] ^ Td2[(s0 >> 8) & 0xFF] ^ Td3[s3 & 0xFF] ^ rk[k + 2];
                uint t3 = Td0[s3 >> 24] ^ Td1[(s2 >> 16) & 0xFF] ^ Td2[(s1 >> 8) & 0xFF] ^ Td3[s0 & 0xFF] ^ rk[k + 3];
                (s0, s1, s2, s3) = (t0, t1, t2, t3);
            }

            BinaryPrimitives.WriteUInt32BigEndian(output[offset..], LastRound(InverseSBox, s0, s3, s2, s1) ^ rk[40] ^ p0);
            BinaryPrimitives.WriteUInt32BigEndian(output[(offset + 4)..], LastRound(InverseSBox, s1, s0, s3, s2) ^ rk[41] ^ p1);
            BinaryPrimitives.WriteUInt32BigEndian(output[(offset + 8)..], LastRound(InverseSBox, s2, s1, s0, s3) ^ rk[42] ^ p2);
            BinaryPrimitives.WriteUInt32BigEndian(output[(offset + 12)..], LastRound(InverseSBox, s3, s2, s1, s0) ^ rk[43] ^ p3);
            (p0, p1, p2, p3) = (c0, c1, c2, c3);
        }
    }

    /// <summary>The final round: no MixColumns, so each byte goes straight through the (inverse) S-box.</summary>
    private static uint LastRound(byte[] box, uint a, uint b, uint c, uint d) =>
        (uint)box[a >> 24] << 24 | (uint)box[(b >> 16) & 0xFF] << 16 | (uint)box[(c >> 8) & 0xFF] << 8 | box[d & 0xFF];

    /// <summary>InvMixColumns of one column: the Td tables apply it after the inverse S-box, which the S-box undoes.</summary>
    private static uint InverseMixColumn(uint word) =>
        Td0[SBox[word >> 24]] ^ Td1[SBox[(word >> 16) & 0xFF]] ^ Td2[SBox[(word >> 8) & 0xFF]] ^ Td3[SBox[word & 0xFF]];

    private static uint SubWord(uint word) =>
        (uint)SBox[word >> 24] << 24 | (uint)SBox[(word >> 16) & 0xFF] << 16 | (uint)SBox[(word >> 8) & 0xFF] << 8 | SBox[word & 0xFF];

    /// <summary>Multiplication in GF(2^8) modulo the AES polynomial.</summary>
    private static uint Multiply(uint a, uint b)
    {
        uint product = 0;
        while (b != 0)
        {
            if ((b & 1) != 0)
                product ^= a;
            a = (a << 1) ^ ((a & 0x80) != 0 ? 0x11Bu : 0);
            b >>= 1;
        }

        return product & 0xFF;
    }

    private static byte RotateLeft(byte value, int shift) => (byte)((value << shift) | (value >> (8 - shift)));

    private static uint RotateLeft(uint value, int shift) => (value << shift) | (value >> (32 - shift));

    private static uint RotateRight(uint value, int shift) => (value >> shift) | (value << (32 - shift));
}
