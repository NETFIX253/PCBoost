using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;
using PCBoost.Diagnostics.Rules;

namespace PCBoost.Diagnostics.Recommendations;

/// <summary>Identifiants stables des recommandations (utilisés pour les masquer).</summary>
public static class RecommendationIds
{
    public const string StartupHeavy = "rec.startup.heavy";
    public const string MemoryHigh = "rec.memory.high";
    public const string CpuHigh = "rec.cpu.high";
    public const string DiskBusy = "rec.disk.busy";
    public const string LowDiskSpace = "rec.storage.low-space";
    public const string Cleanup = "rec.cleanup.recoverable";
    public const string BackgroundApps = "rec.processes.background";
    public const string PowerPlan = "rec.power.balanced";
    public const string VisualEffects = "rec.visual-effects.modest-pc";
    public const string Restart = "rec.system.restart";
    public const string Thermal = "rec.thermal.check";
    public const string UpdateWindows = "rec.system.update-windows";
    public const string HardwareSsd = "rec.hardware.ssd";
    public const string HardwareRam = "rec.hardware.ram";
}

/// <summary>
/// Recommandations à partir du rapport et des constats. Les recommandations liées à un seuil ne sont produites que
/// si le constat correspondant existe (elles respectent donc les seuils personnalisés). Les pistes matérielles sont des
/// <see cref="RecommendationKind.Possibility"/> sans action d'achat. Tri : recommandations avant pistes, puis impact et confiance décroissants.
/// </summary>
public sealed class PerformanceRecommendationEngine : IPerformanceRecommendationEngine
{
    /// <summary>RAM nominale maximale pour évoquer une augmentation de mémoire.</summary>
    public const int RamPossibilityMaxGiB = 8;

    public IReadOnlyList<Recommendation> GetRecommendations(SystemAnalysisReport report, IReadOnlyList<HealthFinding> findings, IReadOnlyCollection<string> dismissedIds)
    {
        ArgumentNullException.ThrowIfNull(report);
        findings ??= [];
        var dismissed = new HashSet<string>(dismissedIds ?? [], StringComparer.OrdinalIgnoreCase);
        var byRule = new Dictionary<string, HealthFinding>(StringComparer.Ordinal);
        foreach (var f in findings) byRule.TryAdd(f.RuleId, f);

        var list = new List<Recommendation>();
        void Add(Recommendation? r)
        {
            if (r is not null && !dismissed.Contains(r.Id)) list.Add(r);
        }

        Add(Startup(report, byRule));
        Add(Memory(report, byRule));
        Add(Cpu(report, byRule));
        Add(Disk(byRule));
        Add(LowSpace(report, byRule));
        Add(Cleanup(report, byRule));
        Add(Background(report, byRule));
        Add(PowerPlan(byRule));
        Add(VisualEffects(report, byRule));
        Add(Restart(report, byRule));
        Add(Thermal(byRule));
        Add(UpdateWindows(report, byRule));
        Add(HardwareSsd(report));
        Add(HardwareRam(report, byRule));

        return list
            .OrderBy(r => r.Kind)
            .ThenByDescending(r => r.Impact)
            .ThenByDescending(r => r.Confidence)
            .ToList();
    }

    private static Recommendation? Startup(SystemAnalysisReport report, Dictionary<string, HealthFinding> byRule)
    {
        if (!byRule.TryGetValue(HealthRuleIds.StartupCount, out var finding)) return null;
        var count = report.EnabledStartupCount;
        var title = count == 1
            ? TextRef.Of(DiagText.RecStartupHeavyTitleOne, count)
            : TextRef.Of(DiagText.RecStartupHeavyTitle, count);
        var hasCandidates = report.StartupEntries.Any(e => e.IsEnabled && e.Recommendation == StartupRecommendation.CanDisable);
        return new Recommendation(RecommendationIds.StartupHeavy, title,
            TextRef.Of(DiagText.RecStartupHeavyDescription),
            TextRef.Of(DiagText.RecStartupHeavyReason, count, (int)(finding.Threshold ?? count)),
            ImpactLevel.High, hasCandidates ? ConfidenceLevel.High : ConfidenceLevel.Medium, RiskLevel.Low,
            RecommendationKind.Recommendation, Reversible: true, HealthCategories.Startup,
            new RecommendationAction(TextRef.Of(DiagText.ActionViewStartupApps), NavigationTargets.Startup, OptimizationIds.StartupApps));
    }

