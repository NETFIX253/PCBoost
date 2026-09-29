using PCBoost.Core.Models.Health;

namespace PCBoost.Core.Tests.Health;

public sealed class HealthElevatedDataTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Disk_counters_round_trip_and_missing_values_stay_missing()
    {
        var data = HealthElevatedData.EncodeDisks([new DiskReliability("0", 12, 41, 58, 4200, 3, 0, 1, Now), new DiskReliability("1", null, null, null, null, null, null, null, Now)]);
        var decoded = HealthElevatedData.DecodeDisks(data, Now);

        Assert.Equal(2, decoded.Count);
        Assert.Equal(new DiskReliability("0", 12, 41, 58, 4200, 3, 0, 1, Now), decoded[0]);
        Assert.False(decoded[1].HasAnyValue);
    }

    [Fact]
    public void Out_of_range_disk_values_are_ignored()
    {
        var data = new Dictionary<string, string>
        {
            ["count"] = "999",
            ["0.id"] = "0", ["0.wear"] = "140", ["0.temp"] = "-5", ["0.hours"] = "-1", ["0.runc"] = "abc",
            ["1.id"] = new string('x', 500),
        };
        var decoded = HealthElevatedData.DecodeDisks(data, Now);
        var disk = Assert.Single(decoded);
        Assert.False(disk.HasAnyValue);
    }

    [Fact]
    public void Boot_measurements_round_trip_sorted_newest_first()
    {
        var boots = new[]
        {
            new BootRecord(Now.AddDays(-2), TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(10), 12),
            new BootRecord(Now.AddDays(-1), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(10), null),
        };
        var degradations = new[] { new BootDegradation(Now.AddDays(-1), BootDegradationKind.Service, "Spooler", null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1)) };

        var decoded = HealthElevatedData.DecodeBoot(HealthElevatedData.EncodeBoot(boots, degradations), Now);

        Assert.Equal([boots[1], boots[0]], decoded.Boots);
        Assert.Equal(degradations, decoded.Degradations);
        Assert.Equal(Now, decoded.ReadAt);
    }

    [Fact]
    public void Invalid_boot_entries_are_dropped()
    {
        var data = new Dictionary<string, string>
        {
            ["boots"] = "3",
            ["b0.time"] = "pas une date", ["b0.total"] = "1000",
            ["b1.time"] = Now.ToString("O"), ["b1.total"] = "0",
            ["b2.time"] = Now.ToString("O"), ["b2.total"] = "99999999999",
            ["degs"] = "2",
            ["d0.time"] = Now.ToString("O"), ["d0.name"] = "x", ["d0.deg"] = "0",
            ["d1.time"] = Now.ToString("O"), ["d1.name"] = "y", ["d1.deg"] = "10", ["d1.kind"] = "77",
        };
        var decoded = HealthElevatedData.DecodeBoot(data, Now);
        Assert.Empty(decoded.Boots);
        Assert.Equal(BootDegradationKind.Other, Assert.Single(decoded.Degradations).Kind);
    }

    [Theory]
    [InlineData(RestorePointStatus.Created)]
    [InlineData(RestorePointStatus.RecentExists)]
    [InlineData(RestorePointStatus.Disabled)]
    public void Restore_point_status_round_trips(RestorePointStatus status)
    {
        var (decoded, time) = HealthElevatedData.DecodeRestorePoint(HealthElevatedData.EncodeRestorePoint(status, Now));
        Assert.Equal(status, decoded);
        Assert.Equal(Now, time);
    }

    [Fact]
    public void Unknown_restore_point_status_is_a_failure()
    {
        Assert.Equal(RestorePointStatus.Failed, HealthElevatedData.DecodeRestorePoint(new Dictionary<string, string> { ["status"] = "42" }).Status);
        Assert.Equal(RestorePointStatus.Failed, HealthElevatedData.DecodeRestorePoint(new Dictionary<string, string>()).Status);
    }

    [Fact]
    public void Last_run_keeps_only_executable_names_and_latest_time()
    {
        var data = new Dictionary<string, string>
        {
            ["count"] = "4",
            ["0.exe"] = "VLC.EXE", ["0.time"] = Now.AddDays(-3).ToString("O"),
            ["1.exe"] = "vlc.exe", ["1.time"] = Now.AddDays(-1).ToString("O"),
            ["2.exe"] = @"C:\evil\x.exe", ["2.time"] = Now.ToString("O"),
            ["3.exe"] = "notes.txt", ["3.time"] = Now.ToString("O"),
        };
        var decoded = HealthElevatedData.DecodeLastRun(data);
        Assert.Equal(Now.AddDays(-1), Assert.Single(decoded).Value);
        Assert.True(decoded.ContainsKey("vlc.exe"));
    }

    [Fact]
    public void Last_run_round_trips()
    {
        var map = new Dictionary<string, DateTimeOffset> { ["app.exe"] = Now, ["game.exe"] = Now.AddDays(-40) };
        Assert.Equal(map, HealthElevatedData.DecodeLastRun(HealthElevatedData.EncodeLastRun(map)));
    }
}

