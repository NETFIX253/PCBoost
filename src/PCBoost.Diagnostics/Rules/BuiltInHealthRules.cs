using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Settings;

namespace PCBoost.Diagnostics.Rules;

// Barème commun des règles intégrées :
//  seuil « warning » atteint → Severity.Medium ; seuil « critical » atteint → Severity.High ;
//  température au-delà du seuil et build non pris en charge → High ;
//  uptime, fichiers récupérables, processus en arrière-plan → Low ; plan Économie d'énergie sur secteur → Info.
// Severity.Critical n'est jamais émis par une règle intégrée (réservé aux règles ajoutées).

/// <summary>Mémoire utilisée (%) au-delà des seuils RAM.</summary>
public sealed class MemoryUsageRule : IHealthRule
{
    public string Id => HealthRuleIds.Memory;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (AnalysisMetrics.MemoryUsedPercent(report) is not double used) return null;
        var totalGiB = AnalysisMetrics.TotalMemoryGiB(report);
        var detail = TextRef.Of(DiagText.RuleMemoryDetail, used, totalGiB * used / 100, totalGiB);

        if (used >= thresholds.RamCriticalPercent)
            return new(Id, Severity.High, TextRef.Of(DiagText.RuleMemoryCriticalTitle), detail, used, thresholds.RamCriticalPercent, HealthCategories.Memory);
        if (used >= thresholds.RamWarningPercent)
            return new(Id, Severity.Medium, TextRef.Of(DiagText.RuleMemoryWarningTitle), detail, used, thresholds.RamWarningPercent, HealthCategories.Memory);
        return null;
    }
}

/// <summary>Espace libre (%) du disque système sous les seuils.</summary>
public sealed class SystemDriveFreeSpaceRule : IHealthRule
{
    public string Id => HealthRuleIds.SystemDriveFreeSpace;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (AnalysisMetrics.MeasuredSystemDrive(report) is not { } drive) return null;
        var free = Math.Clamp(drive.FreePercent, 0, 100);
        var detail = TextRef.Of(DiagText.RuleStorageDetail, drive.FreeBytes / (double)ByteSize.GiB, drive.RootPath, free);

        if (free <= thresholds.SystemDriveFreeCriticalPercent)
            return new(Id, Severity.High, TextRef.Of(DiagText.RuleStorageCriticalTitle), detail, free, thresholds.SystemDriveFreeCriticalPercent, HealthCategories.Storage);
        if (free <= thresholds.SystemDriveFreeWarningPercent)
            return new(Id, Severity.Medium, TextRef.Of(DiagText.RuleStorageWarningTitle), detail, free, thresholds.SystemDriveFreeWarningPercent, HealthCategories.Storage);
        return null;
    }
}

/// <summary>Nombre d'applications activées au démarrage.</summary>
public sealed class StartupCountRule : IHealthRule
{
    public string Id => HealthRuleIds.StartupCount;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (AnalysisMetrics.EnabledStartupCount(report) is not int count) return null;
        var detail = TextRef.Of(DiagText.RuleStartupDetail, count);

        if (count >= thresholds.StartupCriticalCount)
            return new(Id, Severity.High, TextRef.Of(DiagText.RuleStartupCriticalTitle), detail, count, thresholds.StartupCriticalCount, HealthCategories.Startup);
        if (count >= thresholds.StartupWarningCount)
            return new(Id, Severity.Medium, TextRef.Of(DiagText.RuleStartupWarningTitle), detail, count, thresholds.StartupWarningCount, HealthCategories.Startup);
        return null;
    }
}

/// <summary>Charge CPU moyenne pendant l'échantillonnage de l'analyse.</summary>
public sealed class SustainedCpuRule : IHealthRule
{
    public string Id => HealthRuleIds.CpuSustained;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (AnalysisMetrics.CpuAveragePercent(report) is not double average) return null;
        var detail = TextRef.Of(DiagText.RuleCpuDetail, average, report.Load.SamplingDuration.TotalSeconds, AnalysisMetrics.CpuMaxPercent(report) ?? average);

        if (average >= thresholds.CpuSustainedCriticalPercent)
            return new(Id, Severity.High, TextRef.Of(DiagText.RuleCpuCriticalTitle), detail, average, thresholds.CpuSustainedCriticalPercent, HealthCategories.Cpu);
        if (average >= thresholds.CpuSustainedWarningPercent)
            return new(Id, Severity.Medium, TextRef.Of(DiagText.RuleCpuWarningTitle), detail, average, thresholds.CpuSustainedWarningPercent, HealthCategories.Cpu);
        return null;
    }
}

/// <summary>Activité disque moyenne pendant l'échantillonnage.</summary>
public sealed class DiskActivityRule : IHealthRule
{
    public string Id => HealthRuleIds.DiskActive;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (AnalysisMetrics.DiskActivePercent(report) is not double active) return null;
        var detail = TextRef.Of(DiagText.RuleDiskDetail, active);

        if (active >= thresholds.DiskActiveCriticalPercent)
            return new(Id, Severity.High, TextRef.Of(DiagText.RuleDiskCriticalTitle), detail, active, thresholds.DiskActiveCriticalPercent, HealthCategories.Disk);
        if (active >= thresholds.DiskActiveWarningPercent)
            return new(Id, Severity.Medium, TextRef.Of(DiagText.RuleDiskWarningTitle), detail, active, thresholds.DiskActiveWarningPercent, HealthCategories.Disk);
        return null;
    }
}

