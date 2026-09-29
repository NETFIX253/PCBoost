using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;

namespace PCBoost.Optimization.Tests;

internal sealed class FakeForegroundWindowProvider : IForegroundWindowProvider
{
    public int? ForegroundProcessId { get; set; }
    public int? GetForegroundProcessId() => ForegroundProcessId;
    public bool IsForegroundFullscreen() => false;
}

internal sealed class FakePerformanceMonitor : IPerformanceMonitor
{
    public List<SystemMetricsSample> History { get; } = [];
    public MonitoringMode Mode { get; private set; } = MonitoringMode.Background;
    public bool IsRunning { get; private set; }
    public SystemMetricsSample? Latest => History.LastOrDefault();
    public TemperatureReadings Temperatures => TemperatureReadings.None;
    public TimeSpan CurrentInterval => TimeSpan.FromSeconds(5);
    public event EventHandler<SystemMetricsSample>? SampleAvailable;
    public event EventHandler? ModeChanged;
    public void Start() => IsRunning = true;
    public void Stop() => IsRunning = false;
    public void SetMode(MonitoringMode mode)
    {
        Mode = mode;
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }
    public IReadOnlyList<SystemMetricsSample> GetHistory(TimeSpan window) => History.ToList();
    public MetricStatistics GetStatistics(MetricKind metric, TimeSpan window)
        => new(metric, History.LastOrDefault()?.MemoryUsedPercent, History.Count == 0 ? null : History.Average(s => s.MemoryUsedPercent), History.Count == 0 ? null : History.Max(s => s.MemoryUsedPercent), History.Count);
    public void Add(SystemMetricsSample sample)
    {
        History.Add(sample);
        SampleAvailable?.Invoke(this, sample);
    }
    public void Dispose() { }
}

internal sealed class FakeGamingService : IGamingService
{
    public GamingState State { get; set; } = GamingState.Inactive;
    public GamingSession? CurrentSession => null;
    public DetectedGameProcess? CurrentGame => null;
    public GamingLiveMetrics? LiveMetrics => null;
    public event EventHandler? StateChanged;
    public event EventHandler<GamingLiveMetrics>? LiveMetricsUpdated;
    public Task<IReadOnlyList<ActiveGamingOptimization>> PreviewAsync(DetectedGameProcess? game, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ActiveGamingOptimization>>([]);
    public Task<GamingActivationReport> ActivateAsync(DetectedGameProcess? game, CancellationToken cancellationToken = default)
    {
        State = GamingState.Active;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new GamingActivationReport(null, OperationResult.Ok(), game, [], FrameCaptureAvailability.Disabled));
    }
    public Task<GamingRestoreReport> DeactivateAsync(CancellationToken cancellationToken = default)
    {
        State = GamingState.Inactive;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new GamingRestoreReport(null, 0, 0, []));
    }
    public IReadOnlyList<GameSettingCheck> CheckWindowsGameSettings(DetectedGameProcess? game) => [];
    public void RaiseMetrics(GamingLiveMetrics metrics) => LiveMetricsUpdated?.Invoke(this, metrics);
}

internal sealed class FakeSystemAnalyzer : ISystemAnalyzer
{
    public SystemAnalysisReport? LastReport { get; set; }
    public event EventHandler<SystemAnalysisReport>? AnalysisCompleted;
    public Task<SystemAnalysisReport> AnalyzeAsync(AnalysisOptions options, IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var report = LastReport ?? Reports.Create();
        AnalysisCompleted?.Invoke(this, report);
        return Task.FromResult(report);
    }
}

/// <summary>Gestionnaire d'annulation qui enregistre l'ordre des annulations.</summary>
internal sealed class RecordingChangeHandler : IChangeHandler
{
    public RecordingChangeHandler(string kind) => Kind = kind;
    public string Kind { get; }
    public List<string> UndoneTargets { get; } = [];
    public HashSet<string> FailingTargets { get; } = [];
    public bool CanUndo(ChangeRecord change) => true;
    public Task<OperationResult> UndoAsync(ChangeRecord change, CancellationToken cancellationToken = default)
    {
        if (FailingTargets.Contains(change.Target))
            return Task.FromResult(OperationResult.Fail(OperationErrorKind.AccessDenied));
        UndoneTargets.Add(change.Target);
        return Task.FromResult(OperationResult.Ok());
    }
}

