using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;
using PCBoost.Diagnostics.Recommendations;

namespace PCBoost.Diagnostics.SlowPc;

/// <summary>Identifiants stables des facteurs de lenteur.</summary>
public static class SlownessFactorIds
{
    public const string Memory = "slow.memory";
    public const string Startup = "slow.startup";
    public const string DiskBusy = "slow.disk.busy";
    public const string SystemDriveHdd = "slow.disk.hdd";
    public const string Cpu = "slow.cpu";
    public const string LowDiskSpace = "slow.storage.low";
    public const string CpuTemperature = "slow.thermal.cpu";
    public const string GpuTemperature = "slow.thermal.gpu";
    public const string StorageTemperature = "slow.thermal.storage";
    public const string Uptime = "slow.uptime";
    public const string PowerSaver = "slow.power.saver";
    public const string BackgroundProcesses = "slow.processes.background";
}

/// <summary>
/// « Pourquoi mon PC est lent ? » (§74). Réutilise la dernière analyse si elle a moins de 2 minutes, sinon en lance une
/// (échantillonnage de 5 s). Un facteur n'est retenu que s'il dépasse le seuil « warning » configuré (ou, pour le HDD et le
/// plan d'alimentation, s'il est constaté). Facteurs triés par impact puis confiance décroissants.
/// </summary>
public sealed class SlowPcDiagnosticService : ISlowPcDiagnosticService
{
    private readonly ISystemAnalyzer _analyzer;
    private readonly ISettingsService _settings;
    private readonly IClock _clock;
    private readonly SlowPcDiagnosticOptions _options;

