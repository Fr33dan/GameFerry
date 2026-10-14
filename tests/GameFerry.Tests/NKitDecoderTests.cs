using System.IO.Hashing;
using GameFerry.Engine.Disc;
using GameFerry.Engine.NKit;
using GameFerry.Engine.Wbfs;
using GameFerry.Tests.Support;

namespace GameFerry.Tests;

/// <summary>
/// TestData\synthetic.nkit.iso is <see cref="SyntheticDisc.CreateStandard"/> converted by NKit 1.4 itself (keeping the
/// update partition), so these check the decoder against NKit. The synthetic partitions carry no game ID, which NKit
/// stores as raw data, and their unwritten sectors read as zeros, which it treats as scrubbed. NKit also drops the
/// filler hash blocks of such partitions, so the disc it restores is not quite the original: decoded by NKit 1.4, it
/// has CRC-32 FDCD32AB (the original's is 379D3073). Matching that is matching NKit.
/// </summary>
public class NKitDecoderTests
{
    private const uint NKitRestoredCrc = 0xFDCD32AB;

    private static FileStream OpenImage() => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "synthetic.nkit.iso"));

    /// <summary>Keeps a CRC-32 of each write, then joins them in disc order; checks each byte arrives exactly once.</summary>
    private sealed class CrcSink : IRestoredDiscSink
    {
        private readonly List<(long Start, long Length, uint Crc)> _writes = [];

        public bool KeepsOnlyUsedBlocks => false;

        public ValueTask WriteAsync(long discOffset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            _writes.Add((discOffset, data.Length, Crc32.HashToUInt32(data.Span)));
            return ValueTask.CompletedTask;
        }

        public void MarkUsed(long discOffset, long length)
        {
        }

        public void UsageFinalBefore(long discOffset)
        {
        }

        public bool IsNeeded(long discOffset, long length) => true;

        /// <summary>The CRC-32 of the whole disc, or null if the writes don't tile [0, length) exactly.</summary>
        public uint? DiscCrc(long length)
        {
            long next = 0;
            uint crc = 0;
            foreach ((long start, long count, uint part) in _writes.OrderBy(w => w.Start))
            {
                if (start != next)
                    return null;
                crc = next == 0 ? part : Combine(crc, part, count);
                next += count;
            }

            return next == length ? crc : null;
        }

        /// <summary>zlib's crc32_combine: the CRC of A followed by B, from the CRCs of A and B and B's length.</summary>
        private static uint Combine(uint first, uint second, long secondLength)
        {
            uint[] even = new uint[32];
            uint[] odd = new uint[32];
            odd[0] = 0xEDB88320;
            uint row = 1;
            for (int n = 1; n < 32; n++)
            {
                odd[n] = row;
                row <<= 1;
            }

            Square(even, odd);
            Square(odd, even);
            do
            {
                Square(even, odd);
                if ((secondLength & 1) != 0)
                    first = Times(even, first);
                secondLength >>= 1;
                if (secondLength == 0)
                    break;
                Square(odd, even);
                if ((secondLength & 1) != 0)
                    first = Times(odd, first);
                secondLength >>= 1;
            }
            while (secondLength != 0);

            return first ^ second;
        }

        private static uint Times(uint[] matrix, uint vector)
        {
            uint sum = 0;
            for (int i = 0; vector != 0; i++, vector >>= 1)
            {
                if ((vector & 1) != 0)
                    sum ^= matrix[i];
            }

            return sum;
        }

        private static void Square(uint[] square, uint[] matrix)
        {
            for (int n = 0; n < 32; n++)
                square[n] = Times(matrix, matrix[n]);
        }
    }

    [Fact]
    public async Task Restores_the_disc_exactly_as_NKit_does()
    {
        using FileStream image = OpenImage();
        CrcSink sink = new();

        NKitDecodeResult result = await NKitWiiDecoder.DecodeAsync(image, image.Length, sink, PartitionSelection.All);

        Assert.Equal(WiiConstants.SingleLayerSize, result.DiscSize);
        Assert.Equal(NKitRestoredCrc, sink.DiscCrc(result.DiscSize));
        Assert.Equal(3, result.Partitions.Count);
        Assert.False(result.UpdatePartitionRemoved);
    }

    [Theory]
    [InlineData(PartitionSelection.All)]
    [InlineData(PartitionSelection.RemoveUpdate)]
    [InlineData(PartitionSelection.GameOnly)]
    public async Task Streams_a_WBFS_laid_out_as_the_writer_lays_out_the_disc(PartitionSelection selection)
    {
        // Only hash blocks differ between the original and what NKit restores, so the blocks kept, and with them the
        // WBFS header, are the same as the writer's for the original disc.
        SyntheticDisc disc = SyntheticDisc.CreateStandard();
        WbfsPlan plan = WbfsWriter.Plan(disc.Disc, selection);
        List<WbfsChunk> expected = [.. WbfsWriter.Produce(disc.Disc, plan).Select(c => new WbfsChunk(c.Position, c.Data.ToArray()))];

        Dictionary<long, byte[]> actual = [];
        StreamingWbfsBuilder builder = new((chunk, _) =>
        {
            actual.Add(chunk.Position, chunk.Data.ToArray());
            return ValueTask.CompletedTask;
        });
        using FileStream image = OpenImage();
        NKitDecodeResult result = await NKitWiiDecoder.DecodeAsync(image, image.Length, builder, selection);
        await builder.CompleteAsync(result, CancellationToken.None);

        Assert.Equal(plan.OutputSize, builder.OutputSize);
        Assert.Equal(expected.Select(c => c.Position).Order(), actual.Keys.Order());
        Assert.True(expected[0].Data.Span.SequenceEqual(actual[0]), "WBFS header differs");
    }
}