    private static Recommendation? Memory(SystemAnalysisReport report, Dictionary<string, HealthFinding> byRule)
    {
        if (!byRule.TryGetValue(HealthRuleIds.Memory, out var finding)) return null;
        var names = TopNames(report.TopMemoryProcesses.Select(p => p.Name), 3);
        var description = names is null
            ? TextRef.Of(DiagText.RecMemoryHighDescription)
            : TextRef.Of(DiagText.RecMemoryHighDescriptionProcesses, names);
        return new Recommendation(RecommendationIds.MemoryHigh, TextRef.Of(DiagText.RecMemoryHighTitle), description,
            TextRef.Of(DiagText.RecMemoryHighReason, finding.ObservedValue ?? 0, finding.Threshold ?? 0),
            ImpactFor(finding), ConfidenceLevel.High, RiskLevel.Low, RecommendationKind.Recommendation, Reversible: true,
            HealthCategories.Memory, new RecommendationAction(TextRef.Of(DiagText.ActionViewProcesses), NavigationTargets.Processes));
    }

    private static Recommendation? Cpu(SystemAnalysisReport report, Dictionary<string, HealthFinding> byRule)
    {
        if (!byRule.TryGetValue(HealthRuleIds.CpuSustained, out var finding)) return null;
        var top = report.TopCpuProcesses.FirstOrDefault();
        var description = top is null
            ? TextRef.Of(DiagText.RecCpuHighDescription)
            : TextRef.Of(DiagText.RecCpuHighDescriptionProcess, top.Name);
        return new Recommendation(RecommendationIds.CpuHigh, TextRef.Of(DiagText.RecCpuHighTitle), description,
            TextRef.Of(DiagText.RecCpuHighReason, finding.ObservedValue ?? 0, finding.Threshold ?? 0),
            ImpactFor(finding), ConfidenceLevel.Medium, RiskLevel.Low, RecommendationKind.Recommendation, Reversible: true,
            HealthCategories.Cpu, new RecommendationAction(TextRef.Of(DiagText.ActionViewProcesses), NavigationTargets.Processes));
    }

    private static Recommendation? Disk(Dictionary<string, HealthFinding> byRule)
    {
        if (!byRule.TryGetValue(HealthRuleIds.DiskActive, out var finding)) return null;
        return new Recommendation(RecommendationIds.DiskBusy, TextRef.Of(DiagText.RecDiskBusyTitle), TextRef.Of(DiagText.RecDiskBusyDescription),
            TextRef.Of(DiagText.RecDiskBusyReason, finding.ObservedValue ?? 0, finding.Threshold ?? 0),
            ImpactFor(finding), ConfidenceLevel.Medium, RiskLevel.Low, RecommendationKind.Recommendation, Reversible: true,
            HealthCategories.Disk, new RecommendationAction(TextRef.Of(DiagText.ActionViewPerformance), NavigationTargets.Performance));
    }

    private static Recommendation? LowSpace(SystemAnalysisReport report, Dictionary<string, HealthFinding> byRule)
    {
        if (!byRule.TryGetValue(HealthRuleIds.SystemDriveFreeSpace, out var finding) || AnalysisMetrics.MeasuredSystemDrive(report) is not { } drive)
            return null;
        return new Recommendation(RecommendationIds.LowDiskSpace, TextRef.Of(DiagText.RecLowSpaceTitle), TextRef.Of(DiagText.RecLowSpaceDescription),
            TextRef.Of(DiagText.RecLowSpaceReason, drive.FreeBytes / (double)ByteSize.GiB, drive.FreePercent, finding.Threshold ?? 0),
            ImpactFor(finding), ConfidenceLevel.High, RiskLevel.Low, RecommendationKind.Recommendation, Reversible: false,
            HealthCategories.Storage, new RecommendationAction(TextRef.Of(DiagText.ActionViewStorage), NavigationTargets.Storage));
    }

    private static Recommendation? Cleanup(SystemAnalysisReport report, Dictionary<string, HealthFinding> byRule)
    {
        if (!byRule.ContainsKey(HealthRuleIds.Cleanable) || AnalysisMetrics.CleanableBytes(report) is not long bytes) return null;
        // Libérer de l'espace aide surtout quand le disque système manque de place.
        var impact = byRule.ContainsKey(HealthRuleIds.SystemDriveFreeSpace) ? ImpactLevel.Medium : ImpactLevel.Low;
        return new Recommendation(RecommendationIds.Cleanup, TextRef.Of(DiagText.RecCleanupTitle), TextRef.Of(DiagText.RecCleanupDescription),
            DiagText.Size(DiagText.RecCleanupReasonGb, DiagText.RecCleanupReasonMb, bytes),
            impact, ConfidenceLevel.High, RiskLevel.Low, RecommendationKind.Recommendation, Reversible: false,
            HealthCategories.Cleanup, new RecommendationAction(TextRef.Of(DiagText.ActionViewCleanup), NavigationTargets.Cleanup, OptimizationIds.TempFiles));
    }

