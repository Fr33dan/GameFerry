using System.Runtime.InteropServices;
using GameFerry.Engine.Archives;

namespace GameFerry.Web.Engine;

/// <summary>
/// Unpacks one LZMA or LZMA2 stream with the LZMA SDK's C decoder (Native\), which in WebAssembly runs about
/// twice as fast as a managed decoder. Reads forwards only.
/// </summary>
internal sealed unsafe class LzmaStream : Stream
{
    private const int FirstRead = 4096;

    private readonly Stream _packed;
    private readonly byte[] _input = new byte[1 << 20];
    private readonly long _length;
    private nint _decoder;
    private int _inputPosition;
    private int _inputLength;
    private int _readSize = FirstRead;
    private long _position;

    /// <param name="packed">The packed bytes, positioned at their start.</param>
    /// <param name="length">How many bytes the stream unpacks to.</param>
    public LzmaStream(Stream packed, SevenZipCoder coder, long length)
    {
        bool lzma2 = coder.Is(SevenZipCoder.Lzma2);
        if (!lzma2 && !coder.Is(SevenZipCoder.Lzma))
            throw new NotSupportedException("Only LZMA and LZMA2 are unpacked natively.");

        _packed = packed;
        _length = length;
        fixed (byte* properties = coder.Properties)
            _decoder = Native.Create(lzma2 ? 1 : 0, properties, coder.Properties.Length);
        if (_decoder == 0)
            throw new InvalidDataException("The archive's LZMA settings were refused (dictionary too large?).");
    }

    public static bool CanUnpack(SevenZipFolder folder) =>
        folder.IsSimple && (folder.Coders[0].Is(SevenZipCoder.Lzma) || folder.Coders[0].Is(SevenZipCoder.Lzma2));

    /// <summary>Unpacks a whole (small) folder, such as a compressed 7z table of contents.</summary>
    public static byte[] UnpackAll(SevenZipFolder folder, Stream archive)
    {
        if (!CanUnpack(folder) || folder.UnpackSize > 64 << 20)
            throw new NotSupportedException("This 7z archive's table of contents is packed in a way the browser version cannot unpack.");

        using LzmaStream stream = new(new SubStream(archive, folder.PackOffset, folder.PackSize), folder.Coders[0], folder.UnpackSize);
        byte[] bytes = new byte[folder.UnpackSize];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_decoder == 0)
            throw new ObjectDisposedException(nameof(LzmaStream));

        int wanted = (int)Math.Min(buffer.Length, _length - _position);
        int total = 0;
        while (total < wanted)
        {
            if (_inputPosition == _inputLength)
            {
                // Small reads first: identifying a game unpacks only its first few hundred bytes, and every byte may
                // come over a slow network share. A whole copy soon reaches the full buffer.
                _inputLength = _packed.Read(_input, 0, _readSize);
                _inputPosition = 0;
                _readSize = Math.Min(_readSize * 2, _input.Length);
            }

            nuint destLength = (nuint)(wanted - total);
            nuint srcLength = (nuint)(_inputLength - _inputPosition);
            int status;
            int result;
            fixed (byte* destination = buffer[total..])
            fixed (byte* source = _input)
                result = Native.Decode(_decoder, destination, &destLength, source + _inputPosition, &srcLength, &status);
            if (result != 0)
                throw new InvalidDataException($"The archive is damaged (LZMA error {result}).");

            _inputPosition += (int)srcLength;
            total += (int)destLength;
            if (destLength == 0 && srcLength == 0)
                throw new InvalidDataException("The archive ends before the file it holds.");
        }

        _position += total;
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_decoder != 0)
        {
            Native.Free(_decoder);
            _decoder = 0;
        }

        base.Dispose(disposing);
    }

    private static class Native
    {
        [DllImport("gflzma", EntryPoint = "gf_decoder_create")]
        public static extern nint Create(int lzma2, byte* properties, int size);

        [DllImport("gflzma", EntryPoint = "gf_decoder_decode")]
        public static extern int Decode(nint decoder, byte* destination, nuint* destinationLength, byte* source, nuint* sourceLength, int* status);

        [DllImport("gflzma", EntryPoint = "gf_decoder_free")]
        public static extern void Free(nint decoder);
    }
}
