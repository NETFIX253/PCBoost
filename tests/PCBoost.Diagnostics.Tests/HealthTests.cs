using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Settings;
using PCBoost.Diagnostics.Health;
using PCBoost.Diagnostics.Rules;
using PCBoost.Diagnostics.SlowPc;
using PCBoost.Diagnostics.Tests.TestSupport;
using PCBoost.TestUtilities;

namespace PCBoost.Diagnostics.Tests;

public sealed class HardwareHealthRuleTests
{
    private static readonly HealthThresholds Thresholds = new();

    [Theory]
    [InlineData(DiskHealthStatus.Unhealthy, null, null, Severity.Critical)]
    [InlineData(DiskHealthStatus.Healthy, null, 2L, Severity.Critical)]
    [InlineData(DiskHealthStatus.Healthy, 92, null, Severity.Critical)]
    [InlineData(DiskHealthStatus.Warning, null, null, Severity.Medium)]
    [InlineData(DiskHealthStatus.Healthy, 72, null, Severity.Medium)]
    public void Disk_health_rule_reports_failing_or_worn_disks(DiskHealthStatus status, int? wear, long? uncorrected, Severity expected)
    {
        var report = Reports.Healthy().WithHealth(HealthScenarios.Disk(status, wear, uncorrected));
        var finding = new DiskHealthRule().Evaluate(report, Thresholds);
        Assert.NotNull(finding);
        Assert.Equal(expected, finding!.Severity);
        Assert.Equal(HealthCategories.Hardware, finding.Category);
    }

    [Fact]
    public void Healthy_disk_or_missing_data_produces_no_finding()
    {
        Assert.Null(new DiskHealthRule().Evaluate(Reports.Healthy().WithHealth(HealthScenarios.Disk(DiskHealthStatus.Healthy, wear: 10)), Thresholds));
        Assert.Null(new DiskHealthRule().Evaluate(Reports.Healthy(), Thresholds));
        Assert.Null(new DiskHealthRule().Evaluate(Reports.Healthy().WithHealth(HealthScenarios.Disk(DiskHealthStatus.Unknown)), Thresholds));
    }

    [Fact]
    public void Worst_disk_is_reported_first_and_system_disk_preferred()
    {
        var report = Reports.Healthy() with
        {
            Health = Reports.Healthy().WithHealth(HealthScenarios.Disk(DiskHealthStatus.Warning, system: true, id: "0")).Health! with
            {
                Disks = [HealthScenarios.Disk(DiskHealthStatus.Warning, system: true, id: "0"), HealthScenarios.Disk(DiskHealthStatus.Unhealthy, system: false, id: "1")],
            },
        };
        Assert.Equal(Severity.Critical, new DiskHealthRule().Evaluate(report, Thresholds)!.Severity);
    }

    [Theory]
    [InlineData(55, Severity.Medium)]
    [InlineData(70, Severity.Low)]
    public void Battery_rule_uses_capacity_ratio(double health, Severity expected)
    {
        var finding = new BatteryWearRule().Evaluate(Reports.Healthy().WithHealth(battery: health), Thresholds);
        Assert.Equal(expected, finding!.Severity);
        Assert.Equal(health, finding.ObservedValue!.Value, 0);
    }

    [Fact]
    public void Healthy_or_unmeasured_battery_produces_no_finding()
    {
        Assert.Null(new BatteryWearRule().Evaluate(Reports.Healthy().WithHealth(battery: 92), Thresholds));
        var unknown = Reports.Healthy().WithHealth() with { };
        unknown = unknown with { Health = unknown.Health! with { Batteries = [new BatteryInfo("B", null, null, null, 30_000, null)] } };
        Assert.Null(new BatteryWearRule().Evaluate(unknown, Thresholds));
    }

