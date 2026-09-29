using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Cleanup;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;

namespace PCBoost.Presentation.Tests.Infrastructure;

public static class Reports
{
    public static SystemAnalysisReport Create(DateTimeOffset timestamp, int enabledStartup = 3) => new()
    {
        Timestamp = timestamp,
        Os = new OsInfo("Windows 11 Professionnel", "24H2", 26100, 2033, ProcessorArchitecture.X64, true, true, TimeSpan.FromHours(30), false),
        Cpu = new CpuInfo("Intel Core i5-8250U", "GenuineIntel", 4, 8, 1600, 3400, 6144),
        Memory = new MemoryInfo(16L * ByteSize.GiB, 6L * ByteSize.GiB, 10L * ByteSize.GiB, 20L * ByteSize.GiB, 2400, 2),
        Gpus = [new GpuInfo("Intel UHD 620", GpuVendor.Intel, null, 4L * ByteSize.GiB, "31.0", false, true)],
        Drives = [new StorageDrive(@"C:\", "Windows", "NTFS", 256L * ByteSize.GiB, 60L * ByteSize.GiB, true, false, StorageMediaType.Ssd, StorageBusType.Nvme, "SSD")],
        Load = new LoadObservation(25, 60, 62, 4, null, TimeSpan.FromSeconds(5), 5),
        Temperatures = TemperatureReadings.None,
        Power = new PowerStatus(PowerSource.AC, null, false),
        StartupEntries = Enumerable.Range(0, enabledStartup).Select(i => new StartupEntry
        {
            Id = $"run:{i}", Name = $"App {i}", Location = StartupLocation.RegistryRunUser, SourcePath = "HKCU", ItemName = $"App{i}", IsEnabled = true,
        }).ToList(),
        RunningProcessCount = 150,
        BackgroundProcessCount = 120,
    };
}

public sealed class FakeAnalyzer : ISystemAnalyzer
{
    public SystemAnalysisReport? LastReport { get; set; }

    public SystemAnalysisReport Next { get; set; } = Reports.Create(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));

    public int Calls { get; private set; }

    public event EventHandler<SystemAnalysisReport>? AnalysisCompleted;

    public Task<SystemAnalysisReport> AnalyzeAsync(AnalysisOptions options, IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        foreach (var stage in Enum.GetValues<AnalysisStage>()) progress?.Report(new AnalysisProgress(stage, (int)stage * 25));
        LastReport = Next;
        AnalysisCompleted?.Invoke(this, Next);
        return Task.FromResult(Next);
    }
}

public sealed class FakeRules : IHealthRulesEngine
{
    public List<HealthFinding> Findings { get; } = [];

    public IReadOnlyList<HealthFinding> Evaluate(SystemAnalysisReport report, HealthThresholds thresholds) => Findings;
}

public sealed class FakeScore : IPerformanceScoreCalculator
{
    public PerformanceScore Score { get; set; } = new(100, [], DateTimeOffset.UnixEpoch);

    public PerformanceScore Calculate(SystemAnalysisReport report, HealthThresholds thresholds) => Score;
}

public sealed class FakeRecommendations : IPerformanceRecommendationEngine
{
    public List<Recommendation> Items { get; } = [];

    public IReadOnlyList<Recommendation> GetRecommendations(SystemAnalysisReport report, IReadOnlyList<HealthFinding> findings, IReadOnlyCollection<string> dismissedIds)
        => Items.Where(r => !dismissedIds.Contains(r.Id)).ToList();
}

public sealed class FakeMonitor : IPerformanceMonitor
{
    public MonitoringMode Mode { get; private set; } = MonitoringMode.Background;

    public bool IsRunning { get; private set; } = true;

    public SystemMetricsSample? Latest { get; set; }

    public TemperatureReadings Temperatures { get; set; } = TemperatureReadings.None;

    public TimeSpan CurrentInterval => Mode == MonitoringMode.Active ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);

    public List<SystemMetricsSample> History { get; } = [];

    public event EventHandler<SystemMetricsSample>? SampleAvailable;

    public event EventHandler? ModeChanged;

    public void Start() => IsRunning = true;

    public void Stop() => IsRunning = false;

    public void SetMode(MonitoringMode mode)
    {
        Mode = mode;
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<SystemMetricsSample> GetHistory(TimeSpan window) => History;

    public MetricStatistics GetStatistics(MetricKind metric, TimeSpan window) => new(metric, null, null, null, 0);

    public void Publish(SystemMetricsSample sample)
    {
        Latest = sample;
        History.Add(sample);
        SampleAvailable?.Invoke(this, sample);
    }

    public int SubscriberCount => SampleAvailable?.GetInvocationList().Length ?? 0;

    public void Dispose()
    {
    }
}

public sealed class FakeOptimization : IOptimization
{
    public FakeOptimization(string id) => Id = id;

