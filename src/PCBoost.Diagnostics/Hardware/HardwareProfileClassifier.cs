using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;

namespace PCBoost.Diagnostics.Hardware;

/// <summary>
/// Classe le PC selon des critères mesurables (§60), évalués dans cet ordre (voir docs/SCORING.md) :
/// <list type="number">
/// <item>Unknown : mémoire totale illisible (0).</item>
/// <item>LegacyLowResource : RAM ≤ 4 Go, ou disque système HDD avec ≤ 2 cœurs physiques.</item>
/// <item>Entry : RAM ≤ 6 Go, ou ≤ 2 cœurs physiques.</item>
/// <item>LowEnd : RAM ≤ 8 Go et (≤ 4 cœurs ou pas de GPU dédié).</item>
/// <item>HighEnd : RAM ≥ 16 Go, ≥ 8 cœurs et GPU dédié avec ≥ 6 Go de mémoire vidéo.</item>
/// <item>MidRange sinon.</item>
/// </list>
/// La RAM est la capacité nominale (Go arrondis au supérieur). Un nombre de cœurs illisible (0) n'est jamais
/// considéré comme « peu de cœurs » : seuls les critères mesurés sont appliqués.
/// </summary>
public sealed class HardwareProfileClassifier : IHardwareProfileClassifier
{
    public const int LegacyMaxRamGiB = 4;
    public const int EntryMaxRamGiB = 6;
    public const int LowEndMaxRamGiB = 8;
    public const int HighEndMinRamGiB = 16;
    public const int FewCoresMax = 2;
    public const int LowEndMaxCores = 4;
    public const int HighEndMinCores = 8;
    public const int HighEndMinVideoMemoryGiB = 6;

    /// <summary>Espace libre du disque système sous lequel <see cref="HardwareProfile.LowSystemDriveSpace"/> est vrai.</summary>
    public const double LowSpaceFreePercent = 15;

    public HardwareProfile Classify(CpuInfo cpu, MemoryInfo memory, IReadOnlyList<StorageDrive> drives, IReadOnlyList<GpuInfo> gpus)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(memory);
        drives ??= [];
        gpus ??= [];

        var systemDrive = drives.FirstOrDefault(d => d.IsSystemDrive);
        var isHdd = systemDrive?.MediaType == StorageMediaType.Hdd;
        var lowSpace = systemDrive is { TotalBytes: > 0 } && systemDrive.FreePercent < LowSpaceFreePercent;
        var dedicated = gpus.Where(g => !g.IsSoftwareAdapter && !g.IsLikelyIntegrated).ToList();
        var hasDedicatedGpu = dedicated.Count > 0;
        var maxVideoMemory = hasDedicatedGpu ? dedicated.Max(g => g.DedicatedVideoMemoryBytes ?? 0) : 0;
        int? videoMemoryGiB = maxVideoMemory > 0 ? AnalysisMetrics.NominalGiB(maxVideoMemory) : null;

        if (memory.TotalBytes <= 0)
        {
            return new HardwareProfile(HardwareTier.Unknown, [TextRef.Of(DiagText.ProfileReasonUnknown)],
                LowMemory: false, isHdd, FewCores: false, lowSpace, hasDedicatedGpu);
        }

        var ramGiB = AnalysisMetrics.NominalGiB(memory.TotalBytes);
        int? cores = cpu.PhysicalCores > 0 ? cpu.PhysicalCores : null;
        var fewCores = cores is <= FewCoresMax;

        var ramReason = TextRef.Of(DiagText.ProfileReasonRam, ramGiB);
        TextRef? coresReason = cores is int c ? TextRef.Of(DiagText.ProfileReasonCores, c) : null;
        TextRef? driveReason = systemDrive?.MediaType switch
        {
            StorageMediaType.Hdd => TextRef.Of(DiagText.ProfileReasonHdd),
            StorageMediaType.Ssd or StorageMediaType.Scm => TextRef.Of(DiagText.ProfileReasonSsd),
            _ => null,
        };
        TextRef? gpuReason = !hasDedicatedGpu
            ? (gpus.Any(g => !g.IsSoftwareAdapter) ? TextRef.Of(DiagText.ProfileReasonIntegratedGpu) : null)
            : videoMemoryGiB is int v ? TextRef.Of(DiagText.ProfileReasonDedicatedGpu, v) : TextRef.Of(DiagText.ProfileReasonDedicatedGpuUnknownMemory);

        HardwareTier tier;
        var decisive = new List<TextRef?>();
        if (ramGiB <= LegacyMaxRamGiB)
        {
            tier = HardwareTier.LegacyLowResource;
            decisive.Add(ramReason);
        }
        else if (isHdd && fewCores)
        {
            tier = HardwareTier.LegacyLowResource;
            decisive.Add(driveReason);
            decisive.Add(coresReason);
        }
        else if (ramGiB <= EntryMaxRamGiB)
        {
            tier = HardwareTier.Entry;
            decisive.Add(ramReason);
        }
        else if (fewCores)
        {
            tier = HardwareTier.Entry;
            decisive.Add(coresReason);
        }
        else if (ramGiB <= LowEndMaxRamGiB && (cores is <= LowEndMaxCores || !hasDedicatedGpu))
        {
            tier = HardwareTier.LowEnd;
            decisive.Add(ramReason);
            decisive.Add(cores is <= LowEndMaxCores ? coresReason : gpuReason);
        }
        else if (ramGiB >= HighEndMinRamGiB && cores is >= HighEndMinCores && videoMemoryGiB is >= HighEndMinVideoMemoryGiB)
        {
            tier = HardwareTier.HighEnd;
            decisive.Add(ramReason);
            decisive.Add(coresReason);
            decisive.Add(gpuReason);
        }
        else
        {
            tier = HardwareTier.MidRange;
            decisive.Add(ramReason);
        }

        // Raisons : critères décisifs d'abord, puis les autres faits mesurés.
        var reasons = new List<TextRef>();
        foreach (var r in decisive.Concat([ramReason, coresReason, driveReason, gpuReason]))
        {
            if (r is not null && !reasons.Contains(r)) reasons.Add(r);
        }
        if (lowSpace && systemDrive is not null)
            reasons.Add(TextRef.Of(DiagText.ProfileReasonLowSpace, systemDrive.FreePercent));

        return new HardwareProfile(tier, reasons, LowMemory: ramGiB <= EntryMaxRamGiB, isHdd, fewCores, lowSpace, hasDedicatedGpu);
    }
}
