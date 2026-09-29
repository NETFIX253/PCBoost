using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Diagnostics.Analysis;
using PCBoost.Diagnostics.Hardware;
using PCBoost.Diagnostics.Rules;
using PCBoost.Diagnostics.Scoring;
using PCBoost.Diagnostics.Tests.TestSupport;
using PCBoost.TestUtilities;

namespace PCBoost.Diagnostics.Tests;

public sealed class SystemAnalyzerTests
{
    private static readonly AnalysisOptions FastOptions = new(LoadSamplingDuration: TimeSpan.FromMilliseconds(4));

    private readonly FakeClock _clock = new(Reports.Now);
    private readonly FakeSystemInfoProvider _systemInfo = new();
    private readonly CountingHardwareProvider _hardware = new();
    private readonly FakePowerProvider _power = new();
    private readonly FakeSystemMetricsProvider _metrics;
    private readonly FakePerformanceMonitor _monitor = new();
    private readonly FakeStartupService _startup = new();
    private readonly FakeCleanupService _cleanup = new();
    private readonly FakeProcessService _processes = new();
    private readonly FakeProcessProvider _processProvider = new() { CurrentProcessId = 4242, CurrentSessionId = 1 };
    private readonly FakeSettingsService _settings = new();
    private readonly InMemoryScanHistoryRepository _scanHistory = new();

    public SystemAnalyzerTests()
    {
        _metrics = new FakeSystemMetricsProvider(_clock);
        _processes
            .Add(0, "Idle", cpu: 90, memory: 0, currentUser: false, session: 0)
            .Add(4, "System", cpu: 2, memory: 900 * ByteSize.MiB, currentUser: false, session: 0)
            .Add(500, "csrss.exe", cpu: 1, memory: 800 * ByteSize.MiB, currentUser: false, session: 1, protection: ProtectionLevel.Critical)
            .Add(4242, "PCBoost.exe", cpu: 30, memory: 150 * ByteSize.MiB, hasWindow: true)
            .Add(1000, "chrome.exe", cpu: 12, memory: 1500 * ByteSize.MiB, hasWindow: true, path: @"C:\Users\Amin\AppData\Local\chrome.exe")
            .Add(1001, "Teams.exe", cpu: 3, memory: 700 * ByteSize.MiB, hasWindow: true)
            .Add(1002, "OneDrive.exe", cpu: 0.5, memory: 120 * ByteSize.MiB)
            .Add(1003, "SecurityHealthSystray.exe", cpu: 0, memory: 10 * ByteSize.MiB)
            .Add(1004, "updater.exe", cpu: 6, memory: 60 * ByteSize.MiB)
            .Add(1005, "helper.exe", cpu: 1, memory: 30 * ByteSize.MiB, session: 2)
            .Add(1006, "svchost.exe", cpu: 4, memory: 400 * ByteSize.MiB, currentUser: false, session: 0)
            .Add(1007, "RuntimeBroker.exe", cpu: 0.2, memory: 40 * ByteSize.MiB);
    }

    private SystemAnalyzer Analyzer()
    {
        var rules = new HealthRulesEngine(
            [new MemoryUsageRule(), new StartupCountRule(), new CleanableFilesRule(), new SystemDriveFreeSpaceRule()],
            NullLogger<HealthRulesEngine>.Instance);
        return new SystemAnalyzer(_systemInfo, _hardware, _power, _metrics, _monitor, _startup, _cleanup, _processes,
            _processProvider, new HardwareProfileClassifier(), new PerformanceScoreCalculator(_clock), rules, _settings, _scanHistory, _clock,
            NullLogger<SystemAnalyzer>.Instance, new SystemAnalyzerOptions { LoadSamplingInterval = TimeSpan.FromMilliseconds(1) });
    }