public sealed class HealthModelTests
{
    [Theory]
    [InlineData(DiskHealthStatus.Unhealthy, null, null, true, false)]
    [InlineData(DiskHealthStatus.Healthy, 90, null, true, false)]
    [InlineData(DiskHealthStatus.Healthy, null, 1L, true, false)]
    [InlineData(DiskHealthStatus.Warning, null, null, false, true)]
    [InlineData(DiskHealthStatus.Healthy, 70, null, false, true)]
    [InlineData(DiskHealthStatus.Healthy, 69, 0L, false, false)]
    [InlineData(DiskHealthStatus.Unknown, null, null, false, false)]
    public void Disk_classification(DiskHealthStatus status, int? wear, long? uncorrected, bool critical, bool warning)
    {
        var disk = new DiskHealthInfo("0", "SSD", PCBoost.Core.Models.SystemInfo.StorageMediaType.Ssd, PCBoost.Core.Models.SystemInfo.StorageBusType.Nvme, 512_000_000_000, status, true,
            new DiskReliability("0", wear, null, null, null, null, uncorrected, null, DateTimeOffset.UnixEpoch));
        Assert.Equal(critical, disk.IsCritical);
        Assert.Equal(warning, disk.IsWarning);
    }

    [Theory]
    [InlineData(50_000L, 40_000L, 80.0)]
    [InlineData(50_000L, 55_000L, 100.0)]
    [InlineData(null, 40_000L, null)]
    [InlineData(50_000L, 0L, null)]
    public void Battery_health_is_full_charge_over_design(long? design, long? full, double? expected)
        => Assert.Equal(expected, new BatteryInfo("B", null, null, design, full, null).HealthPercent);

    [Theory]
    [InlineData(0, false)]
    [InlineData(22, false)]
    [InlineData(45, false)]
    [InlineData(28, true)]
    [InlineData(10, true)]
    public void Disabled_or_disconnected_devices_are_not_reported(int code, bool reportable)
        => Assert.Equal(reportable, DeviceProblem.IsReportable(code));

    [Fact]
    public void Mostly_fast_startup_needs_a_strict_majority()
    {
        var t = DateTimeOffset.UnixEpoch;
        var two = new BootTimeReport([new(t, BootKind.FastStartup, null), new(t, BootKind.Cold, null)], null, null, null);
        var three = new BootTimeReport([new(t, BootKind.FastStartup, null), new(t, BootKind.FastStartup, null), new(t, BootKind.Cold, null)], null, null, null);
        Assert.False(two.MostlyFastStartup);
        Assert.True(three.MostlyFastStartup);
        Assert.Null(two.LatestBoot);
    }
}
