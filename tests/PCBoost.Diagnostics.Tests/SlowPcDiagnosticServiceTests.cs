using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Settings;
using PCBoost.Diagnostics.SlowPc;
using PCBoost.Diagnostics.Tests.TestSupport;
using PCBoost.TestUtilities;

namespace PCBoost.Diagnostics.Tests;

public sealed class SlowPcDiagnosticServiceTests
{
    private readonly FakeClock _clock = new(Reports.Now);
    private readonly FakeSystemAnalyzer _analyzer = new();
    private readonly FakeSettingsService _settings = new();

    private SlowPcDiagnosticService Service() => new(_analyzer, _settings, _clock);

    [Fact]
    public async Task Healthy_pc_has_no_significant_factor()
    {
        _analyzer.NextReport = Reports.Healthy();
        var diagnosis = await Service().DiagnoseAsync();
        Assert.True(diagnosis.NoSignificantFactor);
        Assert.Empty(diagnosis.Factors);
        Assert.Equal(_clock.UtcNow, diagnosis.Timestamp);
    }

    [Fact]
    public async Task Fresh_last_report_is_reused()
    {
        _analyzer.LastReport = Reports.Healthy().WithMemory(95) with { Timestamp = _clock.UtcNow.AddSeconds(-90) };
        var diagnosis = await Service().DiagnoseAsync();
        Assert.Empty(_analyzer.Calls);
        Assert.Contains(diagnosis.Factors, f => f.Id == SlownessFactorIds.Memory);
    }

    [Fact]
    public async Task Stale_last_report_triggers_a_new_analysis_with_5_second_sampling()
    {
        _analyzer.LastReport = Reports.Healthy().WithMemory(95) with { Timestamp = _clock.UtcNow.AddMinutes(-3) };
        _analyzer.NextReport = Reports.Healthy();
        var diagnosis = await Service().DiagnoseAsync();

        var options = Assert.Single(_analyzer.Calls);
        Assert.Equal(TimeSpan.FromSeconds(5), options.LoadSamplingDuration);
        Assert.True(options.IncludeCleanupScan);
        Assert.True(diagnosis.NoSignificantFactor);
    }

    [Fact]
    public async Task No_previous_report_triggers_analysis()
    {
        await Service().DiagnoseAsync();
        Assert.Single(_analyzer.Calls);
    }

    [Fact]
    public async Task Factors_are_sorted_by_impact_then_confidence()
    {
        _analyzer.NextReport = Reports.Overloaded();
        var diagnosis = await Service().DiagnoseAsync();

        Assert.False(diagnosis.NoSignificantFactor);
        for (var i = 1; i < diagnosis.Factors.Count; i++)
        {
            var previous = diagnosis.Factors[i - 1];
            var current = diagnosis.Factors[i];
            Assert.True(previous.Impact > current.Impact || (previous.Impact == current.Impact && previous.Confidence >= current.Confidence),
                $"{previous.Id} avant {current.Id}");
        }
        Assert.Equal(ImpactLevel.Low, diagnosis.Factors[^1].Impact);
        var ids = diagnosis.Factors.Select(f => f.Id).ToHashSet();
        foreach (var expected in new[]
                 {
                     SlownessFactorIds.Memory, SlownessFactorIds.Startup, SlownessFactorIds.DiskBusy, SlownessFactorIds.SystemDriveHdd,
                     SlownessFactorIds.Cpu, SlownessFactorIds.LowDiskSpace, SlownessFactorIds.CpuTemperature, SlownessFactorIds.Uptime,
                     SlownessFactorIds.PowerSaver, SlownessFactorIds.BackgroundProcesses,
                 })
        {
            Assert.Contains(expected, ids);
        }
    }

