using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Settings;

namespace PCBoost.Diagnostics.Rules;

// Santé du matériel (catégorie « hardware ») : ces constats ne modifient pas le score de performance, ils signalent
// un risque ou une usure mesurés par Windows. Un disque en mauvais état est le seul constat Critical intégré.

/// <summary>Disque signalé défaillant, SSD usé ou erreurs de lecture non corrigées (le pire disque est retenu).</summary>
public sealed class DiskHealthRule : IHealthRule
{
    public string Id => HealthRuleIds.DiskHealth;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (report.Health is not { } health || health.Disks.Count == 0) return null;

        var critical = health.Disks.Where(d => d.IsCritical).OrderByDescending(d => d.IsSystemDisk).FirstOrDefault();
        if (critical is not null)
            return new(Id, Severity.Critical, TextRef.Of(DiagText.RuleDiskHealthCriticalTitle), Detail(critical),
                critical.Reliability?.WearPercent, DiskHealthInfo.WearCriticalPercent, HealthCategories.Hardware);

        var warning = health.Disks.Where(d => d.IsWarning).OrderByDescending(d => d.IsSystemDisk).FirstOrDefault();
        return warning is null
            ? null
            : new(Id, Severity.Medium, TextRef.Of(DiagText.RuleDiskHealthWarningTitle), Detail(warning),
                warning.Reliability?.WearPercent, DiskHealthInfo.WearWarningPercent, HealthCategories.Hardware);
    }

    internal static TextRef Detail(DiskHealthInfo disk)
    {
        if (disk.Status == DiskHealthStatus.Unhealthy) return TextRef.Of(DiagText.RuleDiskHealthUnhealthyDetail, disk.FriendlyName);
        if (disk.Reliability?.ReadErrorsUncorrected is long errors && errors > 0) return TextRef.Of(DiagText.RuleDiskHealthErrorsDetail, disk.FriendlyName, errors);
        if (disk.Reliability?.WearPercent is { } wear && wear >= DiskHealthInfo.WearWarningPercent) return TextRef.Of(DiagText.RuleDiskHealthWearDetail, disk.FriendlyName, wear);
        return TextRef.Of(DiagText.RuleDiskHealthWarningDetail, disk.FriendlyName);
    }
}

/// <summary>Batterie ne conservant plus qu'une faible part de sa capacité d'origine.</summary>
public sealed class BatteryWearRule : IHealthRule
{
    public string Id => HealthRuleIds.BatteryWear;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (report.Health is not { } health) return null;
        var worst = health.Batteries.Where(b => b.HealthPercent.HasValue).MinBy(b => b.HealthPercent!.Value);
        if (worst?.HealthPercent is not { } percent || percent >= BatteryInfo.AgingPercent) return null;
        var severity = percent < BatteryInfo.WornPercent ? Severity.Medium : Severity.Low;
        return new(Id, severity, TextRef.Of(DiagText.RuleBatteryWornTitle), TextRef.Of(DiagText.RuleBatteryDetail, percent),
            percent, BatteryInfo.AgingPercent, HealthCategories.Hardware);
    }
}

/// <summary>Périphériques signalés en erreur par le Gestionnaire de périphériques (information, aucune action automatique).</summary>
public sealed class DeviceProblemRule : IHealthRule
{
    private const int MaxNames = 3;

    public string Id => HealthRuleIds.DeviceProblems;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (report.Health is not { } health || health.DeviceProblems.Count == 0) return null;
        var count = health.DeviceProblems.Count;
        var names = string.Join(", ", health.DeviceProblems.Take(MaxNames).Select(d => d.Name)) + (count > MaxNames ? "…" : string.Empty);
        return new(Id, Severity.Low, TextRef.Of(DiagText.RuleDevicesTitle, count), TextRef.Of(DiagText.RuleDevicesDetail, count, names),
            count, null, HealthCategories.Hardware);
    }
}

/// <summary>Processeur ralenti sous charge (épisode observé) ou vitesse limitée par le microprogramme (événements Windows).</summary>
public sealed class ThermalLimitRule : IHealthRule
{
    public string Id => HealthRuleIds.ThermalLimit;

    public HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        if (report.Health?.Thermal is not { } thermal) return null;
        if (thermal.LastEpisode is { } episode)
            return new(Id, Severity.Medium, TextRef.Of(DiagText.RuleThermalTitle),
                TextRef.Of(DiagText.RuleThermalObservedDetail, episode.AverageProcessorPerformancePercent, episode.Duration.TotalSeconds),
                episode.AverageProcessorPerformancePercent, ThrottlingThresholds.PerformancePercent, HealthCategories.Hardware);
        if (thermal.FirmwareLimitEvents > 0)
            return new(Id, Severity.Low, TextRef.Of(DiagText.RuleThermalTitle),
                TextRef.Of(DiagText.RuleThermalFirmwareDetail, thermal.FirmwareLimitEvents),
                thermal.FirmwareLimitEvents, null, HealthCategories.Hardware);
        return null;
    }
}

/// <summary>Seuils partagés avec la détection en continu (<see cref="Health.ThrottlingEpisodeTracker"/>).</summary>
internal static class ThrottlingThresholds
{
    public const double PerformancePercent = Health.ThrottlingEpisodeTracker.PerformanceThresholdPercent;
}
