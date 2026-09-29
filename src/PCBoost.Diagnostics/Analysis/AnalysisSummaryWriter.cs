using System.Text;
using System.Text.Json;
using PCBoost.Core.Models.Analysis;

namespace PCBoost.Diagnostics.Analysis;

/// <summary>
/// Résumé JSON compact d'une analyse pour l'historique. Uniquement des mesures agrégées et des identifiants de règles :
/// aucun nom de processus, chemin, nom d'utilisateur ou nom de fichier. Sans réflexion (compatible découpage/AOT).
/// </summary>
internal static class AnalysisSummaryWriter
{
    public const int Version = 1;

    public static string Write(SystemAnalysisReport report, PerformanceScore score, IReadOnlyList<HealthFinding> findings)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteNumber("score", score.Value);
            WriteString(w, "tier", report.HardwareProfile?.Tier.ToString());

            w.WriteStartObject("os");
            WriteNumber(w, "build", AnalysisMetrics.IsOsKnown(report) ? report.Os.BuildNumber : null);
            w.WriteBoolean("win11", report.Os.IsWindows11);
            w.WriteEndObject();

            w.WriteStartObject("ram");
            WriteNumber(w, "totalGiB", report.Memory.TotalBytes > 0 ? Math.Round(AnalysisMetrics.TotalMemoryGiB(report), 1) : null);
            WriteNumber(w, "usedPct", Round(AnalysisMetrics.MemoryUsedPercent(report)));
            w.WriteEndObject();

            w.WriteStartObject("cpu");
            WriteNumber(w, "cores", report.Cpu.PhysicalCores > 0 ? report.Cpu.PhysicalCores : null);
            WriteNumber(w, "avgPct", Round(AnalysisMetrics.CpuAveragePercent(report)));
            WriteNumber(w, "maxPct", Round(AnalysisMetrics.CpuMaxPercent(report)));
            w.WriteEndObject();

            w.WriteStartObject("disk");
            WriteNumber(w, "sysFreePct", Round(AnalysisMetrics.SystemDriveFreePercent(report)));
            WriteString(w, "sysMedia", report.SystemDrive?.MediaType.ToString());
            WriteNumber(w, "activePct", Round(AnalysisMetrics.DiskActivePercent(report)));
            w.WriteEndObject();

            w.WriteStartObject("startup");
            WriteNumber(w, "enabled", AnalysisMetrics.EnabledStartupCount(report));
            w.WriteNumber("total", report.StartupEntries.Count);
            w.WriteEndObject();

            WriteNumber(w, "cleanableBytes", AnalysisMetrics.CleanableBytes(report));

            w.WriteStartObject("processes");
            w.WriteNumber("running", report.RunningProcessCount);
            WriteNumber(w, "background", AnalysisMetrics.BackgroundProcessCount(report));
            w.WriteEndObject();

            WriteNumber(w, "uptimeDays", Round(AnalysisMetrics.UptimeDays(report)));

            w.WriteStartArray("findings");
            foreach (var f in findings) w.WriteStringValue(f.RuleId);
            w.WriteEndArray();

            w.WriteStartObject("factors");
            foreach (var factor in score.Factors)
            {
                if (factor.Status == FactorStatus.Unknown) w.WriteNull(factor.Id);
                else w.WriteNumber(factor.Id, factor.Points);
            }
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static double? Round(double? value) => value is double v ? Math.Round(v, 1) : null;

    private static void WriteNumber(Utf8JsonWriter w, string name, double? value)
    {
        if (value is double v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    private static void WriteNumber(Utf8JsonWriter w, string name, long? value)
    {
        if (value is long v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    private static void WriteNumber(Utf8JsonWriter w, string name, int? value)
    {
        if (value is int v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    private static void WriteString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }
}