    [Fact]
    public void Device_rule_names_at_most_three_devices()
    {
        var finding = new DeviceProblemRule().Evaluate(Reports.Healthy().WithHealth(devices: 5), Thresholds)!;
        Assert.Equal(5, finding.ObservedValue);
        var names = (string)finding.Detail.Args[1];
        Assert.Equal(3, names.Split(',').Length);
        Assert.EndsWith("…", names);
    }

    [Fact]
    public void Thermal_rule_prefers_observed_episode_over_firmware_events()
    {
        var withEpisode = new ThermalLimitRule().Evaluate(Reports.Healthy().WithHealth(episode: true, firmwareEvents: 3), Thresholds)!;
        Assert.Equal(Severity.Medium, withEpisode.Severity);
        var firmwareOnly = new ThermalLimitRule().Evaluate(Reports.Healthy().WithHealth(firmwareEvents: 3), Thresholds)!;
        Assert.Equal(Severity.Low, firmwareOnly.Severity);
        Assert.Null(new ThermalLimitRule().Evaluate(Reports.Healthy().WithHealth(), Thresholds));
    }

    [Fact]
    public void Slow_pc_diagnosis_includes_failing_disk_and_throttling()
    {
        var report = Reports.Healthy().WithHealth(HealthScenarios.Disk(DiskHealthStatus.Unhealthy), episode: true);
        var factors = SlowPcDiagnosticService.Evaluate(report, Thresholds);
        Assert.Contains(factors, f => f.Id == SlownessFactorIds.DiskHealth && f.Impact == ImpactLevel.High && f.Action!.NavigationTarget == "health");
        Assert.Contains(factors, f => f.Id == SlownessFactorIds.CpuThrottling);
    }
}

public sealed class ThrottlingEpisodeTrackerTests
{
    private static readonly DateTimeOffset T0 = Reports.Now;

    private static ThrottlingSample S(int second, double cpu, double? perf, bool ac = true, bool saver = false, double? temp = null)
        => new(T0.AddSeconds(second), cpu, perf, ac, saver, temp);

    [Fact]
    public void Sustained_high_load_with_low_performance_is_an_episode()
    {
        var tracker = new ThrottlingEpisodeTracker();
        ThrottlingEpisode? episode = null;
        for (var s = 0; s <= 30; s += 2) episode ??= tracker.Add(S(s, 95, 45, temp: 96));
        episode ??= tracker.Add(S(32, 20, 110));
        episode ??= tracker.Add(S(34, 20, 110));

        Assert.NotNull(episode);
        Assert.Equal(TimeSpan.FromSeconds(30), episode!.Duration);
        Assert.Equal(95, episode.AverageCpuPercent, 1);
        Assert.Equal(45, episode.AverageProcessorPerformancePercent, 1);
        Assert.Equal(96, episode.MaxCpuTemperatureC);
    }

    [Fact]
    public void Short_spikes_battery_power_saver_and_turbo_are_ignored()
    {
        var tracker = new ThrottlingEpisodeTracker();
        for (var s = 0; s <= 10; s += 2) Assert.Null(tracker.Add(S(s, 95, 45)));
        Assert.Null(tracker.Add(S(12, 10, 100)));
        Assert.Null(tracker.Add(S(14, 10, 100))); // 10 s seulement : trop court.

        Assert.False(ThrottlingEpisodeTracker.IsThrottled(S(0, 95, 45, ac: false)));
        Assert.False(ThrottlingEpisodeTracker.IsThrottled(S(0, 95, 45, saver: true)));
        Assert.False(ThrottlingEpisodeTracker.IsThrottled(S(0, 95, 120)));
        Assert.False(ThrottlingEpisodeTracker.IsThrottled(S(0, 95, null)));
        Assert.False(ThrottlingEpisodeTracker.IsThrottled(S(0, 50, 45)));
    }

