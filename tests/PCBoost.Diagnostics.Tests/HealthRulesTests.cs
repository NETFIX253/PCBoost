using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Settings;
using PCBoost.Diagnostics.Rules;
using PCBoost.Diagnostics.Tests.TestSupport;

namespace PCBoost.Diagnostics.Tests;

public sealed class HealthRulesTests
{
    private static readonly HealthThresholds Defaults = new();

    private static IHealthRule[] AllRules() =>
    [
        new MemoryUsageRule(), new SystemDriveFreeSpaceRule(), new StartupCountRule(), new SustainedCpuRule(), new DiskActivityRule(),
        new CpuTemperatureRule(), new GpuTemperatureRule(), new StorageTemperatureRule(), new UptimeRule(), new CleanableFilesRule(),
        new BackgroundProcessesRule(), new PowerSaverOnAcRule(), new UnsupportedBuildRule(),
    ];

    private static HealthRulesEngine Engine(params IHealthRule[] rules) => new(rules.Length == 0 ? AllRules() : rules, NullLogger<HealthRulesEngine>.Instance);

    [Fact]
    public void Healthy_pc_has_no_findings()
        => Assert.Empty(Engine().Evaluate(Reports.Healthy(), Defaults));

    [Fact]
    public void Unmeasured_report_produces_no_findings()
        => Assert.Empty(Engine().Evaluate(Reports.Unmeasured(), Defaults));

    [Fact]
    public void Rule_ids_are_unique_and_categories_are_documented()
    {
        var categories = new[] { "memory", "storage", "startup", "cpu", "disk", "thermal", "system", "cleanup", "processes", "power" };
        var rules = AllRules();
        Assert.Equal(rules.Length, rules.Select(r => r.Id).Distinct().Count());
        foreach (var finding in Engine().Evaluate(Reports.Overloaded(), Defaults))
            Assert.Contains(finding.Category, categories);
    }

    [Fact]
    public void Findings_are_sorted_by_severity()
    {
        var findings = Engine().Evaluate(Reports.Overloaded(), Defaults);
        Assert.True(findings.Count >= 10);
        for (var i = 1; i < findings.Count; i++)
            Assert.True(findings[i - 1].Severity >= findings[i].Severity);
        Assert.Equal(Severity.Info, findings[^1].Severity);
    }

    [Fact]
    public void Failing_rule_is_skipped_and_others_still_run()
    {
        var findings = Engine(new ThrowingRule(), new MemoryUsageRule()).Evaluate(Reports.Healthy().WithMemory(95), Defaults);
        Assert.Equal(HealthRuleIds.Memory, Assert.Single(findings).RuleId);
    }

    [Fact]
    public void Custom_rule_registered_in_engine_is_evaluated()
    {
        var findings = Engine(new AlwaysInfoRule()).Evaluate(Reports.Healthy(), Defaults);
        Assert.Equal("custom.rule", Assert.Single(findings).RuleId);
    }

    [Theory]
    [InlineData(79, null)]
    [InlineData(80, Severity.Medium)]
    [InlineData(89.9, Severity.Medium)]
    [InlineData(90, Severity.High)]
    public void Memory_rule_warning_and_critical(double used, Severity? expected)
        => AssertSeverity(new MemoryUsageRule(), Reports.Healthy().WithMemory(used), expected);

    [Fact]
    public void Memory_rule_reports_threshold_category_and_detail()
    {
        var finding = new MemoryUsageRule().Evaluate(Reports.Healthy().WithMemory(92, 8L * ByteSize.GiB), Defaults)!;
        Assert.Equal(92, finding.ObservedValue);
        Assert.Equal(90, finding.Threshold);
        Assert.Equal("memory", finding.Category);
        Assert.Equal("Diag_Rule_Memory_Critical_Title", finding.Title.Key);
        Assert.Equal("Diag_Rule_Memory_Detail", finding.Detail.Key);
        Assert.Equal(8d, (double)finding.Detail.Args[2], 3);
    }

    [Fact]
    public void Memory_rule_uses_custom_thresholds()
    {
        var custom = new HealthThresholds { RamWarningPercent = 50, RamCriticalPercent = 60 };
        Assert.Equal(Severity.Medium, new MemoryUsageRule().Evaluate(Reports.Healthy().WithMemory(55), custom)!.Severity);
        Assert.Equal(Severity.High, new MemoryUsageRule().Evaluate(Reports.Healthy().WithMemory(65), custom)!.Severity);
    }

    [Fact]
    public void Memory_rule_needs_a_measurement()
        => Assert.Null(new MemoryUsageRule().Evaluate(Reports.Healthy() with { Memory = Reports.Unmeasured().Memory }, Defaults));

