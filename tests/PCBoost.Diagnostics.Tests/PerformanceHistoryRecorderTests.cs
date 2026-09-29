using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Diagnostics.Monitoring;
using PCBoost.Diagnostics.Tests.TestSupport;
using PCBoost.TestUtilities;

namespace PCBoost.Diagnostics.Tests;

public sealed class PerformanceHistoryRecorderTests
{
    private static readonly DateTimeOffset Minute0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeClock _clock = new(Minute0);
    private readonly FakePerformanceMonitor _monitor = new();
    private readonly InMemoryPerformanceSnapshotRepository _repository = new();

    private PerformanceHistoryRecorder Recorder() => new(_monitor, _repository, _clock, NullLogger<PerformanceHistoryRecorder>.Instance);

    private static SystemMetricsSample Sample(DateTimeOffset at, double cpu, double memory, double? disk = null, double? gpu = null)
        => new(at, cpu, memory, 0, 8L * ByteSize.GiB, disk, 0, 0, gpu, null, 0, 0, 100);

    [Fact]
    public async Task Samples_are_averaged_per_minute()
    {
        var recorder = Recorder();
        var temps = new TemperatureReadings(SensorReading.Of(50, "t"), SensorReading.Unavailable(Availability.NoSensor), SensorReading.Unavailable());
        recorder.Record(Sample(Minute0.AddSeconds(10), 10, 40, disk: 20), temps);
        recorder.Record(Sample(Minute0.AddSeconds(20), 20, 50, disk: null), temps);
        recorder.Record(Sample(Minute0.AddSeconds(30), 30, 60, disk: 40), temps);
        recorder.Record(Sample(Minute0.AddSeconds(65), 80, 70), temps); // minute suivante → la première minute est enregistrée
        await recorder.FlushAsync();

        var snapshots = await _repository.GetRangeAsync(Minute0.AddHours(-1), Minute0.AddHours(1));
        Assert.Equal(2, snapshots.Count);

        var first = snapshots[0];
        Assert.Equal(Minute0, first.Timestamp);
        Assert.Equal(20, first.CpuPercent, 3);
        Assert.Equal(50, first.MemoryPercent, 3);
        Assert.Equal(30, first.DiskActivePercent!.Value, 3); // valeur absente exclue de la moyenne
        Assert.Null(first.GpuPercent);
        Assert.Equal(50, first.CpuTemperatureC);
        Assert.Null(first.GpuTemperatureC);
        Assert.Null(first.Fps);

        Assert.Equal(Minute0.AddMinutes(1), snapshots[1].Timestamp);
        Assert.Equal(80, snapshots[1].CpuPercent, 3);
    }

    [Fact]
    public async Task Start_subscribes_to_monitor_and_stop_flushes_partial_minute()
    {
        var recorder = Recorder();
        recorder.Start();
        recorder.Start(); // idempotent
        Assert.True(recorder.IsRecording);
        Assert.Equal(1, _monitor.SubscriberCount);

        _monitor.Raise(Sample(Minute0.AddSeconds(5), 42, 55));
        recorder.Stop();
        Assert.Equal(0, _monitor.SubscriberCount);
        await recorder.FlushAsync();

        var snapshot = Assert.Single(await _repository.GetRangeAsync(Minute0.AddHours(-1), Minute0.AddHours(1)));
        Assert.Equal(42, snapshot.CpuPercent, 3);
    }

    [Fact]
    public async Task No_snapshot_without_samples()
    {
        var recorder = Recorder();
        recorder.Start();
        await recorder.FlushAsync();
        recorder.Stop();
        Assert.Empty(await _repository.GetRangeAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public async Task Snapshots_older_than_30_days_are_purged_once_per_day()
    {
        await _repository.AddRangeAsync([new PerformanceSnapshot(Minute0.AddDays(-40), 1, 1, null, null, null, null, null, null)]);
        var recorder = Recorder();
        recorder.Start();
        await recorder.FlushAsync();
        Assert.Empty(await _repository.GetRangeAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));

        // Nouvel instantané ancien : pas de nouvelle purge le même jour.
        await _repository.AddRangeAsync([new PerformanceSnapshot(Minute0.AddDays(-35), 1, 1, null, null, null, null, null, null)]);
        _clock.Advance(TimeSpan.FromHours(2));
        recorder.Record(Sample(_clock.UtcNow, 10, 10), TemperatureReadings.None);
        await recorder.FlushAsync();
        Assert.Contains(await _repository.GetRangeAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), s => s.Timestamp == Minute0.AddDays(-35));

        // Un jour plus tard : purge.
        _clock.Advance(TimeSpan.FromDays(1));
        recorder.Record(Sample(_clock.UtcNow, 10, 10), TemperatureReadings.None);
        await recorder.FlushAsync();
        Assert.DoesNotContain(await _repository.GetRangeAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), s => s.Timestamp < _clock.UtcNow.AddDays(-30));
        recorder.Stop();
    }

    [Fact]
    public async Task History_returns_snapshots_in_window()
    {
        await _repository.AddRangeAsync(
        [
            new PerformanceSnapshot(Minute0.AddMinutes(-90), 1, 1, null, null, null, null, null, null),
            new PerformanceSnapshot(Minute0.AddMinutes(-30), 2, 2, null, null, null, null, null, null),
            new PerformanceSnapshot(Minute0.AddMinutes(-5), 3, 3, null, null, null, null, null, null),
        ]);
        var recorder = Recorder();

        var lastHour = await recorder.GetHistoryAsync(TimeSpan.FromHours(1));
        Assert.Equal(new[] { 2d, 3d }, lastHour.Select(s => s.CpuPercent));
        Assert.Empty(await recorder.GetHistoryAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task Repository_failure_is_not_fatal()
    {
        var recorder = new PerformanceHistoryRecorder(_monitor, new FailingRepository(), _clock, NullLogger<PerformanceHistoryRecorder>.Instance);
        recorder.Start();
        _monitor.Raise(Sample(Minute0, 10, 10));
        _monitor.Raise(Sample(Minute0.AddMinutes(1), 10, 10));
        await recorder.FlushAsync();
        recorder.Stop();
    }

    [Fact]
    public void Minute_truncation_is_utc()
    {
        var local = new DateTimeOffset(2026, 9, 28, 15, 7, 42, TimeSpan.FromHours(3));
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 12, 7, 0, TimeSpan.Zero), PerformanceHistoryRecorder.TruncateToMinute(local));
    }

    private sealed class FailingRepository : Core.Abstractions.Persistence.IPerformanceSnapshotRepository
    {
        public Task AddRangeAsync(IReadOnlyCollection<PerformanceSnapshot> snapshots, CancellationToken cancellationToken = default) => throw new IOException("disque plein");
        public Task<IReadOnlyList<PerformanceSnapshot>> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) => throw new IOException();
        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => throw new IOException();
    }
}
