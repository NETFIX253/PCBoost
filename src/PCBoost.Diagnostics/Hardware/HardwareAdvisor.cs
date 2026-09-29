using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;

namespace PCBoost.Diagnostics.Hardware;

/// <summary>Identifiants stables des conseils matériels.</summary>
public static class HardwareAdviceIds
{
    public const string Memory = "hardware.ram";
    public const string Ssd = "hardware.ssd";
    public const string StorageFull = "hardware.storage.full";
    public const string IntegratedGpu = "hardware.gpu.integrated";
    public const string CpuSaturated = "hardware.cpu.saturated";
}

/// <summary>
/// Conseils matériels factuels (§24) : une observation mesurée, puis une suggestion au conditionnel.
/// Rien n'est vendu, rien n'est présenté comme nécessaire. Les conseils basés sur l'historique exigent des données :
/// aucun conseil RAM/CPU sans au moins <see cref="MinimumHistorySamples"/> instantanés.
/// </summary>
public sealed class HardwareAdvisor : IHardwareAdvisor
{
    /// <summary>RAM nominale maximale pour laquelle une augmentation est évoquée.</summary>
    public const int RamAdviceMaxGiB = 8;
    public const double RamBusyPercent = 85;
    public const double CpuBusyPercent = 90;

    /// <summary>Part des instantanés au-dessus du seuil pour parler d'utilisation « fréquente ».</summary>
    public const double FrequentShare = 0.25;

    public const int MinimumHistorySamples = 10;

    /// <summary>En dessous : confiance faible. Jusqu'à <see cref="HighConfidenceSamples"/> : moyenne.</summary>
    public const int MediumConfidenceSamples = 30;
    public const int HighConfidenceSamples = 240;

    /// <summary>Remplissage du disque système au-delà duquel un disque plus grand est évoqué.</summary>
    public const double StorageFullUsedPercent = 90;

    public IReadOnlyList<HardwareAdvice> GetAdvice(SystemAnalysisReport report, IReadOnlyList<PerformanceSnapshot> recentHistory)
    {
        ArgumentNullException.ThrowIfNull(report);
        recentHistory ??= [];
        var advice = new List<HardwareAdvice>();

        // RAM : ≤ 8 Go et utilisation > 85 % sur une part significative de l'historique.
        var ramGiB = AnalysisMetrics.NominalGiB(report.Memory.TotalBytes);
        if (ramGiB is > 0 and <= RamAdviceMaxGiB && recentHistory.Count >= MinimumHistorySamples)
        {
            var share = Share(recentHistory, s => s.MemoryPercent > RamBusyPercent);
            if (share >= FrequentShare)
            {
                advice.Add(new HardwareAdvice(HardwareAdviceIds.Memory,
                    TextRef.Of(DiagText.AdviceRamObservation, ramGiB, share * 100),
                    TextRef.Of(DiagText.AdviceRamSuggestion),
                    ConfidenceFor(recentHistory.Count), "memory"));
            }
        }

        // Disque système HDD → SSD.
        if (AnalysisMetrics.SystemDriveIsHdd(report))
        {
            advice.Add(new HardwareAdvice(HardwareAdviceIds.Ssd,
                TextRef.Of(DiagText.AdviceSsdObservation), TextRef.Of(DiagText.AdviceSsdSuggestion), ConfidenceLevel.High, "storage"));
        }

        // Disque système rempli à plus de 90 %.
        if (AnalysisMetrics.MeasuredSystemDrive(report) is { } drive)
        {
            var usedPercent = drive.UsedBytes * 100d / drive.TotalBytes;
            if (usedPercent > StorageFullUsedPercent)
            {
                advice.Add(new HardwareAdvice(HardwareAdviceIds.StorageFull,
                    TextRef.Of(DiagText.AdviceStorageFullObservation, usedPercent, drive.FreeBytes / (double)ByteSize.GiB, drive.TotalBytes / (double)ByteSize.GiB),
                    TextRef.Of(DiagText.AdviceStorageFullSuggestion), ConfidenceLevel.High, "storage"));
            }
        }

        // Carte graphique intégrée uniquement (pertinent pour le jeu).
        var physicalGpus = report.Gpus.Where(g => !g.IsSoftwareAdapter).ToList();
        if (physicalGpus.Count > 0 && physicalGpus.All(g => g.IsLikelyIntegrated))
        {
            advice.Add(new HardwareAdvice(HardwareAdviceIds.IntegratedGpu,
                TextRef.Of(DiagText.AdviceGpuObservation, physicalGpus[0].Name),
                TextRef.Of(DiagText.AdviceGpuSuggestion), ConfidenceLevel.Medium, "gpu"));
        }

        // Processeur saturé (> 90 %) sur une part significative de l'historique.
        if (recentHistory.Count >= MinimumHistorySamples)
        {
            var share = Share(recentHistory, s => s.CpuPercent > CpuBusyPercent);
            if (share >= FrequentShare)
            {
                advice.Add(new HardwareAdvice(HardwareAdviceIds.CpuSaturated,
                    TextRef.Of(DiagText.AdviceCpuObservation, CpuName(report.Cpu), share * 100),
                    TextRef.Of(DiagText.AdviceCpuSuggestion), ConfidenceFor(recentHistory.Count), "cpu"));
            }
        }

        return advice;
    }

    public static ConfidenceLevel ConfidenceFor(int sampleCount) => sampleCount switch
    {
        < MediumConfidenceSamples => ConfidenceLevel.Low,
        < HighConfidenceSamples => ConfidenceLevel.Medium,
        _ => ConfidenceLevel.High,
    };

    private static double Share(IReadOnlyList<PerformanceSnapshot> history, Func<PerformanceSnapshot, bool> predicate)
    {
        var hits = 0;
        foreach (var s in history) if (predicate(s)) hits++;
        return history.Count == 0 ? 0 : hits / (double)history.Count;
    }

    private static string CpuName(CpuInfo cpu) => string.IsNullOrWhiteSpace(cpu.Name) ? "CPU" : cpu.Name.Trim();
}
