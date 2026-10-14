using GameFerry.Engine.Transfer;

namespace GameFerry.Cli;

/// <summary>
/// Console output with a single live progress line. On a terminal the line is redrawn in place;
/// when output is redirected, progress is written as occasional plain lines instead.
/// </summary>
internal sealed class ConsoleUi
{
    private const int RedirectedStepPercent = 25;

    private readonly Lock _lock = new();
    private readonly bool _interactive = !Console.IsOutputRedirected;
    private int _liveLength;
    private string? _lastStage;
    private int _lastPercent;

    public void WriteLine(string text = "")
    {
        lock (_lock)
        {
            // On a terminal the live line has just been wiped and must be drawn again; in redirected output
            // the last progress line is still there to read, and repeating it after every message is noise.
            ClearLive(forgetProgress: _interactive);
            Console.WriteLine(text);
        }
    }

    public void Progress(string stage, double fraction)
    {
        int percent = (int)Math.Clamp(fraction * 100, 0, 100);
        lock (_lock)
        {
            if (_interactive)
            {
                if (stage == _lastStage && percent == _lastPercent)
                    return;

                string text = $"  {stage} {percent,3}%";
                Console.Write('\r' + text.PadRight(_liveLength));
                _liveLength = text.Length;
            }
            else if (stage != _lastStage || percent >= _lastPercent + RedirectedStepPercent)
            {
                Console.WriteLine($"  {stage} {percent}%");
            }
            else
            {
                return;
            }

            _lastStage = stage;
            _lastPercent = percent;
        }
    }

    public void Progress(TransferProgress progress)
    {
        // The single progress line follows the game being written, not the one being prepared behind it.
        if (progress.Background)
            return;

        if (progress.Stage is TransferStage.Done or TransferStage.Skipped or TransferStage.Failed or TransferStage.Cancelled)
        {
            lock (_lock)
                ClearLive();
            return;
        }

        Progress($"[{progress.Index + 1}/{progress.Count}] {progress.Stage}", progress.Fraction);
    }

    public void EndProgress()
    {
        lock (_lock)
            ClearLive();
    }

    private void ClearLive(bool forgetProgress = true)
    {
        if (_liveLength > 0)
        {
            Console.Write('\r' + new string(' ', _liveLength) + '\r');
            _liveLength = 0;
        }

        if (forgetProgress)
        {
            _lastStage = null;
            _lastPercent = 0;
        }
    }
}