    private static Recommendation? Background(SystemAnalysisReport report, Dictionary<string, HealthFinding> byRule)
    {
        if (!byRule.TryGetValue(HealthRuleIds.BackgroundProcesses, out var finding)) return null;
        return new Recommendation(RecommendationIds.BackgroundApps, TextRef.Of(DiagText.RecBackgroundTitle), TextRef.Of(DiagText.RecBackgroundDescription),
            TextRef.Of(DiagText.RecBackgroundReason, report.BackgroundProcessCount, (int)(finding.Threshold ?? 0)),
            ImpactLevel.Medium, ConfidenceLevel.Medium, RiskLevel.Low, RecommendationKind.Recommendation, Reversible: true,
            HealthCategories.Processes,
            new RecommendationAction(TextRef.Of(DiagText.ActionViewBackgroundApps), NavigationTargets.Processes, OptimizationIds.BackgroundApps));
    }

    private static Recommendation? PowerPlan(Dictionary<string, HealthFinding> byRule)
    {
        if (!byRule.ContainsKey(HealthRuleIds.PowerSaverOnAc)) return null;
        return new Recommendation(RecommendationIds.PowerPlan, TextRef.Of(DiagText.RecPowerPlanTitle), TextRef.Of(DiagText.RecPowerPlanDescription),
            TextRef.Of(DiagText.RecPowerPlanReason),
            ImpactLevel.Medium, ConfidenceLevel.High, RiskLevel.Low, RecommendationKind.Recommendation, Reversible: true,
            HealthCategories.Power,
            new RecommendationAction(TextRef.Of(DiagText.ActionChoosePowerPlan), NavigationTargets.Optimization, OptimizationIds.PowerPlan));
    }

    private static Recommendation? VisualEffects(SystemAnalysisReport report, Dictionary<string, HealthFinding> byRule)
    {
        var tier = report.HardwareProfile?.Tier ?? HardwareTier.Unknown;
        var modest = tier is HardwareTier.LegacyLowResource or HardwareTier.Entry
            || (tier == HardwareTier.LowEnd && (byRule.ContainsKey(HealthRuleIds.Memory) || byRule.ContainsKey(HealthRuleIds.CpuSustained)));
        if (!modest) return null;

        var ramGiB = AnalysisMetrics.NominalGiB(report.Memory.TotalBytes);
        var reason = report.Cpu.PhysicalCores > 0
            ? TextRef.Of(DiagText.RecVisualEffectsReason, ramGiB, report.Cpu.PhysicalCores)
            : TextRef.Of(DiagText.RecVisualEffectsReasonRamOnly, ramGiB);
        return new Recommendation(RecommendationIds.VisualEffects, TextRef.Of(DiagText.RecVisualEffectsTitle), TextRef.Of(DiagText.RecVisualEffectsDescription),
            reason, tier == HardwareTier.LegacyLowResource ? ImpactLevel.Medium : ImpactLevel.Low, ConfidenceLevel.Medium, RiskLevel.Low,
            RecommendationKind.Recommendation, Reversible: true, HealthCategories.System,
            new RecommendationAction(TextRef.Of(DiagText.ActionViewVisualEffects), NavigationTargets.Optimization, OptimizationIds.VisualEffects));
    }

    private static Recommendation? Restart(SystemAnalysisReport report, Dictionary<string, HealthFinding> byRule)
    {
        if (!byRule.TryGetValue(HealthRuleIds.Uptime, out var finding) || AnalysisMetrics.UptimeDays(report) is not double days) return null;
        return new Recommendation(RecommendationIds.Restart, TextRef.Of(DiagText.RecRestartTitle), TextRef.Of(DiagText.RecRestartDescription),
            TextRef.Of(DiagText.RecRestartReason, days, finding.Threshold ?? 0),
            ImpactLevel.Medium, ConfidenceLevel.Medium, RiskLevel.Low, RecommendationKind.Recommendation, Reversible: true,
            HealthCategories.System, Action: null);
    }

