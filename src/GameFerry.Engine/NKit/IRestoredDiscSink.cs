using GameFerry.Engine.Disc;

namespace GameFerry.Engine.NKit;

/// <summary>Receives a Wii disc as <see cref="NKitWiiDecoder"/> restores it.</summary>
public interface IRestoredDiscSink
{
    /// <summary>
    /// Final bytes of the disc. Every byte arrives exactly once and mostly in order, but the disc header and a few
    /// groups of each partition (its file table, and any whose hashes NKit stored) are held back until the partition
    /// or the disc is finished. <paramref name="data"/> may be reused once the call completes.
    /// </summary>
    ValueTask WriteAsync(long discOffset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Marks whole sectors that hold something: the disc header and, in kept partitions, the partition header and
    /// every sector of the system areas and files, exactly as <see cref="DiscUsageMap"/> would mark them.
    /// </summary>
    void MarkUsed(long discOffset, long length);

    /// <summary>Nothing below <paramref name="discOffset"/> will be marked from now on.</summary>
    void UsageFinalBefore(long discOffset);

    /// <summary>
    /// Whether any byte of the range is wanted. The decoder skips rebuilding (and writing) ranges that are not, so a
    /// sink that keeps the whole disc must always say yes.
    /// </summary>
    bool IsNeeded(long discOffset, long length);

    /// <summary>
    /// True for a sink that keeps only 2 MiB blocks with a marked sector in them, as WBFS does; the decoder then also
    /// skips generating junk that can only land in dropped blocks. False for one that keeps the whole disc.
    /// </summary>
    bool KeepsOnlyUsedBlocks { get; }
}

/// <summary>What restoring an NKit image found.</summary>
/// <param name="DiscSize">Size of the original disc.</param>
/// <param name="DiscCrc">The original disc's CRC-32 as the NKit header records it; it covers a restored update partition.</param>
/// <param name="Partitions">The original disc's partition table.</param>
/// <param name="KeptPartitions">The partitions restored and kept by the selection, at their original offsets.</param>
/// <param name="GroupsChecked">Groups whose rebuilt hashes were checked against the disc's own hash table.</param>
/// <param name="GroupsFailingH3">Groups whose rebuilt hashes did not match it.</param>
/// <param name="GroupsSkipped">Groups not rebuilt because nothing the sink keeps lies in them.</param>
public sealed record NKitDecodeResult(
    long DiscSize,
    uint DiscCrc,
    IReadOnlyList<WiiPartitionEntry> Partitions,
    IReadOnlyList<WiiPartitionEntry> KeptPartitions,
    bool UpdatePartitionRemoved,
    int GroupsChecked,
    int GroupsFailingH3,
    int GroupsSkipped,
    IReadOnlyList<string> Warnings);
