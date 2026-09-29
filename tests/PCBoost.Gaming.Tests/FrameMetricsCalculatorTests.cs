using PCBoost.Core.Models.Gaming;
using PCBoost.Gaming.Metrics;

namespace PCBoost.Gaming.Tests;

public sealed class FrameMetricsCalculatorTests
{
    private const double Frame60 = 1000d / 60d;

    private static List<double> Regular(int frames, double intervalMs, double start = 1000)
        => Enumerable.Range(0, frames).Select(i => start + i * intervalMs).ToList();

    [Fact]
    public void Regular_60_fps_gives_60()
    {
        var stats = FrameMetricsCalculator.Calculate(Regular(601, Frame60));

        Assert.True(stats.HasData);
        Assert.Equal(60, stats.AverageFps!.Value, 6);
        Assert.Equal(60, stats.OnePercentLowFps!.Value, 6);
        Assert.Equal(60, stats.PointOnePercentLowFps!.Value, 6);
        Assert.Equal(Frame60, stats.AverageFrameTimeMs!.Value, 6);
        Assert.Equal(Frame60, stats.P99FrameTimeMs!.Value, 6);
        Assert.Equal(601, stats.FrameCount);
        Assert.Equal(10_000, stats.Duration.TotalMilliseconds, 3);
    }

    [Fact]
    public void A_single_100_ms_spike_drives_the_lows()
    {
        // 599 intervalles réguliers de 16,67 ms + un pic de 100 ms = 600 intervalles.
        var timestamps = Regular(300, Frame60);
        var t = timestamps[^1] + 100;
        timestamps.Add(t);
        for (var i = 1; i < 301; i++) timestamps.Add(t + i * Frame60);

        var stats = FrameMetricsCalculator.Calculate(timestamps);

        Assert.Equal(601, stats.FrameCount);
        var total = 599 * Frame60 + 100;
        Assert.Equal(600 * 1000 / total, stats.AverageFps!.Value, 6);
        // 1 % de 600 = 6 intervalles les plus longs : le pic et 5 intervalles normaux.
        Assert.Equal(1000 / ((100 + 5 * Frame60) / 6), stats.OnePercentLowFps!.Value, 6);
        Assert.Equal(32.727, stats.OnePercentLowFps.Value, 2);
        // 0,1 % de 600 → au moins 1 intervalle : le pic seul.
        Assert.Equal(10, stats.PointOnePercentLowFps!.Value, 6);
        Assert.Equal(Frame60, stats.P99FrameTimeMs!.Value, 6);
    }

    [Theory]
    [InlineData(new double[0])]
    [InlineData(new[] { 5.0 })]
    [InlineData(new[] { 5.0, 5.0 })]
    [InlineData(new[] { 10.0, 5.0 })]
    [InlineData(new[] { 0.0, 6000.0 })]
    public void Fewer_than_two_measurable_frames_is_empty(double[] timestamps)
    {
        var stats = FrameMetricsCalculator.Calculate(timestamps);

        Assert.Same(FrameStats.Empty, stats);
        Assert.False(stats.HasData);
        Assert.Null(stats.AverageFps);
    }

    [Fact]
    public void Pauses_longer_than_five_seconds_are_ignored()
    {
        var timestamps = Regular(61, Frame60);
        var resume = timestamps[^1] + 30_000; // jeu réduit 30 s
        timestamps.AddRange(Regular(61, Frame60, resume));

        var stats = FrameMetricsCalculator.Calculate(timestamps);

        Assert.Equal(60, stats.AverageFps!.Value, 6);
        Assert.Equal(121, stats.FrameCount);
        Assert.Equal(2000, stats.Duration.TotalMilliseconds, 3);
    }

    [Fact]
    public void One_percent_low_uses_at_least_one_interval()
    {
        Assert.Equal(1, FrameMetricsCalculator.LowCount(10, 0.01));
        Assert.Equal(1, FrameMetricsCalculator.LowCount(100, 0.01));
        Assert.Equal(2, FrameMetricsCalculator.LowCount(101, 0.01));
        Assert.Equal(6, FrameMetricsCalculator.LowCount(600, 0.01));
        Assert.Equal(1, FrameMetricsCalculator.LowCount(600, 0.001));
    }

    [Fact]
    public void Rolling_window_keeps_only_the_last_sixty_seconds()
    {
        var window = new RollingFrameWindow(TimeSpan.FromSeconds(60));
        // 30 s à 30 FPS puis 60 s à 60 FPS : la fenêtre ne voit plus que le 60 FPS.
        var slow = Regular(900, 1000d / 30);
        window.Add(slow);
        window.Add(Regular(3601, Frame60, slow[^1] + Frame60));

        var stats = window.Compute();

        Assert.Equal(60, stats.AverageFps!.Value, 3);
        Assert.InRange(window.Count, 3600, 3602);
    }

    [Fact]
    public void Rolling_window_restarts_when_the_clock_goes_backwards()
    {
        var window = new RollingFrameWindow(TimeSpan.FromSeconds(60));
        window.Add(Regular(100, Frame60, 1_000_000));
        window.Add(Regular(10, Frame60, 0));

        Assert.Equal(10, window.Count);
    }

    [Fact]
    public void Streaming_accumulator_matches_exact_calculation()
    {
        var random = new Random(42);
        var timestamps = new List<double> { 0 };
        for (var i = 0; i < 20_000; i++)
        {
            var interval = random.NextDouble() < 0.02 ? 30 + random.NextDouble() * 70 : 6 + random.NextDouble() * 4;
            timestamps.Add(timestamps[^1] + interval);
        }
        var accumulator = new FrameStatsAccumulator();
        foreach (var chunk in timestamps.Chunk(137)) accumulator.Add(chunk);

        var exact = FrameMetricsCalculator.Calculate(timestamps);
        var streamed = accumulator.Compute();

        Assert.Equal(exact.FrameCount, streamed.FrameCount);
        Assert.Equal(exact.AverageFps!.Value, streamed.AverageFps!.Value, 6);
        Assert.Equal(exact.AverageFrameTimeMs!.Value, streamed.AverageFrameTimeMs!.Value, 6);
        Assert.Equal(1000 / exact.OnePercentLowFps!.Value, 1000 / streamed.OnePercentLowFps!.Value, FrameStatsAccumulator.BucketWidthMs);
        Assert.Equal(1000 / exact.PointOnePercentLowFps!.Value, 1000 / streamed.PointOnePercentLowFps!.Value, FrameStatsAccumulator.BucketWidthMs);
        Assert.InRange(streamed.P99FrameTimeMs!.Value, exact.P99FrameTimeMs!.Value - FrameStatsAccumulator.BucketWidthMs, exact.P99FrameTimeMs.Value + FrameStatsAccumulator.BucketWidthMs);
    }

    [Fact]
    public void Empty_accumulator_is_empty()
        => Assert.False(new FrameStatsAccumulator().Compute().HasData);
}
