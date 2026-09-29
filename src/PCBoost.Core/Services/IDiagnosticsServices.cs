using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Settings;

namespace PCBoost.Core.Services;

/// <summary>Analyse complète du PC (§8). Lecture seule.</summary>
public interface ISystemAnalyzer
{
    Task<SystemAnalysisReport> AnalyzeAsync(AnalysisOptions options, IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Dernière analyse complète effectuée pendant cette session, si disponible.</summary>
    SystemAnalysisReport? LastReport { get; }

    event EventHandler<SystemAnalysisReport>? AnalysisCompleted;
}

/// <summary>Moteur de règles configurables (§26).</summary>
public interface IHealthRulesEngine
{
    IReadOnlyList<HealthFinding> Evaluate(SystemAnalysisReport report, HealthThresholds thresholds);
}

/// <summary>Score explicable (§7, §40) : chaque point provient d'un facteur documenté.</summary>
public interface IPerformanceScoreCalculator
{
    PerformanceScore Calculate(SystemAnalysisReport report, HealthThresholds thresholds);
}

public interface IPerformanceRecommendationEngine
{
    IReadOnlyList<Recommendation> GetRecommendations(SystemAnalysisReport report, IReadOnlyList<HealthFinding> findings, IReadOnlyCollection<string> dismissedIds);
}

public interface IHardwareProfileClassifier
{
    HardwareProfile Classify(CpuInfo cpu, MemoryInfo memory, IReadOnlyList<StorageDrive> drives, IReadOnlyList<GpuInfo> gpus);
}

/// <summary>« Pourquoi mon PC est lent ? » (§74).</summary>
public interface ISlowPcDiagnosticService
{
    Task<SlownessDiagnosis> DiagnoseAsync(CancellationToken cancellationToken = default);
}

/// <summary>Conseils matériels factuels, sans vente (§24).</summary>
public interface IHardwareAdvisor
{
    IReadOnlyList<HardwareAdvice> GetAdvice(SystemAnalysisReport report, IReadOnlyList<PerformanceSnapshot> recentHistory);
}

/// <summary>Répartition de l'espace disque (§25). Lecture seule.</summary>
public interface IStorageAnalyzer
{
    Task<StorageBreakdown?> AnalyzeSystemDriveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Surveillance temps réel adaptative (§22, §47).</summary>
public interface IPerformanceMonitor : IDisposable
{
    MonitoringMode Mode { get; }

    bool IsRunning { get; }

    SystemMetricsSample? Latest { get; }

    TemperatureReadings Temperatures { get; }

    TimeSpan CurrentInterval { get; }

    void Start();

    void Stop();

    void SetMode(MonitoringMode mode);

    IReadOnlyList<SystemMetricsSample> GetHistory(TimeSpan window);

    MetricStatistics GetStatistics(MetricKind metric, TimeSpan window);

    event EventHandler<SystemMetricsSample>? SampleAvailable;

    event EventHandler? ModeChanged;
}

/// <summary>Enregistre périodiquement des instantanés pour les graphiques historiques (§66).</summary>
public interface IPerformanceHistoryService
{
    Task<IReadOnlyList<PerformanceSnapshot>> GetHistoryAsync(TimeSpan window, CancellationToken cancellationToken = default);
}
