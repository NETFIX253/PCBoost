using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Models.SystemInfo;

namespace PCBoost.Core.Models.Analysis;

public enum AnalysisStage { System = 0, Startup, Storage, Processes, Results }

public sealed record AnalysisProgress(AnalysisStage Stage, double Percent);

public sealed record AnalysisOptions(
    bool IncludeCleanupScan = true,
    bool IncludeStartup = true,
    bool IncludeProcesses = true,
    /// <summary>Durée d'échantillonnage de la charge (moyenne CPU/disque).</summary>
    TimeSpan? LoadSamplingDuration = null);

/// <summary>Usage d'un processus au moment de l'analyse (top consommateurs).</summary>
public sealed record ProcessUsage(int ProcessId, string Name, string? ExecutablePath, double CpuPercent, long MemoryBytes, bool IsCurrentUser);

/// <summary>Charge moyenne observée pendant l'échantillonnage de l'analyse.</summary>
public sealed record LoadObservation(
    double CpuAveragePercent,
    double CpuMaxPercent,
    double MemoryUsedPercent,
    double? DiskActiveAveragePercent,
    double? GpuAveragePercent,
    TimeSpan SamplingDuration,
    int SampleCount);

public sealed record SystemAnalysisReport
{
    public required DateTimeOffset Timestamp { get; init; }
    public required OsInfo Os { get; init; }
    public required CpuInfo Cpu { get; init; }
    public required MemoryInfo Memory { get; init; }
    public required IReadOnlyList<GpuInfo> Gpus { get; init; }
    public required IReadOnlyList<StorageDrive> Drives { get; init; }
    public required LoadObservation Load { get; init; }
    public required TemperatureReadings Temperatures { get; init; }
    public required PowerStatus Power { get; init; }
    public PowerScheme? ActivePowerScheme { get; init; }
    public int? InstalledProgramCount { get; init; }
    public IReadOnlyList<StartupEntry> StartupEntries { get; init; } = [];
    public int RunningProcessCount { get; init; }
    public int BackgroundProcessCount { get; init; }
    public IReadOnlyList<ProcessUsage> TopMemoryProcesses { get; init; } = [];
    public IReadOnlyList<ProcessUsage> TopCpuProcesses { get; init; } = [];
    /// <summary>Octets récupérables par les catégories SAFE (null si non analysé).</summary>
    public long? CleanableBytes { get; init; }
    public HardwareProfile? HardwareProfile { get; init; }
    /// <summary>Santé du matériel (disques, batterie, périphériques, limitation thermique), si relevée.</summary>
    public HardwareHealthReport? Health { get; init; }

    public StorageDrive? SystemDrive => Drives.FirstOrDefault(d => d.IsSystemDrive);
    public int EnabledStartupCount => StartupEntries.Count(e => e.IsEnabled);
}

public sealed record HealthFinding(
    string RuleId,
    Severity Severity,
    TextRef Title,
    TextRef Detail,
    double? ObservedValue,
    double? Threshold,
    string Category);

public enum FactorStatus { Good = 0, Fair, Poor, Unknown }

/// <summary>Facteur explicable du score (§40).</summary>
public sealed record ScoreFactor(
    string Id,
    TextRef Label,
    TextRef Explanation,
    int MaxPoints,
    int Points,
    FactorStatus Status,
    // Page de l’application permettant d’agir (clé de navigation), si applicable.
    string? NavigationTarget);

public sealed record PerformanceScore(int Value, IReadOnlyList<ScoreFactor> Factors, DateTimeOffset ComputedAt)
{
    public int ImprovablePoints => Factors.Where(f => f.Status is FactorStatus.Fair or FactorStatus.Poor).Count();
}

public enum RecommendationKind
{
    /// <summary>Action concrète recommandée par PCBoost.</summary>
    Recommendation = 0,
    /// <summary>Piste possible, sans certitude (matériel, usage).</summary>
    Possibility = 1,
}

public sealed record RecommendationAction(TextRef Label, string NavigationTarget, string? OptimizationId = null);

public sealed record Recommendation(
    string Id,
    TextRef Title,
    TextRef Description,
    TextRef Reason,
    ImpactLevel Impact,
    ConfidenceLevel Confidence,
    RiskLevel Risk,
    RecommendationKind Kind,
    bool Reversible,
    string Category,
    RecommendationAction? Action);

public enum HardwareTier { Unknown = 0, LegacyLowResource, Entry, LowEnd, MidRange, HighEnd }

public sealed record HardwareProfile(
    HardwareTier Tier,
    IReadOnlyList<TextRef> Reasons,
    bool LowMemory,
    bool SystemDriveIsHdd,
    bool FewCores,
    bool LowSystemDriveSpace,
    bool HasDedicatedGpu);

public sealed record SlownessFactor(
    string Id,
    TextRef Title,
    TextRef Evidence,
    TextRef Why,
    TextRef WhatToDo,
    ImpactLevel Impact,
    ConfidenceLevel Confidence,
    RecommendationAction? Action);

public sealed record SlownessDiagnosis(DateTimeOffset Timestamp, IReadOnlyList<SlownessFactor> Factors, bool NoSignificantFactor);

public sealed record HardwareAdvice(
    string Id,
    TextRef Observation,
    TextRef Suggestion,
    ConfidenceLevel Confidence,
    string Component);

public enum StorageCategoryKind { Documents = 0, Downloads, Desktop, Pictures, Videos, Music, Applications, Temporary, System, Other }

public sealed record StorageCategoryUsage(StorageCategoryKind Kind, long Bytes, bool Measured, string? Path);

public sealed record StorageBreakdown(
    StorageDrive Drive,
    IReadOnlyList<StorageCategoryUsage> Categories,
    IReadOnlyList<LargeFolder> LargestFolders,
    DateTimeOffset Timestamp);

public sealed record LargeFolder(string Path, long Bytes);

/// <summary>Enregistrement d'une analyse pour l'historique.</summary>
public sealed record ScanRecord(Guid Id, DateTimeOffset Timestamp, int Score, int FindingCount, string SummaryJson);