    [Fact]
    public async Task Full_analysis_populates_the_report()
    {
        _metrics.Enqueue(_metrics.Create(99, 50), _metrics.Create(20, 50, disk: 10, gpu: 4), _metrics.Create(40, 60, disk: 30, gpu: null),
            _metrics.Create(30, 55, disk: null, gpu: 8), _metrics.Create(10, 55, disk: 20, gpu: 6));
        var report = await Analyzer().AnalyzeAsync(FastOptions);

        Assert.Equal(_clock.UtcNow, report.Timestamp);
        Assert.Equal(26100, report.Os.BuildNumber);
        Assert.Equal(4, report.Cpu.PhysicalCores);
        Assert.Single(report.Drives);
        Assert.Equal(PowerScheme.Balanced, report.ActivePowerScheme!.Id);
        Assert.Equal(85, report.InstalledProgramCount);
        Assert.Equal(6, report.StartupEntries.Count);
        Assert.Equal(HardwareTier.LowEnd, report.HardwareProfile!.Tier);
        Assert.Equal(1, _hardware.Calls);

        // Échantillon d'amorçage (99 %) ignoré ; 4 échantillons mesurés.
        Assert.Equal(4, report.Load.SampleCount);
        Assert.Equal(25, report.Load.CpuAveragePercent, 3);
        Assert.Equal(40, report.Load.CpuMaxPercent, 3);
        Assert.Equal(55, report.Load.MemoryUsedPercent, 3);
        Assert.Equal(20, report.Load.DiskActiveAveragePercent!.Value, 3);
        Assert.Equal(6, report.Load.GpuAveragePercent!.Value, 3);
    }

    [Fact]
    public async Task Cleanable_bytes_sum_only_available_safe_categories()
    {
        var report = await Analyzer().AnalyzeAsync(FastOptions);
        // user-temp 300 Mio + recycle-bin 200 Mio ; windows-temp indisponible ; crash dumps = CAUTION.
        Assert.Equal(500 * ByteSize.MiB, report.CleanableBytes);
        Assert.NotNull(_cleanup.LastRequestedIds);
        Assert.DoesNotContain("system-crash-dumps", _cleanup.LastRequestedIds!);
    }

    [Fact]
    public async Task Process_summary_excludes_idle_system_critical_and_itself()
    {
        var report = await Analyzer().AnalyzeAsync(FastOptions);

        Assert.Equal(11, report.RunningProcessCount); // tout sauf le pid 0
        Assert.Equal(new[] { "chrome.exe", "Teams.exe", "svchost.exe", "OneDrive.exe", "updater.exe" }, report.TopMemoryProcesses.Select(p => p.Name));
        Assert.Equal(new[] { "chrome.exe", "updater.exe", "svchost.exe", "Teams.exe", "helper.exe" }, report.TopCpuProcesses.Select(p => p.Name));
        Assert.DoesNotContain(report.TopMemoryProcesses, p => p.ProcessId is 0 or 4 or 500 or 4242);
        // Arrière-plan : utilisateur courant, session 1, sans fenêtre, non critique (OneDrive, SecurityHealth, updater, RuntimeBroker).
        Assert.Equal(4, report.BackgroundProcessCount);
    }

    [Fact]
    public async Task Progress_is_reported_in_stage_order_up_to_100()
    {
        var progress = new SyncProgress<AnalysisProgress>();
        await Analyzer().AnalyzeAsync(FastOptions, progress);

        var stages = progress.Values.Select(p => p.Stage).ToList();
        for (var i = 1; i < stages.Count; i++) Assert.True(stages[i - 1] <= stages[i]);
        Assert.Equal(
            new[] { AnalysisStage.System, AnalysisStage.Startup, AnalysisStage.Storage, AnalysisStage.Processes, AnalysisStage.Results },
            stages.Distinct());
        for (var i = 1; i < progress.Values.Count; i++) Assert.True(progress.Values[i - 1].Percent <= progress.Values[i].Percent);
        Assert.Equal(new AnalysisProgress(AnalysisStage.Results, 100), progress.Values[^1]);
    }

