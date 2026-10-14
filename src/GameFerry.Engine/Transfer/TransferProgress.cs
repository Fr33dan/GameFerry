using GameFerry.Engine.Library;

namespace GameFerry.Engine.Transfer;

public enum TransferStage
{
    Queued,

    /// <summary>7-Zip is unpacking the archive.</summary>
    Extracting,

    /// <summary>NKit is rebuilding the full ISO.</summary>
    Converting,

    /// <summary>Working out which parts of the disc hold data.</summary>
    Analyzing,

    Writing,

    /// <summary>Re-reading the written files from the target.</summary>
    Verifying,

    Done,
    Skipped,
    Failed,
    Cancelled,
}

/// <param name="Index">Zero-based position of the item in the queue.</param>
/// <param name="Fraction">How far the current stage has got, 0 to 1.</param>
public sealed record TransferProgress(SourceItem Item, int Index, int Count, TransferStage Stage, double Fraction, string? Message = null)
{
    /// <summary>True when this is the next game being prepared while an earlier one is still being written.</summary>
    public bool Background { get; init; }

    /// <summary>While writing or verifying: how many bytes this stage has done so far.</summary>
    public long Bytes { get; init; }

    /// <summary>While writing or verifying: how many bytes the game writes to the target in all.</summary>
    public long TotalBytes { get; init; }
}