    private static Recommendation? Thermal(Dictionary<string, HealthFinding> byRule)
    {
        (string RuleId, string ReasonKey)[] sources =
        [
            (HealthRuleIds.CpuTemperature, DiagText.RecThermalReasonCpu),
            (HealthRuleIds.GpuTemperature, DiagText.RecThermalReasonGpu),
            (HealthRuleIds.StorageTemperature, DiagText.RecThermalReasonStorage),
        ];
        // Composant le plus au-dessus de son seuil.
        HealthFinding? hottest = null;
        string? reasonKey = null;
        foreach (var (ruleId, key) in sources)
        {
            if (!byRule.TryGetValue(ruleId, out var f)) continue;
            if (hottest is null || Excess(f) > Excess(hottest))
            {
                hottest = f;
                reasonKey = key;
            }
        }
        if (hottest is null || reasonKey is null) return null;

        return new Recommendation(RecommendationIds.Thermal, TextRef.Of(DiagText.RecThermalTitle), TextRef.Of(DiagText.RecThermalDescription),
            TextRef.Of(reasonKey, hottest.ObservedValue ?? 0, hottest.Threshold ?? 0),
            ImpactLevel.Medium, ConfidenceLevel.Low, RiskLevel.Low, RecommendationKind.Possibility, Reversible: true,
            HealthCategories.Thermal, new RecommendationAction(TextRef.Of(DiagText.ActionViewPerformance), NavigationTargets.Performance));

        static double Excess(HealthFinding f) => (f.ObservedValue ?? 0) - (f.Threshold ?? 0);
    }

    private static Recommendation? UpdateWindows(SystemAnalysisReport report, Dictionary<string, HealthFinding> byRule)
    {
        if (!byRule.ContainsKey(HealthRuleIds.UnsupportedBuild)) return null;
        return new Recommendation(RecommendationIds.UpdateWindows, TextRef.Of(DiagText.RecUpdateWindowsTitle), TextRef.Of(DiagText.RecUpdateWindowsDescription),
            TextRef.Of(DiagText.RecUpdateWindowsReason, report.Os.BuildNumber, OsInfo.MinimumSupportedBuild),
            ImpactLevel.Low, ConfidenceLevel.High, RiskLevel.Medium, RecommendationKind.Recommendation, Reversible: false,
            HealthCategories.System, Action: null);
    }

    private static Recommendation? HardwareSsd(SystemAnalysisReport report)
    {
        if (!AnalysisMetrics.SystemDriveIsHdd(report)) return null;
        return new Recommendation(RecommendationIds.HardwareSsd, TextRef.Of(DiagText.RecHardwareSsdTitle), TextRef.Of(DiagText.RecHardwareSsdDescription),
            TextRef.Of(DiagText.RecHardwareSsdReason),
            ImpactLevel.High, ConfidenceLevel.Medium, RiskLevel.Medium, RecommendationKind.Possibility, Reversible: false,
            "hardware", new RecommendationAction(TextRef.Of(DiagText.ActionViewOldPc), NavigationTargets.OldPc));
    }

    private static Recommendation? HardwareRam(SystemAnalysisReport report, Dictionary<string, HealthFinding> byRule)
    {
        var ramGiB = AnalysisMetrics.NominalGiB(report.Memory.TotalBytes);
        if (ramGiB is <= 0 or > RamPossibilityMaxGiB || !byRule.TryGetValue(HealthRuleIds.Memory, out var finding)) return null;
        // Une seule observation : confiance faible (le conseiller matériel s'appuie, lui, sur l'historique).
        return new Recommendation(RecommendationIds.HardwareRam, TextRef.Of(DiagText.RecHardwareRamTitle), TextRef.Of(DiagText.RecHardwareRamDescription),
            TextRef.Of(DiagText.RecHardwareRamReason, ramGiB, finding.ObservedValue ?? 0),
            ImpactLevel.Medium, ConfidenceLevel.Low, RiskLevel.Medium, RecommendationKind.Possibility, Reversible: false,
            "hardware", Action: null);
    }

    private static ImpactLevel ImpactFor(HealthFinding finding) => finding.Severity >= Severity.High ? ImpactLevel.High : ImpactLevel.Medium;

    /// <summary>Noms distincts (au plus <paramref name="max"/>), joints par des virgules ; null si aucun.</summary>
    internal static string? TopNames(IEnumerable<string> names, int max)
    {
        var distinct = names.Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
        return distinct.Count == 0 ? null : string.Join(", ", distinct);
    }
}