    public string Id { get; }

    public TextRef Name => TextRef.Literal($"Optimisation {Id}");

    public TextRef Description => TextRef.Literal($"Description {Id}");

    public OptimizationCategory Category => OptimizationCategory.Power;

    public RiskLevel RiskLevel => RiskLevel.Low;

    public ImpactLevel ImpactLevel => ImpactLevel.Medium;

    public bool IsReversible => true;

    public int MinimumWindowsBuild => 17763;

    public Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<RollbackResult> RollbackAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

public sealed class FakeOptimizationManager : IOptimizationManager
{
    public List<IOptimization> Items { get; } = [new FakeOptimization("power"), new FakeOptimization("temp")];

    public OptimizationPlan Plan { get; set; } = new(SessionType.OneClick, [], new HashSet<string>());

    public OptimizationPlan? Executed { get; private set; }

    public List<IReadOnlyCollection<string>> BuiltPlans { get; } = [];

    public OptimizationRunReport Report { get; set; } = new(Guid.NewGuid(), SessionStatus.Completed, 3, 0, 512L * ByteSize.MiB, 2, 1, false, null, [], []);

    public IReadOnlyList<IOptimization> Optimizations => Items;

    public IOptimization? Find(string optimizationId) => Items.FirstOrDefault(o => o.Id == optimizationId);

    public Task<OptimizationPlan> BuildOneClickPlanAsync(SystemAnalysisReport analysis, CancellationToken cancellationToken = default) => Task.FromResult(Plan);

    public Task<OptimizationPlan> BuildPlanAsync(SessionType sessionType, IReadOnlyCollection<string> optimizationIds, SystemAnalysisReport? analysis, string? profileId = null, CancellationToken cancellationToken = default)
    {
        BuiltPlans.Add(optimizationIds);
        return Task.FromResult(Plan with { SessionType = sessionType });
    }

    public Task<OptimizationRunReport> ExecuteAsync(OptimizationPlan plan, SystemAnalysisReport? analysis, IProgress<OptimizationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Executed = plan;
        foreach (var stage in Enum.GetValues<OptimizationStage>()) progress?.Report(new OptimizationProgress(stage, (int)stage * 20, null));
        return Task.FromResult(Report);
    }
}

public sealed class FakeRollbackManager : IRollbackManager
{
    public List<OptimizationSession> Sessions { get; } = [];

    public List<Guid> Restored { get; } = [];

    public List<Guid> Undone { get; } = [];

    public RollbackResult RestoreResult { get; set; } = new(3, 0, 1, []);

    public event EventHandler<Guid>? SessionChanged;

    public Task<IChangeRecorder> BeginSessionAsync(SessionType type, TextRef title, string? profileId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task CompleteSessionAsync(Guid sessionId, long bytesFreed = 0, bool requiresRestart = false, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<OperationResult> UndoChangeAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        Undone.Add(changeId);
        return Task.FromResult(OperationResult.Ok());
    }

    public Task<RollbackResult> RestoreSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Restored.Add(sessionId);
        SessionChanged?.Invoke(this, sessionId);
        return Task.FromResult(RestoreResult);
    }

    public bool CanRollback(OptimizationSession session) => session.Status is SessionStatus.Completed or SessionStatus.PartiallyCompleted && session.ReversibleChangeCount > 0;

    public Task<IReadOnlyList<OptimizationSession>> GetHistoryAsync(int limit = 100, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OptimizationSession>>(Sessions.Take(limit).ToList());

    public Task<OptimizationSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(Sessions.FirstOrDefault(s => s.Id == sessionId));
}

public sealed class FakeProfileService : IProfileService
{
    public List<PerformanceProfile> Profiles { get; } =
    [
        new(PerformanceProfile.BalancedId, TextRef.Literal("Équilibré"), TextRef.Literal("Par défaut"), [], true, "\uE9F5"),
        new(PerformanceProfile.GamingId, TextRef.Literal("Gaming"), TextRef.Literal("Jeu"), ["power"], true, "\uE7FC"),
        new(PerformanceProfile.CustomId, TextRef.Literal("Personnalisé"), TextRef.Literal("Vos choix"), ["temp"], false, "\uE713"),
    ];

    public ProfileState State { get; set; } = new(null, null, null);

    public event EventHandler? StateChanged;

    public IReadOnlyList<PerformanceProfile> GetProfiles() => Profiles;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<OptimizationPlan> PreviewActivationAsync(string profileId, CancellationToken cancellationToken = default)
        => Task.FromResult(new OptimizationPlan(SessionType.Profile, [], new HashSet<string>(), profileId));

