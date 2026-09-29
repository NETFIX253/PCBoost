using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Diagnostics.Monitoring;
using PCBoost.Diagnostics.Tests.TestSupport;
using PCBoost.TestUtilities;

namespace PCBoost.Diagnostics.Tests;

public sealed class PerformanceMonitorTests
{
    private readonly FakeClock _clock = new(Reports.Now);
    private readonly FakeSystemMetricsProvider _metrics;
    private readonly CountingHardwareProvider _hardware = new();

    public PerformanceMonitorTests() => _metrics = new FakeSystemMetricsProvider(_clock);

    private PerformanceMonitor Monitor(PerformanceMonitorOptions? options = null)
        => new(_metrics, _hardware, _clock, NullLogger<PerformanceMonitor>.Instance, options);

    private static PerformanceMonitorOptions FastOptions() => new()
    {
        ActiveInterval = TimeSpan.FromMilliseconds(15),
        BackgroundInterval = TimeSpan.FromMilliseconds(40),
        ActiveTemperatureInterval = TimeSpan.FromMilliseconds(15),
        BackgroundTemperatureInterval = TimeSpan.FromMilliseconds(40),
    };

    [Fact]
    public void Modes_have_documented_intervals()
    {
        using var monitor = Monitor();
        Assert.Equal(MonitoringMode.Background, monitor.Mode);
        Assert.Equal(TimeSpan.FromSeconds(5), monitor.CurrentInterval);

        monitor.SetMode(MonitoringMode.Active);
        Assert.Equal(TimeSpan.FromSeconds(1), monitor.CurrentInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), monitor.TemperatureIntervalFor(MonitoringMode.Active));
        Assert.Equal(TimeSpan.FromSeconds(30), monitor.TemperatureIntervalFor(MonitoringMode.Background));

