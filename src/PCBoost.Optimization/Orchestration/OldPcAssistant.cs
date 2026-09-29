using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;

namespace PCBoost.Optimization.Orchestration;

/// <summary>
/// Assistant « Optimiser un ancien PC » (§23) : constats mesurables, puis trois niveaux cumulatifs.
/// Essentiel = fichiers temporaires + démarrage à fort impact ; Standard = + effets visuels (forcés) + plan d'alimentation ;
/// Avancé = + applications d'arrière-plan, traité comme risque élevé (confirmation explicite exigée).
/// </summary>
public sealed class OldPcAssistant : IOldPcAssistant
{
    internal const long LowMemoryTotal = 4 * ByteSize.GiB;
    internal const double HighMemoryUsePercent = 85;
    internal const int WeakCpuMaxCores = 2;
    internal const int WeakCpuMaxThreads = 4;
    internal const int WeakCpuBaseClockMHz = 2000;
    internal const double LowFreeSpacePercent = 15;
    internal const int HeavyStartupCount = 8;
    internal const int ManyBackgroundProcesses = 120;

    private static readonly IReadOnlyList<string> Essential = [OptimizationIds.TemporaryFiles, OptimizationIds.StartupApps];
    private static readonly IReadOnlyList<string> Standard = [.. Essential, OptimizationIds.VisualEffects, OptimizationIds.PowerPlan];
    private static readonly IReadOnlyList<string> Advanced = [.. Standard, OptimizationIds.BackgroundApps];

    private readonly IOptimizationManager _manager;

    public OldPcAssistant(IOptimizationManager manager) => _manager = manager;

    internal static IReadOnlyList<string> OptimizationsFor(OldPcLevel level) => level switch
    {
        OldPcLevel.Essential => Essential,
        OldPcLevel.Standard => Standard,
        _ => Advanced,
    };

    public Task<OldPcAssessment> AssessAsync(SystemAnalysisReport analysis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var findings = new List<TextRef>();

        var memory = analysis.Memory;
        var lowMemory = (memory.TotalBytes > 0 && memory.TotalBytes <= LowMemoryTotal) || memory.UsedPercent > HighMemoryUsePercent;
        if (lowMemory)
            findings.Add(TextRef.Of("Opt_OldPc_Finding_Memory", memory.TotalBytes / (double)ByteSize.GiB, memory.UsedPercent));

        // ≤ 2 cœurs, ou ≤ 4 threads avec une fréquence de base inférieure à 2 GHz (fréquence inconnue : critère non retenu).
        var cpu = analysis.Cpu;
        var weakCpu = (cpu.PhysicalCores > 0 && cpu.PhysicalCores <= WeakCpuMaxCores)
                      || (cpu.LogicalProcessors > 0 && cpu.LogicalProcessors <= WeakCpuMaxThreads && cpu.BaseClockMHz is > 0 and < WeakCpuBaseClockMHz);
        if (weakCpu)
            findings.Add(TextRef.Of("Opt_OldPc_Finding_Cpu", cpu.PhysicalCores, cpu.LogicalProcessors));

        var systemDrive = analysis.SystemDrive;
        var hdd = systemDrive?.MediaType == StorageMediaType.Hdd;
        if (hdd) findings.Add(TextRef.Of("Opt_OldPc_Finding_Hdd"));

        var lowSpace = systemDrive is { TotalBytes: > 0 } && systemDrive.FreePercent < LowFreeSpacePercent;
        if (lowSpace)
            findings.Add(TextRef.Of("Opt_OldPc_Finding_DiskSpace", systemDrive!.FreePercent, systemDrive.FreeBytes / (double)ByteSize.GiB));

        var startupCount = analysis.EnabledStartupCount;
        var heavyStartup = startupCount > HeavyStartupCount;
        if (heavyStartup) findings.Add(TextRef.Of("Opt_OldPc_Finding_Startup", startupCount));

        var manyProcesses = analysis.BackgroundProcessCount > ManyBackgroundProcesses;
        if (manyProcesses) findings.Add(TextRef.Of("Opt_OldPc_Finding_Processes", analysis.BackgroundProcessCount));

        if (findings.Count == 0) findings.Add(TextRef.Of("Opt_OldPc_Finding_None"));

        var byLevel = new Dictionary<OldPcLevel, IReadOnlyList<string>>
        {
            [OldPcLevel.Essential] = Essential,
            [OldPcLevel.Standard] = Standard,
            [OldPcLevel.Advanced] = Advanced,
        };
        return Task.FromResult(new OldPcAssessment(lowMemory, weakCpu, hdd, lowSpace, heavyStartup, manyProcesses, findings, byLevel));
    }

    public async Task<OptimizationPlan> BuildPlanAsync(OldPcLevel level, SystemAnalysisReport analysis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var ids = OptimizationsFor(level);
        OptimizationPlan plan;
        if (level == OldPcLevel.Essential && _manager is OptimizationManager manager)
        {
            // Niveau Essentiel : seules les applications de démarrage à impact mesuré élevé sont proposées.
            var items = new Dictionary<string, object> { [OptimizationIds.StartupMinimumImpactItemKey] = StartupImpact.High };
            plan = await manager.BuildPlanAsync(SessionType.OldPcAssistant, ids, analysis, null, items, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            plan = await _manager.BuildPlanAsync(SessionType.OldPcAssistant, ids, analysis, null, cancellationToken).ConfigureAwait(false);
        }

        if (level != OldPcLevel.Advanced) return plan;

        // Niveau Avancé : traité comme risque élevé → le validateur exige HighRiskActionsConfirmed.
        var previews = plan.Previews.Select(p => p.OptimizationId == OptimizationIds.BackgroundApps
                ? p with { Risk = RiskLevel.High, Changes = p.Changes.Select(c => c with { Risk = RiskLevel.High }).ToList() }
                : p)
            .ToList();
        return plan with { Previews = previews };
    }
}
