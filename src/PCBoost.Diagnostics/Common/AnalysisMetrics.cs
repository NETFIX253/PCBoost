using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.SystemInfo;

namespace PCBoost.Diagnostics;

/// <summary>
/// Lecture normalisée des mesures d'un rapport d'analyse. Chaque méthode renvoie <c>null</c> quand la mesure
/// n'a pas pu être obtenue : les moteurs (score, règles, diagnostic) n'utilisent jamais de valeur supposée.
/// </summary>
internal static class AnalysisMetrics
{
    /// <summary>Utilisation mémoire (%) : moyenne de l'échantillonnage si disponible, sinon instantané de l'analyse.</summary>
    public static double? MemoryUsedPercent(SystemAnalysisReport report)
    {
        if (report.Memory.TotalBytes <= 0) return null;
        var value = report.Load.SampleCount > 0 ? report.Load.MemoryUsedPercent : report.Memory.UsedPercent;
        return Percent(value);
    }

    public static double TotalMemoryGiB(SystemAnalysisReport report) => report.Memory.TotalBytes / (double)ByteSize.GiB;

    /// <summary>Charge CPU moyenne pendant l'échantillonnage (null si aucun échantillon n'a pu être pris).</summary>
    public static double? CpuAveragePercent(SystemAnalysisReport report)
        => report.Load.SampleCount > 0 ? Percent(report.Load.CpuAveragePercent) : null;

    public static double? CpuMaxPercent(SystemAnalysisReport report)
        => report.Load.SampleCount > 0 ? Percent(report.Load.CpuMaxPercent) : null;

    /// <summary>Activité disque moyenne (null si le compteur est indisponible).</summary>
    public static double? DiskActivePercent(SystemAnalysisReport report)
        => report.Load.SampleCount > 0 && report.Load.DiskActiveAveragePercent is double d ? Percent(d) : null;

    public static StorageDrive? MeasuredSystemDrive(SystemAnalysisReport report)
        => report.SystemDrive is { TotalBytes: > 0 } drive ? drive : null;

    public static double? SystemDriveFreePercent(SystemAnalysisReport report)
        => MeasuredSystemDrive(report) is { } drive ? Percent(drive.FreePercent) : null;

    /// <summary>
    /// Nombre d'applications activées au démarrage. Une liste vide est traitée comme « non mesuré » : le rapport
    /// ne distingue pas « aucune entrée » de « énumération impossible », et Windows en liste presque toujours au moins une.
    /// </summary>
    public static int? EnabledStartupCount(SystemAnalysisReport report)
        => report.StartupEntries.Count > 0 ? report.EnabledStartupCount : null;

    /// <summary>Durée de fonctionnement (jours) ; null si elle n'a pas été lue (valeur nulle).</summary>
    public static double? UptimeDays(SystemAnalysisReport report)
        => report.Os.Uptime > TimeSpan.Zero ? report.Os.Uptime.TotalDays : null;

    public static long? CleanableBytes(SystemAnalysisReport report)
        => report.CleanableBytes is long b && b >= 0 ? b : null;

    /// <summary>Processus en arrière-plan ; null si la liste des processus n'a pas pu être lue.</summary>
    public static int? BackgroundProcessCount(SystemAnalysisReport report)
        => report.RunningProcessCount > 0 ? report.BackgroundProcessCount : null;

    public static bool IsOsKnown(SystemAnalysisReport report) => report.Os.BuildNumber > 0;

    public static bool IsPowerSaverOnAc(SystemAnalysisReport report)
        => report.ActivePowerScheme?.Id == PowerScheme.PowerSaver && report.Power.Source == PowerSource.AC;

    public static bool SystemDriveIsHdd(SystemAnalysisReport report)
        => report.SystemDrive?.MediaType == StorageMediaType.Hdd;

    public static double? Temperature(SensorReading reading)
        => reading.HasValue && double.IsFinite(reading.Value!.Value) ? reading.Value : null;

    /// <summary>
    /// Capacité mémoire « nominale » en Go : la mémoire utilisable signalée par Windows est toujours un peu inférieure
    /// à la mémoire installée (réservations matérielles), on arrondit donc au Go supérieur (7,8 Gio → 8 Go).
    /// </summary>
    public static int NominalGiB(long bytes)
        => bytes <= 0 ? 0 : (int)Math.Ceiling(Math.Round(bytes / (double)ByteSize.GiB, 3));

    private static double? Percent(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : null;
}