    public SlowPcDiagnosticService(ISystemAnalyzer analyzer, ISettingsService settings, IClock clock, SlowPcDiagnosticOptions? options = null)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new SlowPcDiagnosticOptions();
    }

    public async Task<SlownessDiagnosis> DiagnoseAsync(CancellationToken cancellationToken = default)
    {
        var report = _analyzer.LastReport;
        if (report is null || _clock.UtcNow - report.Timestamp >= _options.ReportMaxAge || report.Timestamp > _clock.UtcNow)
        {
            report = await _analyzer.AnalyzeAsync(
                new AnalysisOptions(IncludeCleanupScan: true, LoadSamplingDuration: _options.LoadSamplingDuration),
                progress: null, cancellationToken).ConfigureAwait(false);
        }

        var factors = Evaluate(report, _settings.Current.Thresholds ?? new HealthThresholds());
        return new SlownessDiagnosis(_clock.UtcNow, factors, NoSignificantFactor: factors.Count == 0);
    }

    /// <summary>Évalue les facteurs de lenteur d'un rapport (pur, testable).</summary>
    public static IReadOnlyList<SlownessFactor> Evaluate(SystemAnalysisReport report, HealthThresholds t)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(t);
        var factors = new List<SlownessFactor>();

        // Mémoire fortement utilisée : nomme les 3 plus gros processus.
        if (AnalysisMetrics.MemoryUsedPercent(report) is double used && used >= t.RamWarningPercent)
        {
            var totalGiB = AnalysisMetrics.TotalMemoryGiB(report);
            var names = PerformanceRecommendationEngine.TopNames(report.TopMemoryProcesses.Select(p => p.Name), 3);
            factors.Add(new SlownessFactor(SlownessFactorIds.Memory,
                TextRef.Of(DiagText.SlowMemoryTitle),
                TextRef.Of(DiagText.SlowMemoryEvidence, used, totalGiB * used / 100, totalGiB),
                TextRef.Of(DiagText.SlowMemoryWhy),
                names is null ? TextRef.Of(DiagText.SlowMemoryWhatToDo) : TextRef.Of(DiagText.SlowMemoryWhatToDoProcesses, names),
                used >= t.RamCriticalPercent ? ImpactLevel.High : ImpactLevel.Medium, ConfidenceLevel.High,
                Action(DiagText.ActionViewProcesses, NavigationTargets.Processes)));
        }

        // Démarrage chargé.
        if (AnalysisMetrics.EnabledStartupCount(report) is int startup && startup >= t.StartupWarningCount)
        {
            factors.Add(new SlownessFactor(SlownessFactorIds.Startup,
                TextRef.Of(DiagText.SlowStartupTitle),
                TextRef.Of(DiagText.SlowStartupEvidence, startup),
                TextRef.Of(DiagText.SlowStartupWhy),
                TextRef.Of(DiagText.SlowStartupWhatToDo),
                startup >= t.StartupCriticalCount ? ImpactLevel.High : ImpactLevel.Medium, ConfidenceLevel.High,
                Action(DiagText.ActionViewStartupApps, NavigationTargets.Startup, OptimizationIds.StartupApps)));
        }

        // Disque saturé (mesure courte : confiance moyenne).
        if (AnalysisMetrics.DiskActivePercent(report) is double disk && disk >= t.DiskActiveWarningPercent)
        {
            factors.Add(new SlownessFactor(SlownessFactorIds.DiskBusy,
                TextRef.Of(DiagText.SlowDiskBusyTitle),
                TextRef.Of(DiagText.SlowDiskBusyEvidence, disk),
                TextRef.Of(DiagText.SlowDiskBusyWhy),
                TextRef.Of(DiagText.SlowDiskBusyWhatToDo),
                ImpactLevel.High, ConfidenceLevel.Medium,
                Action(DiagText.ActionViewPerformance, NavigationTargets.Performance)));
        }

        // Windows sur disque dur mécanique.
        if (AnalysisMetrics.SystemDriveIsHdd(report) && report.SystemDrive is { } hddDrive)
        {
            factors.Add(new SlownessFactor(SlownessFactorIds.SystemDriveHdd,
                TextRef.Of(DiagText.SlowHddTitle),
                TextRef.Of(DiagText.SlowHddEvidence, hddDrive.RootPath),
                TextRef.Of(DiagText.SlowHddWhy),
                TextRef.Of(DiagText.SlowHddWhatToDo),
                ImpactLevel.High, ConfidenceLevel.High,
                Action(DiagText.ActionViewOldPc, NavigationTargets.OldPc)));
        }

        // Processeur très sollicité : nomme le processus principal.
        if (AnalysisMetrics.CpuAveragePercent(report) is double cpu && cpu >= t.CpuSustainedWarningPercent)
        {
            var top = report.TopCpuProcesses.FirstOrDefault();
            factors.Add(new SlownessFactor(SlownessFactorIds.Cpu,
                TextRef.Of(DiagText.SlowCpuTitle),
                TextRef.Of(DiagText.SlowCpuEvidence, cpu, AnalysisMetrics.CpuMaxPercent(report) ?? cpu),
                TextRef.Of(DiagText.SlowCpuWhy),
                top is null ? TextRef.Of(DiagText.SlowCpuWhatToDo) : TextRef.Of(DiagText.SlowCpuWhatToDoProcess, top.Name),
                cpu >= t.CpuSustainedCriticalPercent ? ImpactLevel.High : ImpactLevel.Medium, ConfidenceLevel.Medium,
                Action(DiagText.ActionViewProcesses, NavigationTargets.Processes)));
        }

        // Espace disque faible.
        if (AnalysisMetrics.MeasuredSystemDrive(report) is { } drive && drive.FreePercent <= t.SystemDriveFreeWarningPercent)
        {
            var cleanable = AnalysisMetrics.CleanableBytes(report);
            var whatToDo = cleanable is > 0
                ? DiagText.Size(DiagText.SlowLowSpaceWhatToDoCleanableGb, DiagText.SlowLowSpaceWhatToDoCleanableMb, cleanable.Value)
                : TextRef.Of(DiagText.SlowLowSpaceWhatToDo);
            var action = cleanable is > 0
                ? Action(DiagText.ActionViewCleanup, NavigationTargets.Cleanup, OptimizationIds.TempFiles)
                : Action(DiagText.ActionViewStorage, NavigationTargets.Storage);
            factors.Add(new SlownessFactor(SlownessFactorIds.LowDiskSpace,
                TextRef.Of(DiagText.SlowLowSpaceTitle),
                TextRef.Of(DiagText.SlowLowSpaceEvidence, drive.FreeBytes / (double)ByteSize.GiB, drive.RootPath, Math.Clamp(drive.FreePercent, 0, 100)),
                TextRef.Of(DiagText.SlowLowSpaceWhy),
                whatToDo,
                drive.FreePercent <= t.SystemDriveFreeCriticalPercent ? ImpactLevel.High : ImpactLevel.Medium, ConfidenceLevel.High,
                action));
        }

        // Températures élevées (uniquement si mesurées).
        AddThermal(factors, SlownessFactorIds.CpuTemperature, DiagText.SlowThermalCpuTitle, report.Temperatures.Cpu, t.CpuTemperatureWarningC);
        AddThermal(factors, SlownessFactorIds.GpuTemperature, DiagText.SlowThermalGpuTitle, report.Temperatures.Gpu, t.GpuTemperatureWarningC);
        AddThermal(factors, SlownessFactorIds.StorageTemperature, DiagText.SlowThermalStorageTitle, report.Temperatures.Storage, t.StorageTemperatureWarningC);

        // Uptime long.
        if (AnalysisMetrics.UptimeDays(report) is double days && days >= t.UptimeWarningDays)
        {
            factors.Add(new SlownessFactor(SlownessFactorIds.Uptime,
                TextRef.Of(DiagText.SlowUptimeTitle),
                TextRef.Of(DiagText.SlowUptimeEvidence, days),
                TextRef.Of(DiagText.SlowUptimeWhy),
                TextRef.Of(DiagText.SlowUptimeWhatToDo),
                ImpactLevel.Low, ConfidenceLevel.Medium, Action: null));
        }

        // Plan « Économie d'énergie » sur secteur.
        if (AnalysisMetrics.IsPowerSaverOnAc(report))
        {
            factors.Add(new SlownessFactor(SlownessFactorIds.PowerSaver,
                TextRef.Of(DiagText.SlowPowerSaverTitle),
                TextRef.Of(DiagText.SlowPowerSaverEvidence),
                TextRef.Of(DiagText.SlowPowerSaverWhy),
                TextRef.Of(DiagText.SlowPowerSaverWhatToDo),
                ImpactLevel.Medium, ConfidenceLevel.High,
                Action(DiagText.ActionChoosePowerPlan, NavigationTargets.Optimization, OptimizationIds.PowerPlan)));
        }

        // Nombreux processus en arrière-plan.
        if (AnalysisMetrics.BackgroundProcessCount(report) is int background && background >= t.BackgroundProcessWarningCount)
        {
            factors.Add(new SlownessFactor(SlownessFactorIds.BackgroundProcesses,
                TextRef.Of(DiagText.SlowBackgroundTitle),
                TextRef.Of(DiagText.SlowBackgroundEvidence, background),
                TextRef.Of(DiagText.SlowBackgroundWhy),
                TextRef.Of(DiagText.SlowBackgroundWhatToDo),
                ImpactLevel.Low, ConfidenceLevel.Medium,
                Action(DiagText.ActionViewBackgroundApps, NavigationTargets.Processes, OptimizationIds.BackgroundApps)));
        }

        // Tri stable : impact, puis confiance.
        return factors
            .OrderByDescending(f => f.Impact)
            .ThenByDescending(f => f.Confidence)
            .ToList();
    }

    private static void AddThermal(List<SlownessFactor> factors, string id, string titleKey, SensorReading reading, double threshold)
    {
        if (AnalysisMetrics.Temperature(reading) is not double celsius || celsius < threshold) return;
        factors.Add(new SlownessFactor(id,
            TextRef.Of(titleKey),
            TextRef.Of(DiagText.SlowThermalEvidence, celsius, threshold),
            TextRef.Of(DiagText.SlowThermalWhy),
            TextRef.Of(DiagText.SlowThermalWhatToDo),
            ImpactLevel.High, ConfidenceLevel.Medium,
            Action(DiagText.ActionViewPerformance, NavigationTargets.Performance)));
    }

    private static RecommendationAction Action(string labelKey, string target, string? optimizationId = null)
        => new(TextRef.Of(labelKey), target, optimizationId);
}
