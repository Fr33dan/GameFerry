using System.Buffers.Binary;

namespace GameFerry.Engine.Disc;

/// <summary>One entry of the disc's partition table.</summary>
/// <param name="Group">Which of the four partition-table groups lists this partition.</param>
/// <param name="Offset">Disc offset of the partition.</param>
/// <param name="Type">0 = game data, 1 = update, 2 = channel installer; anything else is a four-character title ID.</param>
public sealed record WiiPartitionEntry(int Group, long Offset, uint Type)
{
    public bool IsData => Type == 0;

    public bool IsUpdate => Type == 1;

    public string TypeName => Type switch
    {
        0 => "DATA",
        1 => "UPDATE",
        2 => "CHANNEL",
        _ => FourCc(Type),
    };

    private static string FourCc(uint value)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(value >> (24 - 8 * i));
            if (b is < 0x20 or >= 0x7F)
                return $"0x{value:X8}";
            chars[i] = (char)b;
        }

        return new string(chars);
    }
}

public static class WiiPartitionTable
{
    public const int GroupCount = 4;
    private const int GroupHeaderSize = GroupCount * 8;
    private const int MaxPartitionsPerGroup = 64;

    /// <summary>Reads every partition from all four table groups, in table order.</summary>
    public static List<WiiPartitionEntry> Read(IDiscReader disc)
    {
        Span<byte> groups = stackalloc byte[GroupHeaderSize];
        disc.ReadAt(WiiConstants.PartitionTableOffset, groups);

        List<WiiPartitionEntry> partitions = [];
        for (int group = 0; group < GroupCount; group++)
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(groups[(group * 8)..]);
            if (count == 0)
                continue;

            long tableOffset = (long)BinaryPrimitives.ReadUInt32BigEndian(groups[(group * 8 + 4)..]) << 2;
            if (count > MaxPartitionsPerGroup || tableOffset < WiiConstants.PartitionTableOffset || tableOffset >= WiiConstants.HeaderAreaSize)
                throw new InvalidDataException($"Partition table group {group} is not valid (count {count}, offset 0x{tableOffset:X}).");

            byte[] entries = new byte[count * 8];
            disc.ReadAt(tableOffset, entries);
            for (int i = 0; i < count; i++)
            {
                long offset = (long)BinaryPrimitives.ReadUInt32BigEndian(entries.AsSpan(i * 8)) << 2;
                uint type = BinaryPrimitives.ReadUInt32BigEndian(entries.AsSpan(i * 8 + 4));
                partitions.Add(new WiiPartitionEntry(group, offset, type));
            }
        }

        return partitions;
    }

    /// <summary>
    /// Rewrites the partition table in place so it lists only the partitions <paramref name="keep"/> accepts.
    /// As in libwbfs (and so Wii Backup Manager), kept entries are moved to the front of their group and the
    /// group's count is lowered; whatever is left in the slots beyond the count is not touched.
    /// </summary>
    /// <param name="discStart">Bytes of the disc from offset 0, covering at least the header area.</param>
    public static void Rewrite(Span<byte> discStart, Func<WiiPartitionEntry, bool> keep)
    {
        Span<byte> groups = discStart.Slice((int)WiiConstants.PartitionTableOffset, GroupHeaderSize);
        for (int group = 0; group < GroupCount; group++)
        {
            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(groups[(group * 8)..]);
            if (count == 0)
                continue;

            int tableOffset = (int)(BinaryPrimitives.ReadUInt32BigEndian(groups[(group * 8 + 4)..]) << 2);
            Span<byte> entries = discStart.Slice(tableOffset, count * 8);

            int kept = 0;
            for (int i = 0; i < count; i++)
            {
                Span<byte> entry = entries.Slice(i * 8, 8);
                long offset = (long)BinaryPrimitives.ReadUInt32BigEndian(entry) << 2;
                uint type = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
                if (!keep(new WiiPartitionEntry(group, offset, type)))
                    continue;

                entry.CopyTo(entries.Slice(kept * 8, 8));
                kept++;
            }

            BinaryPrimitives.WriteUInt32BigEndian(groups[(group * 8)..], (uint)kept);
        }
    }
}