        monitor.SetMode(MonitoringMode.Paused);
        Assert.Equal(Timeout.InfiniteTimeSpan, monitor.CurrentInterval);
    }

    [Fact]
    public async Task Mode_change_raises_event_on_thread_pool()
    {
        using var monitor = Monitor();
        var raised = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.ModeChanged += (_, _) => raised.TrySetResult(Thread.CurrentThread.IsThreadPoolThread);
        monitor.SetMode(MonitoringMode.Active);
        Assert.True(await raised.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Same_mode_does_not_raise_event()
    {
        using var monitor = Monitor();
        var count = 0;
        monitor.ModeChanged += (_, _) => Interlocked.Increment(ref count);
        monitor.SetMode(MonitoringMode.Background);
        Thread.Sleep(50);
        Assert.Equal(0, count);
    }

    [Fact]
    public void History_keeps_samples_within_window()
    {
        using var monitor = Monitor();
        monitor.SetMode(MonitoringMode.Active);
        for (var i = 0; i < 10; i++)
        {
            _metrics.Enqueue(_metrics.Create(i * 10, 50));
            monitor.Tick();
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(10, monitor.GetHistory(TimeSpan.FromMinutes(1)).Count);
        // Maintenant = t0 + 10 s : la fenêtre de 3 s couvre t0+7, t0+8, t0+9.
        var recent = monitor.GetHistory(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { 70d, 80d, 90d }, recent.Select(s => s.CpuPercent));
        Assert.Empty(monitor.GetHistory(TimeSpan.Zero));
        Assert.Equal(90, monitor.Latest!.CpuPercent);
    }

    [Fact]
    public void History_is_limited_to_its_duration()
    {
        using var monitor = Monitor(new PerformanceMonitorOptions { HistoryDuration = TimeSpan.FromSeconds(20) });
        for (var i = 0; i < 100; i++)
        {
            monitor.Tick();
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
        // Capacité de 21 échantillons ; une fenêtre plus longue est ramenée à la durée d'historique.
        Assert.InRange(monitor.GetHistory(TimeSpan.FromHours(1)).Count, 19, 21);
    }

    [Fact]
    public void Statistics_report_current_average_and_max()
    {
        using var monitor = Monitor();
        foreach (var (cpu, diskValue) in new (double, double?)[] { (10, null), (30, 40), (20, 60) })
        {
            _metrics.Enqueue(_metrics.Create(cpu, 50, disk: diskValue, gpu: null));
            monitor.Tick();
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var cpuStats = monitor.GetStatistics(MetricKind.Cpu, TimeSpan.FromMinutes(1));
        Assert.Equal(20, cpuStats.Current);
        Assert.Equal(20, cpuStats.Average!.Value, 3);
        Assert.Equal(30, cpuStats.Maximum);
        Assert.Equal(3, cpuStats.SampleCount);

        var disk = monitor.GetStatistics(MetricKind.Disk, TimeSpan.FromMinutes(1));
        Assert.Equal(60, disk.Current);
        Assert.Equal(50, disk.Average!.Value, 3);
        Assert.Equal(2, disk.SampleCount);

        var gpu = monitor.GetStatistics(MetricKind.Gpu, TimeSpan.FromMinutes(1));
        Assert.Null(gpu.Current);
        Assert.Null(gpu.Average);
        Assert.Null(gpu.Maximum);
        Assert.Equal(0, gpu.SampleCount);
    }

    [Fact]
    public void Temperatures_are_read_on_their_own_cadence()
    {
        using var monitor = Monitor();
        monitor.SetMode(MonitoringMode.Active);
        for (var i = 0; i < 11; i++)
        {
            monitor.Tick();
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
        // t = 0, 5, 10 s en mode Active.
        Assert.Equal(3, _hardware.Calls);
        Assert.Equal(55, monitor.Temperatures.Cpu.Value);

        monitor.SetMode(MonitoringMode.Background);
        for (var i = 0; i < 40; i++)
        {
            monitor.Tick();
            _clock.Advance(TimeSpan.FromSeconds(1));
        }
        // En arrière-plan : toutes les 30 s → une seule lecture supplémentaire (t = 40 s).
        Assert.Equal(4, _hardware.Calls);

        var cpuTemperature = monitor.GetStatistics(MetricKind.CpuTemperature, TimeSpan.FromMinutes(5));
        Assert.Equal(55, cpuTemperature.Current);
        Assert.Equal(4, cpuTemperature.SampleCount);
    }

    [Fact]
    public void Unavailable_sensors_give_no_temperature_statistics()
    {
        _hardware.Readings = TemperatureReadings.None;
        using var monitor = Monitor();
        monitor.Tick();
        var stats = monitor.GetStatistics(MetricKind.GpuTemperature, TimeSpan.FromMinutes(1));
        Assert.Null(stats.Current);
        Assert.Equal(0, stats.SampleCount);
        Assert.False(monitor.Temperatures.Gpu.HasValue);
    }

    [Fact]
    public void Failing_providers_do_not_throw()
    {
        var metrics = new ThrowingMetricsProvider();
        using var monitor = new PerformanceMonitor(metrics, new CountingHardwareProvider { Throw = true }, _clock, NullLogger<PerformanceMonitor>.Instance);
        monitor.Tick();
        monitor.Tick();
        Assert.Null(monitor.Latest);
        Assert.Equal(2, metrics.Calls);
        Assert.Empty(monitor.GetHistory(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Timer_samples_in_active_mode_and_raises_events_on_thread_pool()
    {
        using var monitor = Monitor(FastOptions());
        monitor.SetMode(MonitoringMode.Active);
        var count = 0;
        var onPool = true;
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SampleAvailable += (_, _) =>
        {
            onPool &= Thread.CurrentThread.IsThreadPoolThread;
            if (Interlocked.Increment(ref count) >= 3) enough.TrySetResult();
        };

        monitor.Start();
        Assert.True(monitor.IsRunning);
        await enough.Task.WaitAsync(TimeSpan.FromSeconds(10));
        monitor.Stop();

        Assert.False(monitor.IsRunning);
        Assert.True(onPool);
        Assert.True(_metrics.SampleCalls >= 3);
    }

    [Fact]
    public async Task Paused_mode_stops_sampling_until_resumed()
    {
        using var monitor = Monitor(FastOptions());
        monitor.SetMode(MonitoringMode.Active);
        monitor.Start();
        await WaitUntil(() => _metrics.SampleCalls >= 2);

        monitor.SetMode(MonitoringMode.Paused);
        await Task.Delay(60); // laisse passer un éventuel échantillon en cours
        var frozen = _metrics.SampleCalls;
        await Task.Delay(200);
        Assert.Equal(frozen, _metrics.SampleCalls);
        Assert.True(monitor.IsRunning);

        monitor.SetMode(MonitoringMode.Active);
        await WaitUntil(() => _metrics.SampleCalls > frozen);
        monitor.Stop();
    }

    [Fact]
    public async Task Starting_in_paused_mode_takes_no_sample()
    {
        using var monitor = Monitor(FastOptions());
        monitor.SetMode(MonitoringMode.Paused);
        monitor.Start();
        await Task.Delay(150);
        Assert.Equal(0, _metrics.SampleCalls);
        monitor.Stop();
    }

    [Fact]
    public async Task Stop_and_dispose_end_sampling()
    {
        var monitor = Monitor(FastOptions());
        monitor.SetMode(MonitoringMode.Active);
        monitor.Start();
        monitor.Start(); // idempotent
        await WaitUntil(() => _metrics.SampleCalls >= 1);
        monitor.Dispose();
        await Task.Delay(60);
        var calls = _metrics.SampleCalls;
        await Task.Delay(150);
        Assert.Equal(calls, _metrics.SampleCalls);
        Assert.False(monitor.IsRunning);
        Assert.Throws<ObjectDisposedException>(monitor.Start);
        monitor.SetMode(MonitoringMode.Background); // ignoré après Dispose, sans exception
        Assert.Equal(MonitoringMode.Active, monitor.Mode);
    }

    [Fact]
    public async Task Throwing_subscriber_does_not_stop_the_loop()
    {
        using var monitor = Monitor(FastOptions());
        monitor.SetMode(MonitoringMode.Active);
        monitor.SampleAvailable += (_, _) => throw new InvalidOperationException("abonné");
        monitor.Start();
        await WaitUntil(() => _metrics.SampleCalls >= 3);
        monitor.Stop();
    }

    [Fact]
    public void Invalid_intervals_are_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Monitor(new PerformanceMonitorOptions { ActiveInterval = TimeSpan.Zero }));

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition non atteinte.");
            await Task.Delay(5);
        }
    }
}
