namespace GameFerry.Engine.Transfer;

/// <summary>
/// Works out how far a transfer has got and how long the rest will take. Work is counted in bytes written to
/// the target and, when verifying, read back from it, game by game, so a slow drive shows in the estimate as
/// soon as the first game starts writing. The time taken so far, preparation included, is spread over the
/// bytes done and carried forward to the bytes still to go; the first game's preparation makes the earliest
/// estimates a little high, and they come down from there.
/// </summary>
public sealed class TransferEstimate
{
    // The first few seconds of writing say little about the drive.
    private const long MinimumBytes = 16L << 20;
    private static readonly TimeSpan MinimumTime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinimumStageTime = TimeSpan.FromSeconds(2);

    private readonly long[] _size;
    private readonly long[] _done;
    private readonly bool[] _over;
    private readonly int _passes;

    private (int Index, TransferStage Stage) _stage = (-1, TransferStage.Queued);
    private TimeSpan _stageStart;
    private TimeSpan _stageAt;
    private long _stageBytes;

    /// <param name="estimatedSizes">How much each game in the queue is expected to write, in queue order.</param>
    /// <param name="verify">Whether each game is read back after it is written, which doubles the work.</param>
    public TransferEstimate(IReadOnlyList<long> estimatedSizes, bool verify)
    {
        _size = [.. estimatedSizes];
        _done = new long[_size.Length];
        _over = new bool[_size.Length];
        _passes = verify ? 2 : 1;
    }

    /// <summary>How much of the work for all the games is done, 0 to 1.</summary>
    public double Fraction
    {
        get
        {
            long total = Total;
            return total == 0 ? 0 : Math.Clamp(_done.Sum() / (double)total, 0, 1);
        }
    }

    /// <summary>
    /// Average speed of the writing or reading back under way, in bytes per second; null at other stages or
    /// while the stage is too young to say.
    /// </summary>
    public double? BytesPerSecond
    {
        get
        {
            TimeSpan time = _stageAt - _stageStart;
            return _stage.Stage is TransferStage.Writing or TransferStage.Verifying && time >= MinimumStageTime
                ? _stageBytes / time.TotalSeconds
                : null;
        }
    }

    // A game that ended early (skipped, failed, cancelled) has nothing more to come.
    private long Total
    {
        get
        {
            long total = 0;
            for (int i = 0; i < _size.Length; i++)
                total += _over[i] ? _done[i] : Math.Max(_done[i], _size[i] * _passes);
            return total;
        }
    }

    /// <summary>Takes in a progress report.</summary>
    /// <param name="at">Time since the transfer started.</param>
    public void Report(TransferProgress progress, TimeSpan at)
    {
        int i = progress.Index;
        if (i < 0 || i >= _size.Length)
            return;

        switch (progress.Stage)
        {
            case TransferStage.Writing:
                if (progress.TotalBytes > 0)
                    _size[i] = progress.TotalBytes;
                _done[i] = progress.Bytes;
                FollowStage(progress, at);
                break;

            case TransferStage.Verifying:
                if (progress.TotalBytes > 0)
                    _size[i] = progress.TotalBytes;
                _done[i] = _size[i] + progress.Bytes;
                FollowStage(progress, at);
                break;

            case TransferStage.Done:
                _done[i] = _size[i] * _passes;
                _over[i] = true;
                break;

            case TransferStage.Skipped or TransferStage.Failed or TransferStage.Cancelled:
                _over[i] = true;
                break;

            default:
                // Preparing the next game happens while this one is written; only the writing counts.
                if (!progress.Background)
                    FollowStage(progress, at);
                break;
        }
    }

    /// <summary>Time left for all the games, or null while too little has been written to judge from.</summary>
    /// <param name="elapsed">Time since the transfer started.</param>
    public TimeSpan? Remaining(TimeSpan elapsed)
    {
        long done = _done.Sum();
        long left = Total - done;
        if (left <= 0)
            return done > 0 ? TimeSpan.Zero : null;
        if (done < MinimumBytes || elapsed < MinimumTime)
            return null;

        return elapsed * (left / (double)done);
    }

    private void FollowStage(TransferProgress progress, TimeSpan at)
    {
        if (_stage != (progress.Index, progress.Stage))
        {
            _stage = (progress.Index, progress.Stage);
            _stageStart = at;
        }

        _stageAt = at;
        _stageBytes = progress.Bytes;
    }
}
