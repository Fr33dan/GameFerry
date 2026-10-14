using GameFerry.Engine.Disc;
using GameFerry.Engine.NKit;
using GameFerry.Engine.Wbfs;
using GameFerry.Tests.Support;

namespace GameFerry.Tests;

public class StreamingWbfsBuilderTests
{
    private const int Block = WbfsFormat.BlockSize;

    private static byte[] WriterOutput(IDiscReader disc, WbfsPlan plan)
    {
        using MemoryStream file = new();
        foreach (WbfsChunk chunk in WbfsWriter.Produce(disc, plan))
            file.Write(chunk.Data.Span);
        return file.ToArray();
    }

    /// <summary>
    /// Feeds a disc to the builder the way the NKit decoder does: usage marked as the stream passes it, each block
    /// written only if still wanted, the first block last, and one block in two halves out of order.
    /// </summary>
    private static async Task<byte[]> BuilderOutput(IDiscReader disc, WbfsPlan plan, bool markEverythingFirst)
    {
        Dictionary<long, byte[]> chunks = [];
        StreamingWbfsBuilder builder = new((chunk, _) =>
        {
            chunks.Add(chunk.Position, chunk.Data.ToArray());
            return ValueTask.CompletedTask;
        });

        bool[] used = plan.Usage.SectorUsed.ToArray();
        void Mark(long firstSector, long count)
        {
            for (long s = firstSector; s < firstSector + count && s < used.Length; s++)
            {
                if (used[s])
                    builder.MarkUsed(s * WiiConstants.SectorSize, WiiConstants.SectorSize);
            }
        }

        int blocks = (int)((disc.Length + Block - 1) / Block);
        Mark(0, markEverythingFirst ? used.Length : WbfsFormat.SectorsPerBlock);

        byte[] buffer = new byte[Block];
        int splitBlock = Enumerable.Range(2, blocks - 2).First(b => plan.BlockMap[b] != 0);
        for (int b = 1; b < blocks; b++)
        {
            if (!markEverythingFirst)
                Mark((long)b * WbfsFormat.SectorsPerBlock, WbfsFormat.SectorsPerBlock);
            builder.UsageFinalBefore((long)(b + 1) * Block);

            long offset = (long)b * Block;
            if (!builder.IsNeeded(offset, Block))
                continue;

            int length = (int)Math.Min(Block, disc.Length - offset);
            disc.ReadAt(offset, buffer.AsSpan(0, length));
            if (b == splitBlock)
            {
                // The second half first: the block must still come out whole.
                await builder.WriteAsync(offset + Block / 2, buffer.AsMemory(Block / 2, Block / 2), CancellationToken.None);
                await builder.WriteAsync(offset, buffer.AsMemory(0, Block / 2), CancellationToken.None);
            }
            else
            {
                await builder.WriteAsync(offset, buffer.AsMemory(0, length), CancellationToken.None);
            }
        }

        disc.ReadAt(0, buffer);
        await builder.WriteAsync(0, buffer, CancellationToken.None);

        NKitDecodeResult result = new(disc.Length, 0, plan.Usage.Partitions, plan.Usage.KeptPartitions, false, 0, 0, 0, []);
        await builder.CompleteAsync(result, CancellationToken.None);

        Assert.Equal(plan.OutputSize, builder.OutputSize);
        byte[] file = new byte[builder.OutputSize];
        foreach ((long position, byte[] data) in chunks)
            data.CopyTo(file, position);
        Assert.Equal(plan.StoredBlocks + 1, chunks.Count);
        return file;
    }

    [Theory]
    [InlineData(PartitionSelection.All, false)]
    [InlineData(PartitionSelection.RemoveUpdate, false)]
    [InlineData(PartitionSelection.GameOnly, false)]
    [InlineData(PartitionSelection.RemoveUpdate, true)]
    public async Task Matches_the_desktop_writer(PartitionSelection selection, bool markEverythingFirst)
    {
        SyntheticDisc disc = SyntheticDisc.CreateStandard();
        WbfsPlan plan = WbfsWriter.Plan(disc.Disc, selection);

        byte[] expected = WriterOutput(disc.Disc, plan);
        byte[] actual = await BuilderOutput(disc.Disc, plan, markEverythingFirst);

        Assert.Equal(expected.Length, actual.Length);
        Assert.True(expected.AsSpan().SequenceEqual(actual), "streamed WBFS differs from the writer's");
    }

    [Fact]
    public void Marking_a_block_already_decided_is_an_error()
    {
        StreamingWbfsBuilder builder = new((_, _) => ValueTask.CompletedTask);
        builder.UsageFinalBefore(4L * Block);

        Assert.Throws<InvalidOperationException>(() => builder.MarkUsed(Block + 0x8000, 0x8000));
        Assert.False(builder.IsNeeded(Block, Block));
        Assert.True(builder.IsNeeded(5L * Block, 1));
    }
}
