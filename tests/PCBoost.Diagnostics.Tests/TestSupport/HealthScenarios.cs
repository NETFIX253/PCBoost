using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Diagnostics.Health;
using PCBoost.TestUtilities;

namespace PCBoost.Diagnostics.Tests.TestSupport;

/// <summary>Scénarios de santé matérielle partagés par les tests de règles et de ressources.</summary>
internal static class HealthScenarios
{
    public static DiskHealthInfo Disk(DiskHealthStatus status, int? wear = null, long? uncorrected = null, bool system = true, string id = "0")
        => new(id, "Disque test", StorageMediaType.Ssd, StorageBusType.Nvme, 512L * ByteSize.GiB, status, system,
            wear is null && uncorrected is null ? null : new DiskReliability(id, wear, 40, 55, 1200, 0, uncorrected, 0, Reports.Now));

    public static BatteryInfo Battery(double healthPercent)
        => new("Batterie", "Fabricant", "LION", 50_000, (long)(50_000 * healthPercent / 100), 300);

    public static SystemAnalysisReport WithHealth(this SystemAnalysisReport report, DiskHealthInfo? disk = null, double? battery = null, int devices = 0,
        bool episode = false, int firmwareEvents = 0)
        => report with
        {
            Health = new HardwareHealthReport(Reports.Now,
                disk is null ? [] : [disk], Availability.Available, disk?.Reliability?.MeasuredAt,
                battery is { } b ? [Battery(b)] : [], Availability.Available,
                Enumerable.Range(1, devices).Select(i => new DeviceProblem($"Périphérique {i}", "USB", 10, null)).ToList(), Availability.Available,
                new ThermalLimitInfo(firmwareEvents, firmwareEvents > 0 ? Reports.Now.AddDays(-1) : null, episode ? 1 : 0,
                    episode ? new ThrottlingEpisode(Reports.Now.AddHours(-1), TimeSpan.FromSeconds(45), 92, 48, 97) : null)),
        };

    /// <summary>Textes produits par les services (notifications, messages de résultat) sur des cas réels.</summary>
    public static IEnumerable<TextRef> ServiceTexts()
    {
        var notifications = new FakeNotificationService();
        var store = new InMemoryKeyValueStore();
        var clock = new FakeClock(Reports.Now);
        var elevation = new FakeElevationService();
        var provider = new FakeHardwareHealthProvider();
        var health = new HardwareHealthService(provider, elevation, store, notifications, clock);

        var critical = Reports.Healthy().WithHealth(Disk(DiskHealthStatus.Unhealthy)).Health!;
        health.NotifyCriticalDisksAsync(critical).GetAwaiter().GetResult();
        var reliability = health.ReadDiskReliabilityAsync().GetAwaiter().GetResult();
        if (reliability.Message is { } m1) yield return m1;

        var detector = new ThermalThrottlingDetector(new FakePerformanceMonitor(), new FakeSystemInfoProvider(), new FakePowerProvider(), store, notifications, clock);
        detector.RecordAsync(new ThrottlingEpisode(Reports.Now, TimeSpan.FromSeconds(30), 90, 50, null)).GetAwaiter().GetResult();

        foreach (var n in notifications.Shown)
        {
            yield return n.Title;
            yield return n.Body;
            if (n.ActionLabel is { } label) yield return label;
        }

        var boot = new BootTimeService(provider, elevation, store, new InMemoryOptimizationHistoryRepository(), clock);
        var measured = boot.ReadMeasurementsAsync().GetAwaiter().GetResult();
        if (measured.Message is { } m2) yield return m2;
    }
}