    [Fact]
    public void Memory_factor_names_three_biggest_processes_with_evidence()
    {
        var report = Reports.Healthy().WithMemory(91, 8L * ByteSize.GiB).WithTopProcesses();
        var factor = Assert.Single(SlowPcDiagnosticService.Evaluate(report, new HealthThresholds()));

        Assert.Equal(SlownessFactorIds.Memory, factor.Id);
        Assert.Equal(ImpactLevel.High, factor.Impact);
        Assert.Equal("Diag_Slow_Memory_Evidence", factor.Evidence.Key);
        Assert.Equal(91d, (double)factor.Evidence.Args[0]);
        Assert.Equal("Diag_Slow_Memory_WhatToDo_Processes", factor.WhatToDo.Key);
        Assert.Equal("chrome.exe, Teams.exe, OneDrive.exe", factor.WhatToDo.Args[0]);
        Assert.Equal("processes", factor.Action!.NavigationTarget);
    }

    [Fact]
    public void Cpu_factor_names_main_process()
    {
        var report = Reports.Healthy().WithCpu(75, 98).WithTopProcesses();
        var factor = Assert.Single(SlowPcDiagnosticService.Evaluate(report, new HealthThresholds()));
        Assert.Equal(SlownessFactorIds.Cpu, factor.Id);
        Assert.Equal(ImpactLevel.Medium, factor.Impact);
        Assert.Equal("MsMpEng.exe", factor.WhatToDo.Args[0]);
        Assert.Equal(98d, (double)factor.Evidence.Args[1]);
    }

    [Fact]
    public void Hdd_system_drive_is_a_factor_even_when_idle()
    {
        var factor = Assert.Single(SlowPcDiagnosticService.Evaluate(Reports.Healthy().WithSystemDrive(50, media: StorageMediaType.Hdd), new HealthThresholds()));
        Assert.Equal(SlownessFactorIds.SystemDriveHdd, factor.Id);
        Assert.Equal("oldpc", factor.Action!.NavigationTarget);
    }

    [Fact]
    public void Low_space_points_to_cleanup_when_files_are_recoverable()
    {
        var withCleanable = SlowPcDiagnosticService.Evaluate(Reports.Healthy().WithSystemDrive(8).WithCleanable(3L * ByteSize.GiB), new HealthThresholds());
        var factor = withCleanable.Single(f => f.Id == SlownessFactorIds.LowDiskSpace);
        Assert.Equal("cleanup", factor.Action!.NavigationTarget);
        Assert.Equal("Diag_Slow_LowSpace_WhatToDo_Cleanable_Gb", factor.WhatToDo.Key);
        Assert.Equal(ImpactLevel.High, factor.Impact);

        var withoutCleanable = SlowPcDiagnosticService.Evaluate(Reports.Healthy().WithSystemDrive(12).WithCleanable(null), new HealthThresholds());
        var other = withoutCleanable.Single(f => f.Id == SlownessFactorIds.LowDiskSpace);
        Assert.Equal("storage", other.Action!.NavigationTarget);
        Assert.Equal(ImpactLevel.Medium, other.Impact);
    }

    [Fact]
    public void Unmeasured_values_never_produce_factors()
    {
        var factors = SlowPcDiagnosticService.Evaluate(Reports.Unmeasured(), new HealthThresholds());
        Assert.Empty(factors);
    }

    [Fact]
    public void Thresholds_come_from_settings()
    {
        var report = Reports.Healthy().WithMemory(50);
        Assert.Empty(SlowPcDiagnosticService.Evaluate(report, new HealthThresholds()));
        Assert.Single(SlowPcDiagnosticService.Evaluate(report, new HealthThresholds { RamWarningPercent = 45, RamCriticalPercent = 70 }));
    }

    [Fact]
    public async Task Settings_thresholds_are_used_by_the_service()
    {
        var settings = new AppSettings { Thresholds = new HealthThresholds { RamWarningPercent = 30, RamCriticalPercent = 40 } };
        await _settings.SaveAsync(settings);
        _analyzer.NextReport = Reports.Healthy(); // 37,5 % de mémoire
        var diagnosis = await Service().DiagnoseAsync();
        Assert.Equal(SlownessFactorIds.Memory, Assert.Single(diagnosis.Factors).Id);
    }
}
