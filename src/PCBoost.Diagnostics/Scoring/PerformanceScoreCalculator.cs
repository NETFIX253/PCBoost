using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;

namespace PCBoost.Diagnostics.Scoring;

/// <summary>Identifiants stables des facteurs du score.</summary>
public static class ScoreFactorIds
{
    public const string Memory = "score.memory";
    public const string Cpu = "score.cpu";
    public const string SystemDriveFreeSpace = "score.storage";
    public const string Startup = "score.startup";
    public const string Cleanable = "score.cleanable";
    public const string DiskActivity = "score.disk";
    public const string Uptime = "score.uptime";
}

/// <summary>
/// Score explicable sur 100 (§7, §40), voir docs/SCORING.md. Chaque facteur donne des points par interpolation linéaire
/// entre une borne « bonne » (tous les points) et une borne « mauvaise » (0 point), arrondis à l'entier.
/// Un facteur non mesurable est <see cref="FactorStatus.Unknown"/>, exclu, et le score est remis à l'échelle :
/// <c>score = arrondi(100 × Σ points mesurés / Σ points maximum mesurés)</c>. Quand tout est mesuré, le score est la somme des points.
/// Les bornes sont fixes (le score reste comparable dans le temps) : les seuils personnalisés n'agissent que sur les constats.
/// </summary>
public sealed class PerformanceScoreCalculator : IPerformanceScoreCalculator
{
    public const int MemoryMaxPoints = 25;
    public const int CpuMaxPoints = 15;
    public const int StorageMaxPoints = 20;
    public const int StartupMaxPoints = 15;
    public const int CleanableMaxPoints = 10;
    public const int DiskMaxPoints = 10;
    public const int UptimeMaxPoints = 5;

    // Bornes (bonne → mauvaise).
    public const double MemoryGoodPercent = 60, MemoryBadPercent = 95;
    public const double CpuGoodPercent = 30, CpuBadPercent = 90;
    public const double StorageGoodFreePercent = 25, StorageBadFreePercent = 5;
    public const int StartupGoodCount = 5, StartupBadCount = 20;
    public const long CleanableGoodBytes = 500 * ByteSize.MiB, CleanableBadBytes = 10 * ByteSize.GiB;
    public const double DiskGoodPercent = 30, DiskBadPercent = 95;
    public const double UptimeGoodDays = 3, UptimeBadDays = 14;

    private readonly IClock _clock;

    public PerformanceScoreCalculator(IClock clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public PerformanceScore Calculate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(report);

        var factors = new List<ScoreFactor>(7)
        {
            MemoryFactor(report),
            CpuFactor(report),
            StorageFactor(report),
            StartupFactor(report),
            CleanableFactor(report),
            DiskFactor(report),
            UptimeFactor(report),
        };

        var measuredMax = 0;
        var measuredPoints = 0;
        foreach (var f in factors)
        {
            if (f.Status == FactorStatus.Unknown) continue;
            measuredMax += f.MaxPoints;
            measuredPoints += f.Points;
        }

        // Aucun facteur mesurable : 0, avec tous les facteurs « Unknown » (l'interface doit afficher « Non disponible »).
        var value = measuredMax == 0 ? 0 : (int)Math.Round(measuredPoints * 100d / measuredMax, MidpointRounding.AwayFromZero);
        return new PerformanceScore(Math.Clamp(value, 0, 100), factors, _clock.UtcNow);
    }

    /// <summary>Statut d'un facteur mesuré : Good ≥ 80 % des points, Fair ≥ 40 %, sinon Poor.</summary>
    public static FactorStatus StatusFor(int points, int maxPoints)
    {
        if (maxPoints <= 0) return FactorStatus.Unknown;
        if (points * 10 >= maxPoints * 8) return FactorStatus.Good;
        if (points * 10 >= maxPoints * 4) return FactorStatus.Fair;
        return FactorStatus.Poor;
    }

    private static ScoreFactor MemoryFactor(SystemAnalysisReport report)
    {
        if (AnalysisMetrics.MemoryUsedPercent(report) is not double used)
            return Unknown(ScoreFactorIds.Memory, DiagText.ScoreMemoryLabel, MemoryMaxPoints, NavigationTargets.Processes);
        var totalGiB = AnalysisMetrics.TotalMemoryGiB(report);
        return Measured(ScoreFactorIds.Memory, DiagText.ScoreMemoryLabel, MemoryMaxPoints,
            LinearScale.Fraction(used, MemoryGoodPercent, MemoryBadPercent),
            TextRef.Of(DiagText.ScoreMemoryExplanation, used, totalGiB * used / 100, totalGiB), NavigationTargets.Processes);
    }

