using PCBoost.Core.Common;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Diagnostics.Hardware;
using PCBoost.Diagnostics.Tests.TestSupport;

namespace PCBoost.Diagnostics.Tests;

public sealed class HardwareAdvisorTests
{
    private readonly HardwareAdvisor _advisor = new();

    private static List<PerformanceSnapshot> History(int count, double memoryPercent, double cpuPercent = 20)
        => Enumerable.Range(0, count)
            .Select(i => new PerformanceSnapshot(Reports.Now.AddMinutes(-i), cpuPercent, memoryPercent, 5, null, null, null, null, null))
            .ToList();

    [Fact]
    public void No_ram_advice_without_history_even_on_a_4GB_pc()
    {
        var report = Reports.Healthy().WithMemory(95, 4L * ByteSize.GiB);
        Assert.DoesNotContain(_advisor.GetAdvice(report, []), a => a.Id == HardwareAdviceIds.Memory);
        Assert.DoesNotContain(_advisor.GetAdvice(report, History(HardwareAdvisor.MinimumHistorySamples - 1, 95)), a => a.Id == HardwareAdviceIds.Memory);
    }

    [Theory]
    [InlineData(20, ConfidenceLevel.Low)]
    [InlineData(60, ConfidenceLevel.Medium)]
    [InlineData(300, ConfidenceLevel.High)]
    public void Ram_advice_confidence_depends_on_amount_of_data(int samples, ConfidenceLevel expected)
    {
        var report = Reports.Healthy().WithMemory(60, 8L * ByteSize.GiB);
        var advice = Assert.Single(_advisor.GetAdvice(report, History(samples, 90)), a => a.Id == HardwareAdviceIds.Memory);

        Assert.Equal(expected, advice.Confidence);
        Assert.Equal("memory", advice.Component);
        Assert.Equal("Diag_Advice_Ram_Observation", advice.Observation.Key);
        Assert.Equal(8, advice.Observation.Args[0]);
        Assert.Equal(100d, (double)advice.Observation.Args[1]);
        Assert.Equal("Diag_Advice_Ram_Suggestion", advice.Suggestion.Key);
    }

    [Fact]
    public void Ram_advice_requires_frequent_high_usage()
    {
        var report = Reports.Healthy().WithMemory(60, 8L * ByteSize.GiB);
        var mostlyCalm = History(30, 60).Concat(History(5, 92)).ToList(); // 14 % du temps > 85 %
        Assert.DoesNotContain(_advisor.GetAdvice(report, mostlyCalm), a => a.Id == HardwareAdviceIds.Memory);
    }

    [Fact]
    public void No_ram_advice_above_8GB()
    {
        var report = Reports.Healthy().WithMemory(60, 16L * ByteSize.GiB);
        Assert.DoesNotContain(_advisor.GetAdvice(report, History(100, 95)), a => a.Id == HardwareAdviceIds.Memory);
    }

    [Fact]
    public void Hdd_system_drive_suggests_ssd()
    {
        var report = Reports.Healthy().WithSystemDrive(50, media: StorageMediaType.Hdd);
        var advice = Assert.Single(_advisor.GetAdvice(report, []), a => a.Id == HardwareAdviceIds.Ssd);
        Assert.Equal("storage", advice.Component);
        Assert.Equal("Diag_Advice_Ssd_Suggestion", advice.Suggestion.Key);
    }

    [Fact]
    public void System_drive_more_than_90_percent_full_is_reported()
    {
        Assert.Contains(_advisor.GetAdvice(Reports.Healthy().WithSystemDrive(5), []), a => a.Id == HardwareAdviceIds.StorageFull);
        Assert.DoesNotContain(_advisor.GetAdvice(Reports.Healthy().WithSystemDrive(20), []), a => a.Id == HardwareAdviceIds.StorageFull);
    }

    [Fact]
    public void Integrated_gpu_only_is_reported_but_not_with_a_dedicated_gpu()
    {
        var integrated = Reports.Healthy() with { Gpus = [new GpuInfo("Intel UHD 620", GpuVendor.Intel, null, null, null, false, true)] };
        var advice = Assert.Single(_advisor.GetAdvice(integrated, []), a => a.Id == HardwareAdviceIds.IntegratedGpu);
        Assert.Equal("Intel UHD 620", advice.Observation.Args[0]);

        Assert.DoesNotContain(_advisor.GetAdvice(Reports.Healthy(), []), a => a.Id == HardwareAdviceIds.IntegratedGpu);
        Assert.DoesNotContain(_advisor.GetAdvice(Reports.Healthy() with { Gpus = [] }, []), a => a.Id == HardwareAdviceIds.IntegratedGpu);
    }

    [Fact]
    public void Sustained_cpu_saturation_in_history_is_reported()
    {
        var advice = Assert.Single(_advisor.GetAdvice(Reports.Healthy(), History(40, 50, cpuPercent: 97)), a => a.Id == HardwareAdviceIds.CpuSaturated);
        Assert.Equal(ConfidenceLevel.Medium, advice.Confidence);
        Assert.DoesNotContain(_advisor.GetAdvice(Reports.Healthy(), History(40, 50, cpuPercent: 40)), a => a.Id == HardwareAdviceIds.CpuSaturated);
    }

    [Fact]
    public void Healthy_pc_gets_no_advice()
        => Assert.Empty(_advisor.GetAdvice(Reports.Healthy(), History(100, 40)));
}