    public Task<OptimizationRunReport> ActivateAsync(OptimizationPlan plan, CancellationToken cancellationToken = default)
    {
        State = new ProfileState(plan.ProfileId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new OptimizationRunReport(Guid.NewGuid(), SessionStatus.Completed, 1, 0, 0, 0, 0, false, plan.ProfileId, [], []));
    }

    public Task<RollbackResult> DeactivateAsync(CancellationToken cancellationToken = default)
    {
        State = new ProfileState(null, null, null);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new RollbackResult(1, 0, 0, []));
    }

    public List<IReadOnlyList<string>> SavedCustom { get; } = [];

    public Task SaveCustomProfileAsync(IReadOnlyList<string> optimizationIds, CancellationToken cancellationToken = default)
    {
        SavedCustom.Add(optimizationIds);
        return Task.CompletedTask;
    }
}

public sealed class FakeOldPcAssistant : IOldPcAssistant
{
    public Task<OldPcAssessment> AssessAsync(SystemAnalysisReport analysis, CancellationToken cancellationToken = default)
        => Task.FromResult(new OldPcAssessment(true, false, true, false, true, false, [TextRef.Literal("Mémoire limitée")],
            new Dictionary<OldPcLevel, IReadOnlyList<string>>
            {
                [OldPcLevel.Essential] = ["temp"],
                [OldPcLevel.Standard] = ["temp", "power"],
                [OldPcLevel.Advanced] = ["temp", "power"],
            }));

    public Task<OptimizationPlan> BuildPlanAsync(OldPcLevel level, SystemAnalysisReport analysis, CancellationToken cancellationToken = default)
        => Task.FromResult(new OptimizationPlan(SessionType.OldPcAssistant,
            [new OptimizationPreview("power", TextRef.Literal("Alimentation"), true, null,
                [new PlannedChange("c1", TextRef.Literal("Mode performances"), "power", true, true, RiskLevel.Low)],
                RiskLevel.Low, ImpactLevel.Medium, true, false, false)],
            new HashSet<string>()));
}

public sealed class FakeShellService : IShellService
{
    public List<string> Calls { get; } = [];

    public OperationResult OpenFolder(string path) { Calls.Add($"folder:{path}"); return OperationResult.Ok(); }

    public OperationResult RevealInExplorer(string filePath) { Calls.Add($"reveal:{filePath}"); return OperationResult.Ok(); }

    public OperationResult ShowFileProperties(string filePath) { Calls.Add($"props:{filePath}"); return OperationResult.Ok(); }

    public OperationResult OpenUri(Uri uri) { Calls.Add($"uri:{uri}"); return OperationResult.Ok(); }

    public OperationResult SearchOnline(string term) { Calls.Add($"search:{term}"); return OperationResult.Ok(); }
}

public sealed class FakeStartupService : IStartupService
{
    public List<StartupEntry> Entries { get; } = [];

    public OperationResult NextResult { get; set; } = OperationResult.Ok();

    public List<(string Id, bool Enabled)> Calls { get; } = [];

    public Task<IReadOnlyList<StartupEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StartupEntry>>(Entries.ToList());

    public Task<OperationResult> SetEnabledAsync(StartupEntry entry, bool enabled, CancellationToken cancellationToken = default)
    {
        Calls.Add((entry.Id, enabled));
        return Task.FromResult(NextResult);
    }
}

public sealed class FakeProcessService : IProcessService
{
    public List<ProcessInfo> Processes { get; } = [];

    public List<int> Terminated { get; } = [];

    public List<int> Closed { get; } = [];

    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProcessInfo>>(Processes.ToList());

    public OperationResult CloseApplication(int processId) { Closed.Add(processId); return OperationResult.Ok(); }

    public OperationResult TerminateProcess(int processId) { Terminated.Add(processId); return OperationResult.Ok(); }

    public ProtectionInfo GetProtection(string processName, string? executablePath) => ProtectionInfo.None;

    public static ProcessInfo Create(int pid, string name, double cpu, long memory, ProtectionLevel protection = ProtectionLevel.None, bool hasWindow = true)
        => new(pid, name, $@"C:\Apps\{name}", cpu, memory, null, ProcessPriority.Normal, hasWindow, null, true, 1, null,
            new TrustAssessment(TrustLevel.SignedPublisher, new SignatureInfo(SignatureStatus.Signed, "Contoso"), "Contoso", null, false, true),
            new ProtectionInfo(protection, protection == ProtectionLevel.Critical ? TextRef.Literal("Processus critique") : null));
}

public sealed class FakeCleanupService : ICleanupService
{
    public List<CleanupCategory> Categories { get; } =
    [
        new("user-temp", TextRef.Literal("Fichiers temporaires"), TextRef.Literal("Temp"), SafetyCategory.Safe, false, true, TimeSpan.FromDays(1)),
        new("recycle-bin", TextRef.Literal("Corbeille"), TextRef.Literal("Corbeille"), SafetyCategory.Caution, false, false, TimeSpan.Zero),
        new("windows-temp", TextRef.Literal("Temp Windows"), TextRef.Literal("Temp"), SafetyCategory.Safe, true, true, TimeSpan.FromDays(1)),
    ];

