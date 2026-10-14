using GameFerry.Engine.Library;
using GameFerry.Engine.Transfer;

namespace GameFerry.Core.Transfer;

public sealed record TransferResult(SourceItem Item, TransferStage Outcome, string? Message = null)
{
    public long BytesWritten { get; init; }

    public string? Folder { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>True when NKit confirmed the rebuilt image against the Redump database.</summary>
    public bool? MatchesRedump { get; init; }
}

/// <summary>A failure with a message meant for the user.</summary>
public sealed class TransferException(string message) : Exception(message);

/// <summary>An <see cref="IProgress{T}"/> that calls straight through on the reporting thread.</summary>
internal sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