    [Fact]
    public void Long_gap_between_samples_interrupts_the_episode()
    {
        var tracker = new ThrottlingEpisodeTracker();
        for (var s = 0; s <= 24; s += 2) tracker.Add(S(s, 95, 45));
        var episode = tracker.Add(S(120, 95, 45)); // veille ou surveillance suspendue
        Assert.NotNull(episode);
        Assert.Equal(TimeSpan.FromSeconds(24), episode!.Duration);
    }

    [Fact]
    public async Task Detector_notifies_at_most_once_a_day()
    {
        var notifications = new FakeNotificationService();
        var clock = new FakeClock(T0);
        var detector = new ThermalThrottlingDetector(new FakePerformanceMonitor(), new FakeSystemInfoProvider(), new FakePowerProvider(), new InMemoryKeyValueStore(), notifications, clock);
        var episode = new ThrottlingEpisode(T0, TimeSpan.FromSeconds(30), 90, 50, null);

        await detector.RecordAsync(episode);
        await detector.RecordAsync(episode);
        Assert.Single(notifications.Shown);
        Assert.Equal(2, detector.EpisodeCount);

        clock.Advance(TimeSpan.FromDays(1.1));
        await detector.RecordAsync(episode);
        Assert.Equal(2, notifications.Shown.Count);
        Assert.Equal("navigate:health", notifications.Shown[0].ActionId);
    }

    [Fact]
    public void Detector_feeds_monitor_samples_with_power_conditions()
    {
        var monitor = new FakePerformanceMonitor();
        var system = new FakeSystemInfoProvider();
        var detector = new ThermalThrottlingDetector(monitor, system, new FakePowerProvider(), new InMemoryKeyValueStore(), new FakeNotificationService(), new FakeClock(T0));
        detector.Start();
        for (var s = 0; s <= 30; s += 2) monitor.Raise(Sample(s, 95, 45));
        monitor.Raise(Sample(32, 5, 100));
        monitor.Raise(Sample(34, 5, 100));
        detector.Stop();
        Assert.Equal(1, detector.EpisodeCount);
        Assert.Equal(0, monitor.SubscriberCount);
    }

    private static SystemMetricsSample Sample(int second, double cpu, double perf)
        => new(T0.AddSeconds(second), cpu, 50, 0, 0, null, null, null, null, null, null, null, 100, perf);
}

public sealed class HardwareHealthServiceTests
{
    [Fact]
    public async Task Refresh_merges_saved_reliability_counters_by_disk()
    {
        var provider = new FakeHardwareHealthProvider
        {
            Disks = OperationResult<IReadOnlyList<DiskHealthInfo>>.Ok([HealthScenarios.Disk(DiskHealthStatus.Healthy) with { Reliability = null }]),
        };
        var store = new InMemoryKeyValueStore();
        await store.SetAsync(HardwareHealthService.ReliabilityKey, new List<DiskReliability> { new("0", 12, 38, 50, 900, 0, 0, 0, Reports.Now.AddDays(-2)) });
        var service = new HardwareHealthService(provider, new FakeElevationService(), store, new FakeNotificationService(), new FakeClock(Reports.Now));

        var report = await service.RefreshAsync();

        Assert.Equal(12, report.Disks.Single().Reliability!.WearPercent);
        Assert.Equal(Reports.Now.AddDays(-2), report.ReliabilityMeasuredAt);
        Assert.Equal(Availability.Available, report.DiskAvailability);
        Assert.Same(report, service.Latest);
    }

    [Fact]
    public async Task Unreadable_sources_are_reported_as_unavailable_not_empty()
    {
        var provider = new FakeHardwareHealthProvider
        {
            Disks = OperationResult<IReadOnlyList<DiskHealthInfo>>.Fail(OperationErrorKind.NotSupported),
            Batteries = OperationResult<IReadOnlyList<BatteryInfo>>.Fail(OperationErrorKind.AccessDenied),
        };
        var service = new HardwareHealthService(provider, new FakeElevationService(), new InMemoryKeyValueStore(), new FakeNotificationService(), new FakeClock(Reports.Now));
        var report = await service.RefreshAsync();
        Assert.Equal(Availability.NotSupported, report.DiskAvailability);
        Assert.Equal(Availability.RequiresElevation, report.BatteryAvailability);
        Assert.Empty(report.Disks);
    }