/// <summary>Planificateur dont les tâches exigent l'élévation pour être modifiées.</summary>
internal sealed class ProtectedScheduledTaskProvider : IScheduledTaskProvider
{
    public List<ScheduledTaskInfo> Tasks { get; } = [];
    public IReadOnlyList<ScheduledTaskInfo> GetLogonTasks() => Tasks;
    public bool? IsEnabled(string taskPath) => Tasks.FirstOrDefault(t => t.Path == taskPath)?.Enabled;
    public OperationResult SetEnabled(string taskPath, bool enabled) => OperationResult.Fail(OperationErrorKind.RequiresElevation);
}

/// <summary>Optimisation configurable pour tester l'orchestrateur.</summary>
internal sealed class FakeOptimization : IOptimization
{
    public FakeOptimization(string id, RiskLevel risk = RiskLevel.Low, bool reversible = true)
    {
        Id = id;
        RiskLevel = risk;
        IsReversible = reversible;
    }

    public string Id { get; }
    public TextRef Name => TextRef.Literal(Id);
    public TextRef Description => TextRef.Literal(Id);
    public OptimizationCategory Category => OptimizationCategory.Windows;
    public RiskLevel RiskLevel { get; }
    public ImpactLevel ImpactLevel => ImpactLevel.Low;
    public bool IsReversible { get; }
    public int MinimumWindowsBuild { get; set; } = 17763;
    public bool ThrowOnApply { get; set; }
    public int ApplyCalls { get; private set; }
    public IReadOnlySet<string>? LastSelection { get; private set; }

    public Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(new OptimizationPreview(Id, Name, true, null,
            [new PlannedChange(Id + ":1", TextRef.Literal(Id), "fake:" + Id, true, IsReversible, RiskLevel)],
            RiskLevel, ImpactLevel, IsReversible, false, false));

    public async Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        LastSelection = context.SelectedChangeIds;
        if (ThrowOnApply) throw new InvalidOperationException("boom");
        var outcome = await recorder.ApplyAsync(
            new PendingChange(ChangeKinds.PowerScheme, Id, "fake:" + Id, TextRef.Literal(Id),
                ChangeStateSerializer.Serialize(new PowerSchemeState(PowerScheme.Balanced, "Utilisation normale")), true),
            _ => Task.FromResult(OperationResult.Ok()), cancellationToken);
        return new OptimizationResult(Id, outcome, outcome.Success ? 1 : 0, outcome.Success ? 0 : 1, 0, false, []);
    }

    public Task<RollbackResult> RollbackAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult(RollbackResult.Empty);
}

internal static class Reports
{
    public static SystemAnalysisReport Create(
        HardwareTier tier = HardwareTier.MidRange,
        long totalRam = 8L * ByteSize.GiB,
        long availableRam = 4L * ByteSize.GiB,
        int cores = 4,
        int threads = 8,
        int? baseClock = 2400,
        StorageMediaType media = StorageMediaType.Ssd,
        long totalDisk = 256L * ByteSize.GiB,
        long freeDisk = 100L * ByteSize.GiB,
        int enabledStartup = 3,
        int backgroundProcesses = 80)
    {
        var startup = Enumerable.Range(0, enabledStartup).Select(i => new Core.Models.Startup.StartupEntry
        {
            Id = "e" + i,
            Name = "App" + i,
            Location = Core.Models.Startup.StartupLocation.RegistryRunUser,
            SourcePath = "HKCU",
            ItemName = "App" + i,
            IsEnabled = true,
        }).ToList();
        return new SystemAnalysisReport
        {
            Timestamp = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero),
            Os = new OsInfo("Windows 11 Pro", "24H2", 26100, 1000, ProcessorArchitecture.X64, true, true, TimeSpan.FromHours(3), false),
            Cpu = new CpuInfo("CPU", "GenuineIntel", cores, threads, baseClock, null, null),
            Memory = new MemoryInfo(totalRam, availableRam, 0, 0, null, null),
            Gpus = [],
            Drives = [new StorageDrive(@"C:\", "Windows", "NTFS", totalDisk, freeDisk, true, false, media, StorageBusType.Sata, null)],
            Load = new LoadObservation(10, 20, 50, 5, null, TimeSpan.FromSeconds(5), 5),
            Temperatures = TemperatureReadings.None,
            Power = new PowerStatus(PowerSource.AC, 100, true),
            StartupEntries = startup,
            BackgroundProcessCount = backgroundProcesses,
            HardwareProfile = new HardwareProfile(tier, [], false, media == StorageMediaType.Hdd, cores <= 2, false, false),
        };
    }
}
