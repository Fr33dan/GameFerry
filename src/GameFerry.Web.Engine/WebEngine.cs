using System.IO.Hashing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameFerry.Engine.Archives;
using GameFerry.Engine.Crypto;
using GameFerry.Engine.Disc;
using GameFerry.Engine.Library;
using GameFerry.Engine.NKit;
using GameFerry.Engine.Wbfs;

namespace GameFerry.Web.Engine;

/// <summary>
/// What the worker script calls. Results are JSON strings, written by hand so nothing depends on reflection, which
/// ahead-of-time compilation trims away.
/// </summary>
public static partial class WebEngine
{
    private static readonly string[] ImageExtensions = [".nkit.iso", ".iso", ".wbfs", ".nkit.gcz", ".gcz"];

    private static CancellationTokenSource? _cancellation;

    /// <summary>The engine's version and whether this build carries the Wii common keys.</summary>
    [JSExport]
    internal static string About() => Json(writer =>
    {
        writer.WriteString("version", typeof(WebEngine).Assembly.GetName().Version?.ToString(3));
        // With the commit it was built from, e.g. 0.1.0+154eb7d…, so a log says exactly which code ran.
        writer.WriteString("build", typeof(WebEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        writer.WriteBoolean("builtInKeys", WiiKeys.HasBuiltInKeys);
        writer.WriteBoolean("hasKeys", WiiKeys.Current.GetCommonKey(WiiKeys.StandardIndex) is not null);
    });

    /// <summary>Uses the common key from a keys.bin (or the last 1 KiB of a nand.bin) the page passed in.</summary>
    [JSExport]
    internal static string UseKeysBin(int fileId, double size)
    {
        try
        {
            using JsFileStream file = new(fileId, (long)size);
            byte[] keys = new byte[KeysBinSource.Size];
            file.Position = (long)size - KeysBinSource.Size;
            file.ReadExactly(keys);
            WiiKeys.Current = new CombinedWiiKeys(KeysBinSource.Parse(keys), WiiKeys.Current);
            return Json(writer => writer.WriteBoolean("ok", true));
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return Json(writer => writer.WriteString("error", ex.Message));
        }
    }

    /// <summary>Identifies a file: whether it is (or holds) a Wii disc image this can copy, and what game.</summary>
    [JSExport]
    internal static string Probe(int fileId, double size, string name)
    {
        try
        {
            using Source source = Source.Open(fileId, (long)size, name);
            byte[] start = new byte[DiscHeader.PeekSize];
            source.Image.ReadExactly(start);
            DiscHeader? header = DiscHeader.TryParse(start);
            return Json(writer =>
            {
                writer.WriteString("file", name);
                writer.WriteString("entry", source.EntryName);
                writer.WriteNumber("imageBytes", source.Length);
                writer.WriteString("packing", source.Packing);
                if (header is null)
                {
                    writer.WriteString("problem", "Not a Wii or GameCube disc image.");
                    return;
                }

                writer.WriteString("id6", header.Id6);
                writer.WriteString("discTitle", header.Title);
                writer.WriteString("kind", header.Kind.ToString());
                writer.WriteBoolean("nkit", header.IsNkit);
                writer.WriteBoolean("updateRemoved", header.NkitUpdateRemoved);
                // The disc's own title, as Wii Backup Manager names folders; the file name if the disc has none.
                writer.WriteString("folder", NameBuilder.GameFolderName(header.Id6, header.Title, NameBuilder.TitleFromFileName(source.EntryName ?? name)));
                writer.WriteString("fileTitle", NameBuilder.TitleFromFileName(source.EntryName ?? name));
                writer.WriteNumber("estimatedBytes", header.IsNkit ? (long)(source.Length * 1.06) + (8 << 20) : source.Length);
                string? problem = header.Kind != DiscKind.Wii ? "GameCube discs are not copied to WBFS."
                    : !header.IsNkit && source.IsArchived ? "Plain ISO images can only be copied when they aren't in an archive, so far."
                    : null;
                if (problem is not null)
                    writer.WriteString("problem", problem);
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException)
        {
            return Json(writer =>
            {
                writer.WriteString("file", name);
                writer.WriteString("problem", ex.Message);
            });
        }
    }

    /// <summary>
    /// Copies a game: restores the disc from the NKit image and streams the WBFS file to the page through
    /// <see cref="Interop.WriteChunk"/>, split into parts of <c>splitSize</c> bytes.
    /// </summary>
    /// <param name="options">JSON: <c>partitions</c> ("RemoveUpdate" or "GameOnly"), <c>splitSize</c>, <c>chunkHashes</c>.</param>
    [JSExport]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static async Task<string> Transfer(int fileId, double size, string name, string options)
    {
        using JsonDocument settings = JsonDocument.Parse(options);
        PartitionSelection partitions = settings.RootElement.TryGetProperty("partitions", out JsonElement p) && p.GetString() == "GameOnly"
            ? PartitionSelection.GameOnly
            : PartitionSelection.RemoveUpdate;
        long splitSize = settings.RootElement.TryGetProperty("splitSize", out JsonElement s) ? s.GetInt64() : WbfsFormat.DefaultSplitSize;
        bool chunkHashes = settings.RootElement.TryGetProperty("chunkHashes", out JsonElement h) && h.GetBoolean();
        if (splitSize <= 0 || splitSize % WbfsFormat.HdSectorSize != 0)
            throw new ArgumentException("The split size must be a whole number of 512-byte sectors.");

        using CancellationTokenSource cancellation = new();
        _cancellation = cancellation;
        try
        {
            using Source source = Source.Open(fileId, (long)size, name);
            CrcStream input = new(source.Image);
            long written = 0;
            int parts = 0;
            List<(long Position, string Hash)> hashes = [];

            // Hands a piece of the WBFS file to the page, cut where the file is split into parts.
            async ValueTask Write(WbfsChunk chunk, CancellationToken token)
            {
                if (chunkHashes)
                    hashes.Add((chunk.Position, Convert.ToHexString(SHA256.HashData(chunk.Data.Span))));

                long position = chunk.Position;
                ReadOnlyMemory<byte> data = chunk.Data;
                while (!data.IsEmpty)
                {
                    int part = (int)(position / splitSize);
                    long within = position % splitSize;
                    int count = (int)Math.Min(data.Length, splitSize - within);
                    ArraySegment<byte> piece = MemoryMarshal.TryGetArray(data[..count], out ArraySegment<byte> array) ? array : data[..count].ToArray();
                    await Interop.WriteChunk(part, within, piece);
                    parts = Math.Max(parts, part + 1);
                    position += count;
                    data = data[count..];
                }

                written += chunk.Data.Length;
            }

            Reporter progress = new(() => written);
            long outputSize;
            IReadOnlyList<string> warnings;
            Action<Utf8JsonWriter>? checks = null;
            if (!source.IsArchived && !IsNkit(source))
            {
                (outputSize, warnings) = await CopyPlainImageAsync(source, partitions, Write, progress, cancellation.Token);
            }
            else
            {
                StreamingWbfsBuilder builder = new(Write);
                NKitDecodeResult result = await NKitWiiDecoder.DecodeAsync(input, source.Length, builder, partitions, progress, cancellationToken: cancellation.Token);
                await builder.CompleteAsync(result, cancellation.Token);
                outputSize = builder.OutputSize;
                warnings = result.Warnings;

                // The whole image, trailing bytes too, so its CRC can be checked: an NKit image's CRC is the disc's own.
                input.ReadToEnd();
                uint expected = source.EntryCrc ?? result.DiscCrc;
                checks = writer =>
                {
                    writer.WriteString("crc", input.Crc.ToString("X8"));
                    writer.WriteString("expectedCrc", expected.ToString("X8"));
                    writer.WriteBoolean("crcMatches", input.Crc == expected);
                    writer.WriteNumber("groupsChecked", result.GroupsChecked);
                    writer.WriteNumber("groupsFailing", result.GroupsFailingH3);
                    writer.WriteNumber("groupsSkipped", result.GroupsSkipped);
                };
            }

            Interop.Progress(source.Length, written);
            return Json(writer =>
            {
                writer.WriteBoolean("ok", true);
                writer.WriteNumber("outputBytes", outputSize);
                writer.WriteNumber("parts", parts);
                checks?.Invoke(writer);
                writer.WriteStartArray("warnings");
                foreach (string warning in warnings)
                    writer.WriteStringValue(warning);
                writer.WriteEndArray();
                if (chunkHashes)
                {
                    writer.WriteStartObject("chunkHashes");
                    foreach ((long position, string hash) in hashes.OrderBy(c => c.Position))
                        writer.WriteString(position.ToString(System.Globalization.CultureInfo.InvariantCulture), hash);
                    writer.WriteEndObject();
                }
            });
        }
        catch (OperationCanceledException)
        {
            return Json(writer => writer.WriteBoolean("cancelled", true));
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException or MissingWiiKeyException)
        {
            return Json(writer => writer.WriteString("error", ex.Message));
        }
        finally
        {
            _cancellation = null;
        }
    }

    [JSExport]
    internal static void Cancel() => _cancellation?.Cancel();

    private static bool IsNkit(Source source)
    {
        byte[] start = new byte[DiscHeader.PeekSize];
        source.File.Position = 0;
        source.File.ReadExactly(start);
        source.File.Position = 0;
        return DiscHeader.TryParse(start)?.IsNkit == true;
    }

    /// <summary>
    /// A loose, plain ISO can be read anywhere, so it goes through the same planner and writer as on the desktop:
    /// the used blocks are worked out from the disc first, then written in order.
    /// </summary>
    private static async Task<(long OutputSize, IReadOnlyList<string> Warnings)> CopyPlainImageAsync(
        Source source, PartitionSelection partitions, Func<WbfsChunk, CancellationToken, ValueTask> output, IProgress<long> progress,
        CancellationToken cancellationToken)
    {
        using JsDiscReader disc = new(source.File);
        WbfsPlan plan = WbfsWriter.Plan(disc, partitions);
        long read = 0;
        foreach (WbfsChunk chunk in WbfsWriter.Produce(disc, plan, cancellationToken))
        {
            await output(chunk, cancellationToken);
            read += chunk.Data.Length;
            progress.Report(Math.Min(source.Length, read * source.Length / plan.OutputSize));
        }

        return (plan.OutputSize, plan.Usage.Warnings);
    }

    /// <summary>A plain disc image in a File, read anywhere (unread space reads as zeros).</summary>
    private sealed class JsDiscReader(JsFileStream file) : IDiscReader
    {
        public long Length => file.Length;

        public void ReadAt(long offset, Span<byte> buffer)
        {
            file.Position = offset;
            int read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            buffer[read..].Clear();
        }

        public void Dispose()
        {
        }
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>The disc image a chosen file stands for: the file itself, or the image inside a 7z archive.</summary>
    private sealed class Source : IDisposable
    {
        private readonly JsFileStream _file;

        private Source(JsFileStream file, Stream image, long length, string? entryName, uint? entryCrc, string packing)
        {
            _file = file;
            Image = image;
            Length = length;
            EntryName = entryName;
            EntryCrc = entryCrc;
            Packing = packing;
        }

        public Stream Image { get; }

        /// <summary>The chosen file itself, readable anywhere.</summary>
        public JsFileStream File => _file;

        public bool IsArchived => EntryName is not null;

        public long Length { get; }

        public string? EntryName { get; }

        public uint? EntryCrc { get; }

        public string Packing { get; }

        public static Source Open(int fileId, long size, string name)
        {
            JsFileStream file = new(fileId, size);
            byte[] start = new byte[32];
            int read = file.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
            file.Position = 0;
            if (!SevenZipArchive.HasSignature(start.AsSpan(0, read)))
                return new Source(file, file, size, null, null, "none");

            SevenZipArchive archive = SevenZipArchive.Read(file, LzmaStream.UnpackAll);
            SevenZipEntry entry = archive.Entries
                .Where(e => !e.IsDirectory && ImageExtensions.Any(x => e.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(e => e.Size)
                .FirstOrDefault() ?? throw new InvalidDataException("The archive holds no disc image.");
            SevenZipFolder folder = archive.Folders[entry.Folder];
            if (!LzmaStream.CanUnpack(folder))
                throw new NotSupportedException("The archive is packed in a way the browser version cannot unpack yet (only LZMA and LZMA2).");

            LzmaStream unpacked = new(new SubStream(file, folder.PackOffset, folder.PackSize), folder.Coders[0], folder.UnpackSize);
            Skip(unpacked, entry.OffsetInFolder);
            string packing = folder.Coders[0].Is(SevenZipCoder.Lzma2) ? "7z (LZMA2)" : "7z (LZMA)";
            return new Source(file, new LimitedStream(unpacked, entry.Size), entry.Size, Path.GetFileName(entry.Name), entry.Crc, packing);
        }

        public void Dispose()
        {
            Image.Dispose();
            _file.Dispose();
        }

        private static void Skip(Stream stream, long count)
        {
            byte[] buffer = new byte[1 << 20];
            while (count > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read == 0)
                    throw new EndOfStreamException("The archive ends before the file it holds.");
                count -= read;
            }
        }
    }

    /// <summary>Passes reads through, keeping a CRC-32 of every byte.</summary>
    private sealed class CrcStream(Stream inner) : Stream
    {
        private readonly Crc32 _crc = new();

        public uint Crc => _crc.GetCurrentHashAsUInt32();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void ReadToEnd()
        {
            byte[] buffer = new byte[1 << 20];
            while (Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer);
            _crc.Append(buffer[..read]);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Reports progress to the page, at most every 4 MiB of source read.</summary>
    private sealed class Reporter(Func<long> written) : IProgress<long>
    {
        private long _last = -1;

        public void Report(long value)
        {
            if (value - _last < 4 << 20)
                return;
            _last = value;
            Interop.Progress(value, written());
        }
    }
}