    [Theory]
    [InlineData(16, null)]
    [InlineData(15, Severity.Medium)]
    [InlineData(11, Severity.Medium)]
    [InlineData(10, Severity.High)]
    [InlineData(2, Severity.High)]
    public void System_drive_free_space_rule(double freePercent, Severity? expected)
        => AssertSeverity(new SystemDriveFreeSpaceRule(), Reports.Healthy().WithSystemDrive(freePercent), expected);

    [Fact]
    public void System_drive_rule_without_drive_produces_nothing()
        => Assert.Null(new SystemDriveFreeSpaceRule().Evaluate(Reports.Healthy().WithoutDrives(), Defaults));

    [Fact]
    public void System_drive_rule_custom_thresholds()
    {
        var custom = new HealthThresholds { SystemDriveFreeWarningPercent = 30, SystemDriveFreeCriticalPercent = 20 };
        Assert.Equal(Severity.Medium, new SystemDriveFreeSpaceRule().Evaluate(Reports.Healthy().WithSystemDrive(25), custom)!.Severity);
    }

    [Theory]
    [InlineData(7, null)]
    [InlineData(8, Severity.Medium)]
    [InlineData(14, Severity.Medium)]
    [InlineData(15, Severity.High)]
    public void Startup_count_rule(int enabled, Severity? expected)
        => AssertSeverity(new StartupCountRule(), Reports.Healthy().WithStartup(enabled, disabled: 3), expected);

    [Fact]
    public void Startup_rule_counts_only_enabled_entries_and_needs_entries()
    {
        Assert.Null(new StartupCountRule().Evaluate(Reports.Healthy().WithStartup(2, disabled: 20), Defaults));
        Assert.Null(new StartupCountRule().Evaluate(Reports.Healthy().WithStartup(0), Defaults));
        var custom = new HealthThresholds { StartupWarningCount = 2, StartupCriticalCount = 4 };
        Assert.Equal(Severity.Medium, new StartupCountRule().Evaluate(Reports.Healthy().WithStartup(3), custom)!.Severity);
    }

    [Theory]
    [InlineData(69, null)]
    [InlineData(70, Severity.Medium)]
    [InlineData(90, Severity.High)]
    public void Sustained_cpu_rule(double average, Severity? expected)
        => AssertSeverity(new SustainedCpuRule(), Reports.Healthy().WithCpu(average), expected);

    [Fact]
    public void Cpu_rule_needs_samples()
        => Assert.Null(new SustainedCpuRule().Evaluate(Reports.Healthy().WithCpu(99).WithNoLoadSamples(), Defaults));

    [Fact]
    public void Cpu_rule_custom_threshold()
        => Assert.Equal(Severity.High, new SustainedCpuRule().Evaluate(Reports.Healthy().WithCpu(50), new HealthThresholds { CpuSustainedWarningPercent = 30, CpuSustainedCriticalPercent = 45 })!.Severity);

    [Theory]
    [InlineData(79, null)]
    [InlineData(80, Severity.Medium)]
    [InlineData(95, Severity.High)]
    public void Disk_activity_rule(double active, Severity? expected)
        => AssertSeverity(new DiskActivityRule(), Reports.Healthy().WithDisk(active), expected);

    [Fact]
    public void Disk_rule_without_counter_produces_nothing()
        => Assert.Null(new DiskActivityRule().Evaluate(Reports.Healthy().WithDisk(null), Defaults));

    [Fact]
    public void Temperature_rules_fire_only_when_measured_and_above_threshold()
    {
        var hot = Reports.Healthy().WithTemperatures(90, 88, 70);
        Assert.Equal(Severity.High, new CpuTemperatureRule().Evaluate(hot, Defaults)!.Severity);
        Assert.Equal(Severity.High, new GpuTemperatureRule().Evaluate(hot, Defaults)!.Severity);
        Assert.Equal(Severity.High, new StorageTemperatureRule().Evaluate(hot, Defaults)!.Severity);
        Assert.Equal("thermal", new CpuTemperatureRule().Evaluate(hot, Defaults)!.Category);

        var cool = Reports.Healthy().WithTemperatures(60, 50, 40);
        Assert.Null(new CpuTemperatureRule().Evaluate(cool, Defaults));
        Assert.Null(new GpuTemperatureRule().Evaluate(cool, Defaults));
        Assert.Null(new StorageTemperatureRule().Evaluate(cool, Defaults));

        var unmeasured = Reports.Healthy().WithTemperatures(null, null, null);
        Assert.Null(new CpuTemperatureRule().Evaluate(unmeasured, Defaults));
        Assert.Null(new GpuTemperatureRule().Evaluate(unmeasured, Defaults));
        Assert.Null(new StorageTemperatureRule().Evaluate(unmeasured, Defaults));

        var custom = new HealthThresholds { CpuTemperatureWarningC = 55 };
        Assert.NotNull(new CpuTemperatureRule().Evaluate(cool, custom));
    }