    public List<IReadOnlyCollection<string>> Cleaned { get; } = [];

    public IReadOnlyList<CleanupCategory> GetCategories() => Categories;

    public List<CleanupScanResult> ScanResults { get; } =
    [
        new("user-temp", 300L * ByteSize.MiB, 120, true, null, false),
        new("recycle-bin", 1L * ByteSize.GiB, 10, true, null, false),
        new("windows-temp", 0, 0, false, TextRef.Literal("Nécessite une autorisation"), true),
    ];

    public Task<IReadOnlyList<CleanupScanResult>> ScanAsync(IReadOnlyCollection<string>? categoryIds = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CleanupScanResult>>([.. ScanResults]);

    public Task<CleanupSummary> CleanAsync(IReadOnlyCollection<string> categoryIds, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Cleaned.Add(categoryIds);
        progress?.Report(1);
        return Task.FromResult(new CleanupSummary([new CleanupExecutionResult("user-temp", 290L * ByteSize.MiB, 110, 10, OperationResult.Ok())]));
    }
}

public sealed class FakeRecoveryManager : IRecoveryManager
{
    public List<OptimizationSession> Interrupted { get; } = [];

    public List<Guid> Recovered { get; } = [];

    public List<Guid> Dismissed { get; } = [];

    public Task<IReadOnlyList<OptimizationSession>> FindInterruptedSessionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OptimizationSession>>(Interrupted.ToList());

    public Task<RollbackResult> RecoverAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Recovered.Add(sessionId);
        return Task.FromResult(new RollbackResult(2, 0, 0, []));
    }

    public Task DismissAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Dismissed.Add(sessionId);
        return Task.CompletedTask;
    }
}

public sealed class FakeAutoGamingMode : IAutoGamingMode
{
    public event EventHandler<DetectedGameProcess>? ActivationSuggested;

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public void Suggest(DetectedGameProcess game) => ActivationSuggested?.Invoke(this, game);

    public void Dispose()
    {
    }
}

public sealed class FakeGamingService : IGamingService
{
    public GamingState State { get; set; }

    public GamingSession? CurrentSession { get; set; }

    public DetectedGameProcess? CurrentGame { get; set; }

    public GamingLiveMetrics? LiveMetrics { get; set; }

    public List<DetectedGameProcess?> Activations { get; } = [];

    public event EventHandler? StateChanged;

    public event EventHandler<GamingLiveMetrics>? LiveMetricsUpdated;

    public Task<IReadOnlyList<ActiveGamingOptimization>> PreviewAsync(DetectedGameProcess? game, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ActiveGamingOptimization>>([new("power", TextRef.Literal("Plan d'alimentation"), true, null)]);

    public Task<GamingActivationReport> ActivateAsync(DetectedGameProcess? game, CancellationToken cancellationToken = default)
    {
        Activations.Add(game);
        State = GamingState.Active;
        CurrentGame = game;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new GamingActivationReport(Guid.NewGuid(), OperationResult.Ok(), game,
            [new("power", TextRef.Literal("Plan d'alimentation"), true, null)], FrameCaptureAvailability.RequiresElevation));
    }

    public Task<GamingRestoreReport> DeactivateAsync(CancellationToken cancellationToken = default)
    {
        State = GamingState.Inactive;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new GamingRestoreReport(Guid.NewGuid(), 1, 0, []));
    }

    public IReadOnlyList<GameSettingCheck> CheckWindowsGameSettings(DetectedGameProcess? game) => [];

    public void PublishMetrics(GamingLiveMetrics metrics)
    {
        LiveMetrics = metrics;
        LiveMetricsUpdated?.Invoke(this, metrics);
    }
}

public sealed class FakeAppInfo : IAppInfo
{
    public string ProductName => "PCBoost";

    public Version Version => new(1, 2, 3);

    public string DotNetVersion => "10.0.0";

    public string WindowsAppSdkVersion => "2.3";

    public string DataDirectory { get; set; } = "/tmp/pcboost-data";

    public string LogDirectory { get; set; } = "/tmp/pcboost-logs";
}
