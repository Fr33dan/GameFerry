using GameFerry.Engine.Disc;

namespace GameFerry.Engine.Library;

public enum SourceFormat
{
    Unknown,
    Iso,
    NkitIso,
    NkitGcz,
    Gcz,
    Wbfs,
}

/// <summary>A file that may hold a game: a loose disc image or an archive containing one.</summary>
public sealed class SourceItem
{
    private static readonly string[] ArchiveExtensions = [".7z", ".zip", ".rar"];

    // Update partitions in NKit's recovery set average about 150 MB.
    private const long TypicalUpdatePartitionSize = 160L << 20;

    public SourceItem(string path, long fileSize, DateTime lastWriteUtc)
    {
        Path = path;
        FileSize = fileSize;
        LastWriteUtc = lastWriteUtc;
        IsArchive = ArchiveExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    public string Path { get; }

    public long FileSize { get; }

    public DateTime LastWriteUtc { get; }

    public bool IsArchive { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>True once the image inside has been identified (or found to be unusable).</summary>
    public bool Inspected { get; set; }

    /// <summary>Name of the image inside an archive.</summary>
    public string? EntryName { get; set; }

    public string? Id6 { get; set; }

    /// <summary>The title stored in the disc header.</summary>
    public string? DiscTitle { get; set; }

    public DiscKind Kind { get; set; }

    public SourceFormat Format { get; set; }

    /// <summary>Size of the image as stored (uncompressed, for archives).</summary>
    public long SourceSize { get; set; }

    /// <summary>Size of the full disc the image represents.</summary>
    public long ImageSize { get; set; }

    /// <summary>True for an NKit image whose update partition must be restored from NKit's recovery files.</summary>
    public bool UpdateRemoved { get; set; }

    /// <summary>Why this file can't be transferred, if it can't.</summary>
    public string? Problem { get; set; }

    /// <summary>True when the image has to go through NKit to become a plain ISO first.</summary>
    public bool NeedsNKit => IsArchive || Format is SourceFormat.NkitIso or SourceFormat.NkitGcz or SourceFormat.Gcz;

    public bool CanTransfer => Inspected && Problem is null && Kind == DiscKind.Wii && Id6 is not null;

    /// <summary>
    /// Rough size of the WBFS file this will become; the exact size is only known once the disc has been rebuilt.
    /// NKit images are already stripped of padding, and the reference conversions came out about 5% larger
    /// (hashes restored, 2 MiB block rounding). An update partition that NKit has to put back adds its own size.
    /// </summary>
    public long EstimateWbfsSize(PartitionSelection partitions)
    {
        switch (Format)
        {
            case SourceFormat.NkitIso or SourceFormat.NkitGcz:
                long size = (long)(SourceSize * 1.06) + (8 << 20);
                return UpdateRemoved && partitions == PartitionSelection.All ? size + TypicalUpdatePartitionSize : size;
            case SourceFormat.Wbfs:
                return SourceSize;
            default:
                return ImageSize;
        }
    }

    public string FormatLabel
    {
        get
        {
            string format = Format switch
            {
                SourceFormat.NkitIso => "NKit ISO",
                SourceFormat.NkitGcz => "NKit GCZ",
                SourceFormat.Gcz => "GCZ",
                SourceFormat.Wbfs => "WBFS",
                SourceFormat.Iso => "ISO",
                _ => "?",
            };
            return IsArchive ? $"{format} in {System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant()}" : format;
        }
    }
}