    [Fact]
    public async Task Reading_reliability_uses_one_elevated_request_and_stores_the_result()
    {
        var elevation = new FakeElevationService
        {
            Handler = _ => new ElevatedResponse(OperationResult.Ok(), HealthElevatedData.EncodeDisks([new DiskReliability("0", 30, 41, 60, 5000, 1, 0, 2, Reports.Now)])),
        };
        var store = new InMemoryKeyValueStore();
        var provider = new FakeHardwareHealthProvider { Disks = OperationResult<IReadOnlyList<DiskHealthInfo>>.Ok([HealthScenarios.Disk(DiskHealthStatus.Healthy)]) };
        var service = new HardwareHealthService(provider, elevation, store, new FakeNotificationService(), new FakeClock(Reports.Now));

        var result = await service.ReadDiskReliabilityAsync();

        Assert.True(result.Success);
        Assert.Null(result.Message);
        Assert.Equal(ElevatedHealthOperations.DiskReliability, elevation.Requests.Single().Operation);
        Assert.Empty(elevation.Requests.Single().Parameters);
        Assert.Equal(30, service.Latest!.Disks.Single().Reliability!.WearPercent);
    }

    [Fact]
    public async Task Cancelled_elevation_changes_nothing()
    {
        var store = new InMemoryKeyValueStore();
        var service = new HardwareHealthService(new FakeHardwareHealthProvider(), new FakeElevationService { UserCancels = true }, store, new FakeNotificationService(), new FakeClock(Reports.Now));
        var result = await service.ReadDiskReliabilityAsync();
        Assert.Equal(OperationErrorKind.ElevationCancelled, result.Error);
        Assert.Null(await store.GetAsync<List<DiskReliability>>(HardwareHealthService.ReliabilityKey));
    }

    [Fact]
    public async Task Critical_disk_notification_is_sent_once_a_day_per_disk()
    {
        var notifications = new FakeNotificationService();
        var clock = new FakeClock(Reports.Now);
        var service = new HardwareHealthService(new FakeHardwareHealthProvider(), new FakeElevationService(), new InMemoryKeyValueStore(), notifications, clock);
        var report = Reports.Healthy().WithHealth(HealthScenarios.Disk(DiskHealthStatus.Unhealthy)).Health!;

        await service.NotifyCriticalDisksAsync(report);
        await service.NotifyCriticalDisksAsync(report);
        Assert.Single(notifications.Shown);

        clock.Advance(TimeSpan.FromHours(25));
        await service.NotifyCriticalDisksAsync(report);
        Assert.Equal(2, notifications.Shown.Count);

        await service.NotifyCriticalDisksAsync(Reports.Healthy().WithHealth(HealthScenarios.Disk(DiskHealthStatus.Healthy)).Health!);
        Assert.Equal(2, notifications.Shown.Count);
    }
}

