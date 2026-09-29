using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Gaming.Services;
using PCBoost.Gaming.Tests.Fakes;
using PCBoost.TestUtilities;

namespace PCBoost.Gaming.Tests;

public sealed class BenchmarkServiceTests
{
    private const double Frame60 = 1000d / 60d;

    private readonly FakePerformanceMonitor _monitor = new();
    private readonly FakeFrameTimeSource _frames = new();
    private readonly InMemoryBenchmarkRepository _repository = new();
    private readonly FakeProcessProvider _processes = new();
    private readonly FakeGameDetectionService _detection = new();
    private readonly FakeClock _clock = new();
    private readonly GamingOptions _options = new();

    private BenchmarkService Create() => new(_monitor, _frames, _repository, _processes, _detection, _clock, _options);

    private static BenchmarkRun Run(FrameStats frames, int samples = 10, double cpu = 50, double? gpu = 70) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = DateTimeOffset.UtcNow,
        Phase = BenchmarkPhase.Before,
        Duration = TimeSpan.FromSeconds(60),
        CpuAveragePercent = cpu,
        CpuMaxPercent = cpu + 20,
        GpuAveragePercent = gpu,
        GpuMaxPercent = gpu + 10,
        RamAveragePercent = 60,
        DiskActiveAveragePercent = null,
        Frames = frames,
        SampleCount = samples,
    };

    private static FrameStats Fps(double fps, double low) => new(fps, low, low - 5, 1000 / fps, 1000 / low, 1000, TimeSpan.FromSeconds(60));

    [Fact]
    public void Compare_computes_differences_only_when_both_sides_are_measured()
    {
        var comparison = Create().Compare(Run(Fps(55, 40)), Run(Fps(60, 48), cpu: 45, gpu: null));
        var d = comparison.Deltas.ToDictionary(x => x.Metric);

        Assert.Equal(5, d[BenchmarkMetrics.AverageFps].Difference!.Value, 6);
        Assert.True(d[BenchmarkMetrics.AverageFps].HigherIsBetter);
        Assert.True(d[BenchmarkMetrics.OnePercentLowFps].HigherIsBetter);
        Assert.True(d[BenchmarkMetrics.PointOnePercentLowFps].HigherIsBetter);
        Assert.False(d[BenchmarkMetrics.AverageFrameTimeMs].HigherIsBetter);
        Assert.False(d[BenchmarkMetrics.P99FrameTimeMs].HigherIsBetter);
        Assert.False(d[BenchmarkMetrics.CpuAveragePercent].HigherIsBetter);
        Assert.Equal(-5, d[BenchmarkMetrics.CpuAveragePercent].Difference!.Value, 6);
        Assert.Null(d[BenchmarkMetrics.GpuAveragePercent].Difference);     // GPU non mesuré « après »
        Assert.Equal(70, d[BenchmarkMetrics.GpuAveragePercent].Before);
        Assert.Null(d[BenchmarkMetrics.DiskActiveAveragePercent].Difference); // jamais mesuré
    }

    [Fact]
    public void Compare_returns_null_differences_when_frames_or_samples_were_not_measured()
    {
        var comparison = Create().Compare(Run(FrameStats.Empty), Run(Fps(60, 48), samples: 0));
        var d = comparison.Deltas.ToDictionary(x => x.Metric);

        Assert.Null(d[BenchmarkMetrics.AverageFps].Difference);
        Assert.Null(d[BenchmarkMetrics.AverageFps].Before);
        Assert.Equal(60, d[BenchmarkMetrics.AverageFps].After);
        Assert.Null(d[BenchmarkMetrics.OnePercentLowFps].Difference);
        Assert.Null(d[BenchmarkMetrics.CpuAveragePercent].Difference);
        Assert.Null(d[BenchmarkMetrics.CpuAveragePercent].After);
        Assert.Null(d[BenchmarkMetrics.RamAveragePercent].Difference);
    }

    [Fact]
    public async Task Run_collects_samples_and_frames_saves_and_restores_monitor_mode()
    {
        _monitor.SetRunning(true, MonitoringMode.Background);
        _processes.Add(4242, "eldenring.exe", @"D:\Jeux\ELDEN RING\Game\eldenring.exe");
        var progress = new SyncProgress();
        var step = 0;
        var t = 0d;
        _options.Delay = (d, _) =>
        {
            step++;
            _clock.Advance(d);
            _monitor.Publish(FakePerformanceMonitor.Sample(_clock.UtcNow, 40 + step, 55, gpu: 90, disk: step % 2 == 0 ? 10 : null));
            var batch = new double[15];
            for (var i = 0; i < batch.Length; i++) { batch[i] = t; t += Frame60; }
            _frames.LastSession?.Push(batch);
            return Task.CompletedTask;
        };

        var run = await Create().RunAsync(BenchmarkPhase.Before, TimeSpan.FromSeconds(2), "Avant", 4242, null, progress);

        Assert.Equal(8, run.SampleCount);
        Assert.Equal(41 + 3.5, run.CpuAveragePercent, 6);
        Assert.Equal(48, run.CpuMaxPercent);
        Assert.Equal(90, run.GpuAveragePercent);
        Assert.Equal(10, run.DiskActiveAveragePercent);
        Assert.Equal(55, run.RamAveragePercent, 6);
        Assert.Equal("eldenring", run.GameName);
        Assert.Equal("Avant", run.Label);
        Assert.Equal(60, run.Frames.AverageFps!.Value, 3);
        Assert.Equal(120, run.Frames.FrameCount);
        Assert.Equal([4242], _frames.StartRequests);
        Assert.Equal(0, progress.Values[0]);
        Assert.Equal(1, progress.Values[^1]);
        Assert.True(_frames.LastSession!.Disposed);
        Assert.Equal(MonitoringMode.Background, _monitor.Mode);
        Assert.Equal(run.Id, Assert.Single(await _repository.GetRecentAsync(10)).Id);
    }

    [Fact]
    public async Task Run_without_game_does_not_capture_frames_and_leaves_them_unmeasured()
    {
        _monitor.SetRunning(false, MonitoringMode.Background);
        _options.Delay = (d, _) =>
        {
            _clock.Advance(d);
            _monitor.Publish(FakePerformanceMonitor.Sample(_clock.UtcNow, 30, 50));
            return Task.CompletedTask;
        };

        var run = await Create().RunAsync(BenchmarkPhase.Standalone, TimeSpan.FromSeconds(1), null, null, null);

        Assert.False(run.Frames.HasData);
        Assert.Null(run.GpuAveragePercent);
        Assert.Empty(_frames.StartRequests);
        Assert.False(_monitor.IsRunning);
        Assert.Equal(1, _monitor.StopCalls);
    }

    [Fact]
    public async Task Refused_capture_is_reported_and_fps_stay_unmeasured()
    {
        _frames.Grant = false;
        _processes.Add(4242, "cs2.exe", @"D:\cs2.exe");
        _options.Delay = (d, _) => { _clock.Advance(d); return Task.CompletedTask; };
        var service = Create();

        var run = await service.RunAsync(BenchmarkPhase.After, TimeSpan.FromSeconds(1), null, 4242, Guid.NewGuid(), null);

        Assert.False(run.Frames.HasData);
        Assert.Equal(FrameCaptureAvailability.RequiresElevation, service.LastFrameCapture);
        Assert.Equal(0, run.SampleCount);
        Assert.NotNull(run.PairedRunId);
    }

    [Fact]
    public async Task Cancelled_run_is_not_saved_and_monitor_is_restored()
    {
        _monitor.SetRunning(true, MonitoringMode.Background);
        using var cts = new CancellationTokenSource();
        _options.Delay = (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().RunAsync(BenchmarkPhase.Before, TimeSpan.FromSeconds(5), null, null, null, null, cts.Token));

        Assert.Empty(await _repository.GetRecentAsync(10));
        Assert.Equal(MonitoringMode.Background, _monitor.Mode);
    }

    private sealed class SyncProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public void Report(double value) => Values.Add(value);
    }
}