/// <summary>Base des règles de température : un constat seulement si le capteur a fourni une valeur.</summary>
public abstract class TemperatureRuleBase : IHealthRule
{
    public abstract string Id { get; }

    protected abstract SensorReading Select(TemperatureReadings readings);

    protected abstract double Threshold(HealthThresholds thresholds);

    protected abstract string TitleKey { get; }

    protected abstract string DetailKey { get; }

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (AnalysisMetrics.Temperature(Select(report.Temperatures)) is not double celsius) return null;
        var limit = Threshold(thresholds);
        if (celsius < limit) return null;
        return new(Id, Severity.High, TextRef.Of(TitleKey), TextRef.Of(DetailKey, celsius, limit), celsius, limit, HealthCategories.Thermal);
    }
}

public sealed class CpuTemperatureRule : TemperatureRuleBase
{
    public override string Id => HealthRuleIds.CpuTemperature;
    protected override SensorReading Select(TemperatureReadings readings) => readings.Cpu;
    protected override double Threshold(HealthThresholds thresholds) => thresholds.CpuTemperatureWarningC;
    protected override string TitleKey => DiagText.RuleCpuTemperatureTitle;
    protected override string DetailKey => DiagText.RuleCpuTemperatureDetail;
}

public sealed class GpuTemperatureRule : TemperatureRuleBase
{
    public override string Id => HealthRuleIds.GpuTemperature;
    protected override SensorReading Select(TemperatureReadings readings) => readings.Gpu;
    protected override double Threshold(HealthThresholds thresholds) => thresholds.GpuTemperatureWarningC;
    protected override string TitleKey => DiagText.RuleGpuTemperatureTitle;
    protected override string DetailKey => DiagText.RuleGpuTemperatureDetail;
}

public sealed class StorageTemperatureRule : TemperatureRuleBase
{
    public override string Id => HealthRuleIds.StorageTemperature;
    protected override SensorReading Select(TemperatureReadings readings) => readings.Storage;
    protected override double Threshold(HealthThresholds thresholds) => thresholds.StorageTemperatureWarningC;
    protected override string TitleKey => DiagText.RuleStorageTemperatureTitle;
    protected override string DetailKey => DiagText.RuleStorageTemperatureDetail;
}

/// <summary>Durée depuis le dernier redémarrage.</summary>
public sealed class UptimeRule : IHealthRule
{
    public string Id => HealthRuleIds.Uptime;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (AnalysisMetrics.UptimeDays(report) is not double days || days < thresholds.UptimeWarningDays) return null;
        return new(Id, Severity.Low, TextRef.Of(DiagText.RuleUptimeTitle), TextRef.Of(DiagText.RuleUptimeDetail, days),
            days, thresholds.UptimeWarningDays, HealthCategories.System);
    }
}

/// <summary>Octets récupérables par les catégories de nettoyage SAFE.</summary>
public sealed class CleanableFilesRule : IHealthRule
{
    public string Id => HealthRuleIds.Cleanable;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (AnalysisMetrics.CleanableBytes(report) is not long bytes || bytes <= 0 || bytes < thresholds.CleanableWarningBytes) return null;
        return new(Id, Severity.Low, TextRef.Of(DiagText.RuleCleanableTitle),
            DiagText.Size(DiagText.RuleCleanableDetailGb, DiagText.RuleCleanableDetailMb, bytes),
            bytes, thresholds.CleanableWarningBytes, HealthCategories.Cleanup);
    }
}

/// <summary>Processus de l'utilisateur, sans fenêtre, dans sa session.</summary>
public sealed class BackgroundProcessesRule : IHealthRule
{
    public string Id => HealthRuleIds.BackgroundProcesses;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (AnalysisMetrics.BackgroundProcessCount(report) is not int count || count < thresholds.BackgroundProcessWarningCount) return null;
        return new(Id, Severity.Low, TextRef.Of(DiagText.RuleBackgroundTitle),
            TextRef.Of(DiagText.RuleBackgroundDetail, count, thresholds.BackgroundProcessWarningCount),
            count, thresholds.BackgroundProcessWarningCount, HealthCategories.Processes);
    }
}

/// <summary>Plan « Économie d'énergie » actif alors que le PC est sur secteur (information).</summary>
public sealed class PowerSaverOnAcRule : IHealthRule
{
    public string Id => HealthRuleIds.PowerSaverOnAc;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
        => AnalysisMetrics.IsPowerSaverOnAc(report)
            ? new(Id, Severity.Info, TextRef.Of(DiagText.RulePowerSaverTitle), TextRef.Of(DiagText.RulePowerSaverDetail), null, null, HealthCategories.Power)
            : null;
}

/// <summary>Build de Windows antérieure à la version minimale prise en charge (<see cref="OsInfo.IsSupported"/>).</summary>
public sealed class UnsupportedBuildRule : IHealthRule
{
    public string Id => HealthRuleIds.UnsupportedBuild;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (!AnalysisMetrics.IsOsKnown(report) || report.Os.IsSupported) return null;
        return new(Id, Severity.High, TextRef.Of(DiagText.RuleUnsupportedBuildTitle),
            TextRef.Of(DiagText.RuleUnsupportedBuildDetail, report.Os.BuildNumber, OsInfo.MinimumSupportedBuild),
            report.Os.BuildNumber, OsInfo.MinimumSupportedBuild, HealthCategories.System);
    }
}