    [Theory]
    [InlineData(6.9, false)]
    [InlineData(7, true)]
    [InlineData(30, true)]
    public void Uptime_rule(double days, bool expected)
    {
        var finding = new UptimeRule().Evaluate(Reports.Healthy().WithUptime(TimeSpan.FromDays(days)), Defaults);
        Assert.Equal(expected, finding is not null);
        if (finding is not null) Assert.Equal(Severity.Low, finding.Severity);
    }

    [Fact]
    public void Uptime_rule_needs_a_measurement_and_honours_custom_days()
    {
        Assert.Null(new UptimeRule().Evaluate(Reports.Healthy().WithUptime(TimeSpan.Zero), Defaults));
        Assert.NotNull(new UptimeRule().Evaluate(Reports.Healthy().WithUptime(TimeSpan.FromDays(2)), new HealthThresholds { UptimeWarningDays = 1 }));
    }

    [Fact]
    public void Cleanable_rule()
    {
        Assert.Null(new CleanableFilesRule().Evaluate(Reports.Healthy().WithCleanable(ByteSize.GiB), Defaults));
        var finding = new CleanableFilesRule().Evaluate(Reports.Healthy().WithCleanable(3L * ByteSize.GiB), Defaults)!;
        Assert.Equal(Severity.Low, finding.Severity);
        Assert.Equal("cleanup", finding.Category);
        Assert.Equal("Diag_Rule_Cleanable_Detail_Gb", finding.Detail.Key);
        Assert.Null(new CleanableFilesRule().Evaluate(Reports.Healthy().WithCleanable(null), Defaults));

        var custom = new HealthThresholds { CleanableWarningBytes = 100 * ByteSize.MiB };
        Assert.Equal("Diag_Rule_Cleanable_Detail_Mb", new CleanableFilesRule().Evaluate(Reports.Healthy().WithCleanable(300 * ByteSize.MiB), custom)!.Detail.Key);
    }

    [Fact]
    public void Background_processes_rule()
    {
        Assert.Null(new BackgroundProcessesRule().Evaluate(Reports.Healthy().WithBackground(179), Defaults));
        Assert.Equal(Severity.Low, new BackgroundProcessesRule().Evaluate(Reports.Healthy().WithBackground(180), Defaults)!.Severity);
        Assert.Null(new BackgroundProcessesRule().Evaluate(Reports.Healthy().WithBackground(500, running: 0), Defaults));
        Assert.NotNull(new BackgroundProcessesRule().Evaluate(Reports.Healthy().WithBackground(50), new HealthThresholds { BackgroundProcessWarningCount = 40 }));
    }

    [Fact]
    public void Power_saver_on_ac_is_info_only_when_on_ac()
    {
        var finding = new PowerSaverOnAcRule().Evaluate(Reports.Healthy().WithPowerSaverOnAc(), Defaults)!;
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal("power", finding.Category);

        var onBattery = Reports.Healthy().WithPowerSaverOnAc() with { Power = new(Core.Models.SystemInfo.PowerSource.Battery, 40, true) };
        Assert.Null(new PowerSaverOnAcRule().Evaluate(onBattery, Defaults));
        Assert.Null(new PowerSaverOnAcRule().Evaluate(Reports.Healthy(), Defaults));
    }

    [Fact]
    public void Unsupported_build_rule()
    {
        var old = Reports.Healthy() with { Os = Reports.Healthy().Os with { BuildNumber = 17134 } };
        var finding = new UnsupportedBuildRule().Evaluate(old, Defaults)!;
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal("system", finding.Category);
        Assert.Equal(17134, finding.ObservedValue);
        Assert.Equal(17763, finding.Threshold);

        Assert.Null(new UnsupportedBuildRule().Evaluate(Reports.Healthy(), Defaults));
        // Build illisible (0) : jamais signalée comme non prise en charge.
        Assert.Null(new UnsupportedBuildRule().Evaluate(Reports.Unmeasured(), Defaults));
    }

    private static void AssertSeverity(IHealthRule rule, SystemAnalysisReport report, Severity? expected)
    {
        var finding = rule.Evaluate(report, Defaults);
        if (expected is null)
        {
            Assert.Null(finding);
            return;
        }
        Assert.NotNull(finding);
        Assert.Equal(expected, finding.Severity);
        Assert.Equal(rule.Id, finding.RuleId);
        Assert.NotNull(finding.ObservedValue);
        Assert.NotNull(finding.Threshold);
    }

    private sealed class ThrowingRule : IHealthRule
    {
        public string Id => "throwing";
        public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds) => throw new InvalidOperationException("boom");
    }

    private sealed class AlwaysInfoRule : IHealthRule
    {
        public string Id => "custom.rule";
        public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
            => new(Id, Severity.Info, TextRef.Of("Diag_Rule_PowerSaver_Title"), TextRef.Of("Diag_Rule_PowerSaver_Detail"), null, null, "system");
    }
}