    [Fact]
    public async Task Scan_record_is_saved_with_score_findings_and_private_summary()
    {
        _startup.Entries = Reports.StartupEntries(9, 1); // déclenche la règle de démarrage
        var report = await Analyzer().AnalyzeAsync(FastOptions);

        var record = Assert.Single(await _scanHistory.GetRecentAsync(10));
        Assert.Equal(report.Timestamp, record.Timestamp);
        Assert.InRange(record.Score, 1, 100);
        Assert.Equal(1, record.FindingCount);

        using var json = JsonDocument.Parse(record.SummaryJson);
        Assert.Equal(1, json.RootElement.GetProperty("v").GetInt32());
        Assert.Equal(record.Score, json.RootElement.GetProperty("score").GetInt32());
        Assert.Equal(9, json.RootElement.GetProperty("startup").GetProperty("enabled").GetInt32());
        Assert.Equal("health.startup.count", json.RootElement.GetProperty("findings")[0].GetString());
        // Aucune donnée personnelle : pas de noms de processus, de chemins ni de nom d'utilisateur.
        Assert.DoesNotContain("chrome", record.SummaryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Amin", record.SummaryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\\\", record.SummaryJson);
    }

    [Fact]
    public async Task Last_report_and_completion_event_are_set()
    {
        var analyzer = Analyzer();
        SystemAnalysisReport? raised = null;
        analyzer.AnalysisCompleted += (_, r) => raised = r;
        Assert.Null(analyzer.LastReport);

        var report = await analyzer.AnalyzeAsync(FastOptions);
        Assert.Same(report, analyzer.LastReport);
        Assert.Same(report, raised);
    }

    [Fact]
    public async Task Throwing_event_subscriber_does_not_fail_the_analysis()
    {
        var analyzer = Analyzer();
        analyzer.AnalysisCompleted += (_, _) => throw new InvalidOperationException("abonné");
        var report = await analyzer.AnalyzeAsync(FastOptions);
        Assert.Same(report, analyzer.LastReport);
    }

    [Fact]
    public async Task Failing_substeps_do_not_prevent_the_report()
    {
        _startup.Throw = new UnauthorizedAccessException(@"Accès refusé à C:\Users\Amin\secret");
        _cleanup.Throw = new IOException("scan");
        _processes.Throw = new System.ComponentModel.Win32Exception(5);

        var report = await Analyzer().AnalyzeAsync(FastOptions);

        Assert.Empty(report.StartupEntries);
        Assert.Null(report.CleanableBytes);
        Assert.Equal(0, report.RunningProcessCount);
        Assert.Empty(report.TopMemoryProcesses);
        Assert.Single(await _scanHistory.GetRecentAsync(10));
        Assert.NotNull(report.HardwareProfile);
    }

    [Fact]
    public async Task Failing_platform_providers_yield_unknown_values_not_invented_ones()
    {
        var failing = new ThrowingSystemInfoProvider();
        var analyzer = new SystemAnalyzer(failing, new CountingHardwareProvider { Throw = true }, new ThrowingPowerProvider(), new ThrowingMetricsProvider(),
            _monitor, _startup, _cleanup, _processes, _processProvider, new HardwareProfileClassifier(), new PerformanceScoreCalculator(_clock),
            new HealthRulesEngine([new MemoryUsageRule(), new UnsupportedBuildRule()], NullLogger<HealthRulesEngine>.Instance),
            _settings, _scanHistory, _clock, NullLogger<SystemAnalyzer>.Instance, new SystemAnalyzerOptions { LoadSamplingInterval = TimeSpan.FromMilliseconds(1) });

        var report = await analyzer.AnalyzeAsync(FastOptions);

        Assert.Equal(0, report.Os.BuildNumber);
        Assert.Equal(0, report.Memory.TotalBytes);
        Assert.Empty(report.Drives);
        Assert.Null(report.ActivePowerScheme);
        Assert.Equal(0, report.Load.SampleCount);
        Assert.False(report.Temperatures.Cpu.HasValue);
        Assert.Equal(HardwareTier.Unknown, report.HardwareProfile!.Tier);
        // Build illisible : pas de constat « Windows non pris en charge ».
        var record = Assert.Single(await _scanHistory.GetRecentAsync(10));
        Assert.Equal(0, record.FindingCount);
    }

    [Fact]
    public async Task Skipped_stages_leave_values_empty()
    {
        var report = await Analyzer().AnalyzeAsync(new AnalysisOptions(IncludeCleanupScan: false, IncludeStartup: false, IncludeProcesses: false,
            LoadSamplingDuration: TimeSpan.FromMilliseconds(2)));
        Assert.Null(report.CleanableBytes);
        Assert.Empty(report.StartupEntries);
        Assert.Equal(0, report.RunningProcessCount);
        Assert.Equal(0, _startup.Calls);
    }

    [Fact]
    public async Task Analysis_is_cancellable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Analyzer().AnalyzeAsync(FastOptions, null, cts.Token));
        Assert.Empty(await _scanHistory.GetRecentAsync(10));
    }

    [Fact]
    public async Task Cancellation_during_sampling_stops_the_analysis()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var slow = new AnalysisOptions(LoadSamplingDuration: TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Analyzer().AnalyzeAsync(slow, null, cts.Token));
    }

    [Fact]
    public async Task Recent_monitor_history_is_used_instead_of_direct_sampling()
    {
        _monitor.IsRunning = true;
        _monitor.Now = () => _clock.UtcNow;
        for (var i = 4; i >= 0; i--)
            _monitor.History.Add(new SystemMetricsSample(_clock.UtcNow.AddSeconds(-i), 50, 70, 0, 8L * ByteSize.GiB, 12, 0, 0, 5, null, 0, 0, 200));

        var report = await Analyzer().AnalyzeAsync(new AnalysisOptions(LoadSamplingDuration: TimeSpan.FromSeconds(5)));

        Assert.Equal(0, _metrics.SampleCalls);
        Assert.Equal(5, report.Load.SampleCount);
        Assert.Equal(50, report.Load.CpuAveragePercent, 3);
        Assert.Equal(12, report.Load.DiskActiveAveragePercent!.Value, 3);
    }

    [Fact]
    public async Task Insufficient_monitor_history_falls_back_to_direct_sampling()
    {
        _monitor.IsRunning = true;
        _monitor.Now = () => _clock.UtcNow;
        _monitor.History.Add(new SystemMetricsSample(_clock.UtcNow, 50, 70, 0, 8L * ByteSize.GiB, 12, 0, 0, 5, null, 0, 0, 200));

        await Analyzer().AnalyzeAsync(FastOptions);
        Assert.True(_metrics.SampleCalls >= 2);
    }

    [Fact]
    public void Observation_ignores_unavailable_counters()
    {
        var clock = new FakeClock();
        var provider = new FakeSystemMetricsProvider(clock);
        var samples = new[] { provider.Create(10, 40, disk: null, gpu: null), provider.Create(30, 60, disk: null, gpu: null) };
        var observation = SystemAnalyzer.BuildObservation(samples, TimeSpan.FromSeconds(1), new MemoryInfo(8L * ByteSize.GiB, 4L * ByteSize.GiB, 0, 0, null, null));

        Assert.Equal(20, observation.CpuAveragePercent, 3);
        Assert.Equal(50, observation.MemoryUsedPercent, 3);
        Assert.Null(observation.DiskActiveAveragePercent);
        Assert.Null(observation.GpuAveragePercent);
        Assert.Equal(2, observation.SampleCount);
    }

    private sealed class ThrowingSystemInfoProvider : Core.Abstractions.Platform.ISystemInfoProvider
    {
        public OsInfo GetOsInfo() => throw new UnauthorizedAccessException();
        public CpuInfo GetCpuInfo() => throw new UnauthorizedAccessException();
        public MemoryInfo GetMemoryInfo() => throw new UnauthorizedAccessException();
        public IReadOnlyList<GpuInfo> GetGpus() => throw new UnauthorizedAccessException();
        public IReadOnlyList<StorageDrive> GetDrives() => throw new UnauthorizedAccessException();
        public PowerStatus GetPowerStatus() => throw new UnauthorizedAccessException();
        public int? GetInstalledProgramCount() => throw new UnauthorizedAccessException();
    }

    private sealed class ThrowingPowerProvider : Core.Abstractions.Platform.IPowerProvider
    {
        public PowerScheme? GetActiveScheme() => throw new UnauthorizedAccessException();
        public IReadOnlyList<PowerScheme> GetSchemes() => throw new UnauthorizedAccessException();
        public OperationResult SetActiveScheme(Guid schemeId) => throw new InvalidOperationException();
    }
}
