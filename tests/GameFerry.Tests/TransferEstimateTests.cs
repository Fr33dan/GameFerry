using GameFerry.Engine.Library;
using GameFerry.Engine.Transfer;

namespace GameFerry.Tests;

public sealed class TransferEstimateTests
{
    private const long MB = 1L << 20;

    private static readonly SourceItem Item = new(@"C:\games\Game.7z", 1, DateTime.UtcNow);

    private static TransferProgress At(int index, TransferStage stage, long bytes = 0, long total = 0, int count = 10) =>
        new(Item, index, count, stage, total > 0 ? bytes / (double)total : 0) { Bytes = bytes, TotalBytes = total };

    /// <summary>
    /// Ten 500 MB games to a drive that writes and reads back at 2 MB/s, each game prepared in 30 seconds
    /// while the one before it is written, as happens when copying to a card over Remote Desktop.
    /// Returns the estimate every ten seconds, with the true time left alongside it.
    /// </summary>
    private static List<(TimeSpan Estimate, TimeSpan Truth)> SlowDrive()
    {
        const long size = 500 * MB;
        const double speed = 2 * MB;
        TransferEstimate estimate = new([.. Enumerable.Repeat(size, 10)], verify: true);
        TimeSpan perPass = TimeSpan.FromSeconds(size / speed);
        TimeSpan finish = TimeSpan.FromSeconds(30) + perPass * 20;

        List<(TimeSpan, TimeSpan)> samples = [];
        TimeSpan now = TimeSpan.Zero;
        void Sample()
        {
            if (estimate.Remaining(now) is { } left)
                samples.Add((left, finish - now));
        }

        for (now = TimeSpan.Zero; now < TimeSpan.FromSeconds(30); now += TimeSpan.FromSeconds(10))
        {
            estimate.Report(At(0, TransferStage.Extracting), now);
            Sample();
        }

        for (int game = 0; game < 10; game++)
        {
            foreach (TransferStage stage in (TransferStage[])[TransferStage.Writing, TransferStage.Verifying])
            {
                TimeSpan start = now;
                for (; now - start < perPass; now += TimeSpan.FromSeconds(10))
                {
                    estimate.Report(At(game, stage, (long)((now - start).TotalSeconds * speed), size), now);
                    Sample();
                }

                now = start + perPass;
                estimate.Report(At(game, stage, size, size), now);
            }

            estimate.Report(At(game, TransferStage.Done), now);
        }

        Assert.Equal(1, estimate.Fraction);
        Assert.Equal(TimeSpan.Zero, estimate.Remaining(now));
        return samples;
    }

    [Fact]
    public void On_a_slow_drive_the_estimate_comes_down_steadily_instead_of_climbing()
    {
        List<(TimeSpan Estimate, TimeSpan Truth)> samples = SlowDrive();

        // Nothing is promised during the first preparation, and from then on it only falls.
        Assert.True(samples.Count > 100);
        for (int i = 1; i < samples.Count; i++)
            Assert.True(samples[i].Estimate <= samples[i - 1].Estimate + TimeSpan.FromSeconds(1), $"rose at sample {i}");

        // Once the first game's preparation no longer weighs much (about ten minutes in), it is within 10% of
        // the truth and stays there.
        foreach ((TimeSpan estimate, TimeSpan truth) in samples.Where(s => s.Truth < TimeSpan.FromMinutes(75)))
            Assert.InRange(estimate.TotalSeconds, truth.TotalSeconds * 0.95, truth.TotalSeconds * 1.1);
    }

    [Fact]
    public void Nothing_is_estimated_until_enough_has_been_written_to_go_on()
    {
        TransferEstimate estimate = new([500 * MB, 500 * MB], verify: true);

        estimate.Report(At(0, TransferStage.Extracting), TimeSpan.FromSeconds(40));
        Assert.Null(estimate.Remaining(TimeSpan.FromSeconds(40)));
        Assert.Equal(0, estimate.Fraction);

        estimate.Report(At(0, TransferStage.Writing, 8 * MB, 500 * MB), TimeSpan.FromSeconds(44));
        Assert.Null(estimate.Remaining(TimeSpan.FromSeconds(44)));

        estimate.Report(At(0, TransferStage.Writing, 32 * MB, 500 * MB), TimeSpan.FromSeconds(56));
        Assert.NotNull(estimate.Remaining(TimeSpan.FromSeconds(56)));
    }

    [Fact]
    public void Games_that_end_early_drop_out_of_what_is_left_and_exact_sizes_replace_estimates()
    {
        TransferEstimate estimate = new([100 * MB, 100 * MB, 100 * MB, 100 * MB], verify: false);

        // The first game turns out bigger than estimated once its size is known exactly.
        estimate.Report(At(0, TransferStage.Writing, 100 * MB, 200 * MB, count: 4), TimeSpan.FromSeconds(50));
        Assert.Equal(100.0 / 500, estimate.Fraction, 3);

        // The second was already on the target and the third failed before writing anything.
        estimate.Report(At(1, TransferStage.Skipped, count: 4), TimeSpan.FromSeconds(50));
        estimate.Report(At(2, TransferStage.Failed, count: 4), TimeSpan.FromSeconds(50));
        Assert.Equal(100.0 / 300, estimate.Fraction, 3);

        // 100 MB in 50 s, and 200 MB to go: about 100 s more.
        Assert.Equal(100, estimate.Remaining(TimeSpan.FromSeconds(50))!.Value.TotalSeconds, 1);
    }

    [Fact]
    public void Speed_is_the_average_of_the_writing_or_reading_back_under_way()
    {
        TransferEstimate estimate = new([400 * MB], verify: true);

        estimate.Report(At(0, TransferStage.Writing, 0, 400 * MB, count: 1), TimeSpan.FromSeconds(30));
        estimate.Report(At(0, TransferStage.Writing, 1 * MB, 400 * MB, count: 1), TimeSpan.FromSeconds(31));
        Assert.Null(estimate.BytesPerSecond);

        estimate.Report(At(0, TransferStage.Writing, 40 * MB, 400 * MB, count: 1), TimeSpan.FromSeconds(50));
        Assert.Equal(2 * MB, estimate.BytesPerSecond!.Value, 1);

        // Reading back starts its own average.
        estimate.Report(At(0, TransferStage.Verifying, 0, 400 * MB, count: 1), TimeSpan.FromSeconds(230));
        estimate.Report(At(0, TransferStage.Verifying, 100 * MB, 400 * MB, count: 1), TimeSpan.FromSeconds(240));
        Assert.Equal(10 * MB, estimate.BytesPerSecond!.Value, 1);

        estimate.Report(At(0, TransferStage.Analyzing, count: 1), TimeSpan.FromSeconds(250));
        Assert.Null(estimate.BytesPerSecond);
    }
}