    private static ScoreFactor CpuFactor(SystemAnalysisReport report)
    {
        if (AnalysisMetrics.CpuAveragePercent(report) is not double cpu)
            return Unknown(ScoreFactorIds.Cpu, DiagText.ScoreCpuLabel, CpuMaxPoints, NavigationTargets.Processes);
        return Measured(ScoreFactorIds.Cpu, DiagText.ScoreCpuLabel, CpuMaxPoints,
            LinearScale.Fraction(cpu, CpuGoodPercent, CpuBadPercent),
            TextRef.Of(DiagText.ScoreCpuExplanation, cpu, report.Load.SamplingDuration.TotalSeconds), NavigationTargets.Processes);
    }

    private static ScoreFactor StorageFactor(SystemAnalysisReport report)
    {
        if (AnalysisMetrics.MeasuredSystemDrive(report) is not { } drive)
            return Unknown(ScoreFactorIds.SystemDriveFreeSpace, DiagText.ScoreStorageLabel, StorageMaxPoints, NavigationTargets.Storage);
        var free = Math.Clamp(drive.FreePercent, 0, 100);
        return Measured(ScoreFactorIds.SystemDriveFreeSpace, DiagText.ScoreStorageLabel, StorageMaxPoints,
            LinearScale.Fraction(free, StorageGoodFreePercent, StorageBadFreePercent),
            TextRef.Of(DiagText.ScoreStorageExplanation, drive.FreeBytes / (double)ByteSize.GiB, drive.RootPath, free), NavigationTargets.Storage);
    }

    private static ScoreFactor StartupFactor(SystemAnalysisReport report)
    {
        if (AnalysisMetrics.EnabledStartupCount(report) is not int count)
            return Unknown(ScoreFactorIds.Startup, DiagText.ScoreStartupLabel, StartupMaxPoints, NavigationTargets.Startup);
        return Measured(ScoreFactorIds.Startup, DiagText.ScoreStartupLabel, StartupMaxPoints,
            LinearScale.Fraction(count, StartupGoodCount, StartupBadCount),
            TextRef.Of(DiagText.ScoreStartupExplanation, count), NavigationTargets.Startup);
    }

    private static ScoreFactor CleanableFactor(SystemAnalysisReport report)
    {
        if (AnalysisMetrics.CleanableBytes(report) is not long bytes)
            return Unknown(ScoreFactorIds.Cleanable, DiagText.ScoreCleanableLabel, CleanableMaxPoints, NavigationTargets.Cleanup);
        return Measured(ScoreFactorIds.Cleanable, DiagText.ScoreCleanableLabel, CleanableMaxPoints,
            LinearScale.Fraction(bytes, CleanableGoodBytes, CleanableBadBytes),
            DiagText.Size(DiagText.ScoreCleanableExplanationGb, DiagText.ScoreCleanableExplanationMb, bytes), NavigationTargets.Cleanup);
    }

    private static ScoreFactor DiskFactor(SystemAnalysisReport report)
    {
        if (AnalysisMetrics.DiskActivePercent(report) is not double active)
            return Unknown(ScoreFactorIds.DiskActivity, DiagText.ScoreDiskLabel, DiskMaxPoints, NavigationTargets.Performance);
        return Measured(ScoreFactorIds.DiskActivity, DiagText.ScoreDiskLabel, DiskMaxPoints,
            LinearScale.Fraction(active, DiskGoodPercent, DiskBadPercent),
            TextRef.Of(DiagText.ScoreDiskExplanation, active), NavigationTargets.Performance);
    }

    private static ScoreFactor UptimeFactor(SystemAnalysisReport report)
    {
        if (AnalysisMetrics.UptimeDays(report) is not double days)
            return Unknown(ScoreFactorIds.Uptime, DiagText.ScoreUptimeLabel, UptimeMaxPoints, NavigationTargets.Diagnosis);
        var explanation = days >= 2
            ? TextRef.Of(DiagText.ScoreUptimeExplanationDays, Math.Floor(days))
            : TextRef.Of(DiagText.ScoreUptimeExplanationRecent, Math.Floor(days * 24));
        return Measured(ScoreFactorIds.Uptime, DiagText.ScoreUptimeLabel, UptimeMaxPoints,
            LinearScale.Fraction(days, UptimeGoodDays, UptimeBadDays), explanation, NavigationTargets.Diagnosis);
    }

    private static ScoreFactor Measured(string id, string labelKey, int maxPoints, double fraction, TextRef explanation, string navigationTarget)
    {
        var points = LinearScale.Points(maxPoints, fraction);
        return new ScoreFactor(id, TextRef.Of(labelKey), explanation, maxPoints, points, StatusFor(points, maxPoints), navigationTarget);
    }

    private static ScoreFactor Unknown(string id, string labelKey, int maxPoints, string navigationTarget)
        => new(id, TextRef.Of(labelKey), TextRef.Of(DiagText.ScoreUnknownExplanation), maxPoints, 0, FactorStatus.Unknown, navigationTarget);
}