public sealed class BootTimeServiceTests
{
    private static BootRecord Boot(int daysAgo, double seconds) => new(Reports.Now.AddDays(-daysAgo), TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds * 0.8), TimeSpan.FromSeconds(seconds * 0.2), 10);

    [Fact]
    public void Comparison_averages_up_to_five_measured_boots_on_each_side()
    {
        var boots = new[] { Boot(1, 30), Boot(2, 34), Boot(10, 60), Boot(11, 58), Boot(12, 62), Boot(13, 70), Boot(14, 50), Boot(15, 99) };
        var comparison = BootTimeService.Compare(boots, Reports.Now.AddDays(-5))!;
        Assert.Equal(5, comparison.BootsBefore);
        Assert.Equal(2, comparison.BootsAfter);
        Assert.Equal(60, comparison.AverageBefore.TotalSeconds, 1);
        Assert.Equal(32, comparison.AverageAfter.TotalSeconds, 1);
        Assert.True(comparison.Difference < TimeSpan.Zero);
    }

    [Fact]
    public void No_comparison_without_boots_on_both_sides()
    {
        Assert.Null(BootTimeService.Compare([Boot(10, 60)], Reports.Now.AddDays(-5)));
        Assert.Null(BootTimeService.Compare([Boot(1, 30)], Reports.Now.AddDays(-5)));
    }

    [Fact]
    public void Merge_keeps_older_measurements_without_duplicates()
    {
        var previous = new BootPerformanceData(Reports.Now.AddDays(-3), [Boot(4, 50), Boot(20, 70)], []);
        var fresh = new BootPerformanceData(Reports.Now, [Boot(1, 40), Boot(4, 50)], []);
        var merged = BootTimeService.Merge(previous, fresh);
        Assert.Equal(3, merged.Boots.Count);
        Assert.Equal(Reports.Now, merged.ReadAt);
        Assert.True(merged.Boots[0].Timestamp > merged.Boots[1].Timestamp);
    }

    [Fact]
    public async Task Report_uses_last_startup_change_from_history()
    {
        var history = new InMemoryOptimizationHistoryRepository();
        var changedAt = Reports.Now.AddDays(-5);
        var sessionId = Guid.NewGuid();
        await history.CreateSessionAsync(new OptimizationSession { Id = sessionId, Type = SessionType.Startup, StartedAt = changedAt, Status = SessionStatus.Completed });
        await history.AddChangeAsync(new ChangeRecord
        {
            Id = Guid.NewGuid(), SessionId = sessionId, OptimizationId = Core.Optimization.StartupChangeSources.StartupManager, Kind = "registry.value",
            Target = "HKCU : app", Description = TextRef.Literal("x"), Reversible = true, Status = ChangeStatus.Applied, RecordedAt = changedAt,
        });
        var store = new InMemoryKeyValueStore();
        await store.SetAsync(BootTimeService.MeasurementsKey, new BootPerformanceData(Reports.Now, [Boot(1, 30), Boot(10, 60)], []));
        var provider = new FakeHardwareHealthProvider();
        provider.Boots.Add(new BootSession(Reports.Now.AddHours(-3), BootKind.FastStartup, Reports.Now.AddHours(-3).AddSeconds(40)));

        var report = await new BootTimeService(provider, new FakeElevationService(), store, history, new FakeClock(Reports.Now)).GetReportAsync();

        Assert.Equal(changedAt, report.LastStartupChangeAt);
        Assert.NotNull(report.Comparison);
        Assert.Equal(30, report.LatestBoot!.BootTime.TotalSeconds);
        Assert.True(report.MostlyFastStartup);
    }

    [Fact]
    public async Task Reading_measurements_stores_decoded_boots()
    {
        var elevation = new FakeElevationService
        {
            Handler = _ => new ElevatedResponse(OperationResult.Ok(), HealthElevatedData.EncodeBoot([Boot(1, 42)],
                [new BootDegradation(Reports.Now.AddDays(-1), BootDegradationKind.Application, "OneDrive", "OneDrive.exe", TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(4))])),
        };
        var store = new InMemoryKeyValueStore();
        var service = new BootTimeService(new FakeHardwareHealthProvider(), elevation, store, new InMemoryOptimizationHistoryRepository(), new FakeClock(Reports.Now));

        var result = await service.ReadMeasurementsAsync();

        Assert.True(result.Success);
        var saved = await store.GetAsync<BootPerformanceData>(BootTimeService.MeasurementsKey);
        Assert.Equal(42, saved!.Boots.Single().BootTime.TotalSeconds, 1);
        Assert.Equal("OneDrive", saved.Degradations.Single().Name);
    }
}
