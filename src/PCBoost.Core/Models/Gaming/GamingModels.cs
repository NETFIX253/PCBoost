using PCBoost.Core.Common;

namespace PCBoost.Core.Models.Gaming;

public enum GameSource
{
    Unknown = 0,
    Steam,
    EpicGames,
    Xbox,
    BattleNet,
    Riot,
    Ubisoft,
    EA,
    Gog,
    /// <summary>Jeux reconnus par Windows (Game Bar / GameConfigStore).</summary>
    WindowsGameConfig,
    /// <summary>Base de signatures intégrée (nom d'exécutable connu).</summary>
    Signature,
    Custom,
}

public sealed record GameInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required GameSource Source { get; init; }
    public string? InstallDirectory { get; init; }
    public string? ExecutablePath { get; init; }
    /// <summary>Noms d'exécutables connus (en minuscules, avec .exe).</summary>
    public IReadOnlyList<string> ExecutableNames { get; init; } = [];
}

public sealed record DetectedGameProcess(GameInfo Game, int ProcessId, string? ExecutablePath, DateTimeOffset DetectedAt);

public enum GamingState { Inactive = 0, Activating, Active, Restoring }

public enum GamingSessionStatus { Active = 0, Restored, RestoreFailed, Interrupted, Dismissed }

public sealed record ActiveGamingOptimization(string OptimizationId, TextRef Label, bool Applied, TextRef? Detail);

public sealed record GamingSession
{
    public required Guid Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public GamingSessionStatus Status { get; init; }
    public string? GameId { get; init; }
    public string? GameName { get; init; }
    public int? ProcessId { get; init; }
    /// <summary>Session de restauration contenant les modifications du mode Gaming.</summary>
    public Guid? OptimizationSessionId { get; init; }
    public IReadOnlyList<ActiveGamingOptimization> Optimizations { get; init; } = [];
    public FrameStats? FrameStats { get; init; }
}

/// <summary>
/// Statistiques d'images calculées à partir des horodatages de présentation (ETW).
/// Toutes les valeurs sont null si non mesurées.
/// </summary>
public sealed record FrameStats(
    double? AverageFps,
    double? OnePercentLowFps,
    double? PointOnePercentLowFps,
    double? AverageFrameTimeMs,
    double? P99FrameTimeMs,
    int FrameCount,
    TimeSpan Duration)
{
    public static FrameStats Empty { get; } = new(null, null, null, null, null, 0, TimeSpan.Zero);
    public bool HasData => FrameCount > 1 && AverageFps.HasValue;
}

public enum FrameCaptureAvailability { Available = 0, RequiresElevation, NotSupported, Disabled }

/// <summary>Lot d'horodatages de présentation (millisecondes, horloge monotone) pour un processus.</summary>
public sealed record FrameBatch(int ProcessId, IReadOnlyList<double> PresentTimestampsMs);

public enum BenchmarkPhase { Before = 0, After = 1, Standalone = 2 }

public sealed record BenchmarkRun
{
    public required Guid Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required BenchmarkPhase Phase { get; init; }
    public required TimeSpan Duration { get; init; }
    public string? Label { get; init; }
    public string? GameName { get; init; }
    public Guid? PairedRunId { get; init; }
    public double CpuAveragePercent { get; init; }
    public double CpuMaxPercent { get; init; }
    public double? GpuAveragePercent { get; init; }
    public double? GpuMaxPercent { get; init; }
    public double RamAveragePercent { get; init; }
    public double? DiskActiveAveragePercent { get; init; }
    public FrameStats Frames { get; init; } = FrameStats.Empty;
    public int SampleCount { get; init; }
}

/// <summary>Différence mesurée entre deux benchmarks. Null = non comparable (au moins une mesure absente).</summary>
public sealed record BenchmarkDelta(string Metric, double? Before, double? After, double? Difference, bool HigherIsBetter);

public sealed record BenchmarkComparison(BenchmarkRun Before, BenchmarkRun After, IReadOnlyList<BenchmarkDelta> Deltas);

public sealed record GamingActivationReport(
    Guid? SessionId,
    OperationResult Outcome,
    DetectedGameProcess? Game,
    IReadOnlyList<ActiveGamingOptimization> Optimizations,
    FrameCaptureAvailability FrameCapture);

public sealed record GamingRestoreReport(Guid? SessionId, int Restored, int Failed, IReadOnlyList<TextRef> Messages)
{
    public bool Success => Failed == 0;
}

/// <summary>Métriques en direct du mode Gaming.</summary>
public sealed record GamingLiveMetrics(
    FrameStats Frames,
    double CpuPercent,
    double? GpuPercent,
    double MemoryUsedPercent,
    long? GpuMemoryUsedBytes,
    SensorReading CpuTemperature,
    SensorReading GpuTemperature,
    FrameCaptureAvailability FrameCapture);

public sealed record GameSettingCheck(string Id, TextRef Label, TextRef Detail, bool IsRecommendedState, bool CanFixAutomatically);
