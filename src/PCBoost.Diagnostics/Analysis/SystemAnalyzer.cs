using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;

namespace PCBoost.Diagnostics.Analysis;

/// <summary>
/// Analyse complète du PC (§8), en lecture seule. Étapes : System (informations + échantillonnage de la charge) → Startup →
/// Storage (scan de nettoyage SAFE) → Processes → Results (profil, constats, score, historique).
/// La charge est mesurée en premier, avant les étapes coûteuses, pour que l'analyse elle-même ne fausse pas la mesure.
/// Chaque sous-étape est isolée : une erreur (permission refusée, service absent…) est journalisée et l'analyse continue
/// avec une valeur vide ou nulle. Seule l'annulation demandée par l'appelant interrompt l'analyse.
/// </summary>
public sealed class SystemAnalyzer : ISystemAnalyzer
{
    private const double SystemStageEnd = 40, StartupStageEnd = 55, StorageStageEnd = 80, ProcessesStageEnd = 95;

    private static readonly OsInfo UnknownOs = new(string.Empty, string.Empty, 0, 0, ProcessorArchitecture.Unknown, false, false, TimeSpan.Zero, false);
    private static readonly CpuInfo UnknownCpu = new(string.Empty, string.Empty, 0, 0, null, null, null);
    private static readonly MemoryInfo UnknownMemory = new(0, 0, 0, 0, null, null);
    private static readonly PowerStatus UnknownPower = new(PowerSource.Unknown, null, false);
    private static readonly TemperatureReadings UnreadTemperatures = new(
        SensorReading.Unavailable(Availability.Unavailable),
        SensorReading.Unavailable(Availability.Unavailable),
        SensorReading.Unavailable(Availability.Unavailable));

    private readonly ISystemInfoProvider _systemInfo;
    private readonly IHardwareProvider _hardware;
    private readonly IPowerProvider _power;
    private readonly ISystemMetricsProvider _metrics;
    private readonly IPerformanceMonitor _monitor;
    private readonly IStartupService _startup;
    private readonly ICleanupService _cleanup;
    private readonly IProcessService _processes;
    private readonly IProcessProvider _processProvider;
    private readonly IHardwareProfileClassifier _classifier;
    private readonly IPerformanceScoreCalculator _scoreCalculator;
    private readonly IHealthRulesEngine _rules;
    private readonly ISettingsService _settings;
    private readonly IScanHistoryRepository _scanHistory;
    private readonly IClock _clock;
    private readonly ILogger<SystemAnalyzer> _logger;
    private readonly SystemAnalyzerOptions _options;
    private volatile SystemAnalysisReport? _lastReport;

    public SystemAnalyzer(
        ISystemInfoProvider systemInfo,
        IHardwareProvider hardware,
        IPowerProvider power,
        ISystemMetricsProvider metrics,
        IPerformanceMonitor monitor,
        IStartupService startup,
        ICleanupService cleanup,
        IProcessService processes,
        IProcessProvider processProvider,
        IHardwareProfileClassifier classifier,
        IPerformanceScoreCalculator scoreCalculator,
        IHealthRulesEngine rules,
        ISettingsService settings,
        IScanHistoryRepository scanHistory,
        IClock clock,
        ILogger<SystemAnalyzer> logger,
        SystemAnalyzerOptions? options = null)
    {
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        _power = power ?? throw new ArgumentNullException(nameof(power));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _processProvider = processProvider ?? throw new ArgumentNullException(nameof(processProvider));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _scoreCalculator = scoreCalculator ?? throw new ArgumentNullException(nameof(scoreCalculator));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _scanHistory = scanHistory ?? throw new ArgumentNullException(nameof(scanHistory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new SystemAnalyzerOptions();
    }

    public SystemAnalysisReport? LastReport => _lastReport;

    public event EventHandler<SystemAnalysisReport>? AnalysisCompleted;

    public async Task<SystemAnalysisReport> AnalyzeAsync(AnalysisOptions options, IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        // 1. Système : informations statiques puis échantillonnage de la charge.
        Report(progress, AnalysisStage.System, 0);
        var os = Try("os", _systemInfo.GetOsInfo, UnknownOs);
        var cpu = Try("cpu", _systemInfo.GetCpuInfo, UnknownCpu);
        var memory = Try("memory", _systemInfo.GetMemoryInfo, UnknownMemory);
        var gpus = Try("gpus", _systemInfo.GetGpus, []);
        var drives = Try("drives", _systemInfo.GetDrives, []);
        var powerStatus = Try("power-status", _systemInfo.GetPowerStatus, UnknownPower);
        var scheme = Try<PowerScheme?>("power-scheme", _power.GetActiveScheme, null);
        var installedPrograms = Try<int?>("installed-programs", _systemInfo.GetInstalledProgramCount, null);
        var temperatures = Try("temperatures", _hardware.GetTemperatures, UnreadTemperatures);
        Report(progress, AnalysisStage.System, 5);

        var load = await ObserveLoadAsync(options.LoadSamplingDuration ?? _options.DefaultLoadSamplingDuration, memory, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // 2. Démarrage.
        Report(progress, AnalysisStage.Startup, SystemStageEnd);
        IReadOnlyList<StartupEntry> startupEntries = [];
        if (options.IncludeStartup)
            startupEntries = await TryAsync("startup", ct => _startup.GetEntriesAsync(ct), (IReadOnlyList<StartupEntry>)[], cancellationToken).ConfigureAwait(false);

        // 3. Stockage : octets récupérables par les catégories SAFE.
        Report(progress, AnalysisStage.Storage, StartupStageEnd);
        long? cleanableBytes = null;
        if (options.IncludeCleanupScan)
            cleanableBytes = await TryAsync<long?>("cleanup-scan", ScanCleanableBytesAsync, null, cancellationToken).ConfigureAwait(false);

        // 4. Processus.
        Report(progress, AnalysisStage.Processes, StorageStageEnd);
        var processSummary = ProcessSummary.Empty;
        if (options.IncludeProcesses)
            processSummary = await TryAsync("processes", SummarizeProcessesAsync, ProcessSummary.Empty, cancellationToken).ConfigureAwait(false);

        // 5. Résultats.
        Report(progress, AnalysisStage.Results, ProcessesStageEnd);
        cancellationToken.ThrowIfCancellationRequested();
        var profile = Try<HardwareProfile?>("hardware-profile", () => _classifier.Classify(cpu, memory, drives, gpus), null);

        var report = new SystemAnalysisReport
        {
            Timestamp = _clock.UtcNow,
            Os = os,
            Cpu = cpu,
            Memory = memory,
            Gpus = gpus,
            Drives = drives,
            Load = load,
            Temperatures = temperatures,
            Power = powerStatus,
            ActivePowerScheme = scheme,
            InstalledProgramCount = installedPrograms,
            StartupEntries = startupEntries,
            RunningProcessCount = processSummary.Running,
            BackgroundProcessCount = processSummary.Background,
            TopMemoryProcesses = processSummary.TopMemory,
            TopCpuProcesses = processSummary.TopCpu,
            CleanableBytes = cleanableBytes,
            HardwareProfile = profile,
        };

        var thresholds = Thresholds();
        var findings = Try<IReadOnlyList<HealthFinding>>("health-rules", () => _rules.Evaluate(report, thresholds), []);
        var score = Try<PerformanceScore?>("score", () => _scoreCalculator.Calculate(report, thresholds), null);
        if (score is not null) await SaveScanAsync(report, score, findings, cancellationToken).ConfigureAwait(false);

        _lastReport = report;
        Report(progress, AnalysisStage.Results, 100);
        _logger.LogInformation("Analyse terminée en {ElapsedMs} ms : score {Score}, {FindingCount} constat(s).",
            stopwatch.ElapsedMilliseconds, score?.Value, findings.Count);
        RaiseCompleted(report);
        return report;
    }

    /// <summary>
    /// Charge moyenne : historique récent du moniteur s'il couvre la durée demandée (au moins N échantillons sur la moitié
    /// de la fenêtre), sinon échantillonnage direct (un échantillon d'amorçage, puis un pas régulier).
    /// </summary>
    private async Task<LoadObservation> ObserveLoadAsync(TimeSpan duration, MemoryInfo memory, IProgress<AnalysisProgress>? progress, CancellationToken cancellationToken)
    {
        var interval = _options.LoadSamplingInterval > TimeSpan.Zero ? _options.LoadSamplingInterval : TimeSpan.FromMilliseconds(500);
        if (duration < interval) duration = interval;

        try
        {
            if (_monitor.IsRunning)
            {
                var history = _monitor.GetHistory(duration);
                if (history.Count >= Math.Max(2, _options.MinimumMonitorSamples)
                    && history[^1].Timestamp - history[0].Timestamp >= duration / 2)
                {
                    Report(progress, AnalysisStage.System, SystemStageEnd);
                    return BuildObservation(history, duration, memory);
                }
            }
        }
        catch (Exception ex)
        {
            LogStepFailure("monitor-history", ex);
        }

        var target = Math.Max(1, (int)Math.Round(duration / interval));
        var samples = new List<SystemMetricsSample>(target);
        var failureLogged = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _metrics.Sample(); // amorçage : les pourcentages sont calculés depuis l'appel précédent
        }
        catch (Exception ex)
        {
            LogStepFailure("load-sampling", ex);
            failureLogged = true;
        }

        for (var i = 1; i <= target; i++)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            try
            {
                samples.Add(_metrics.Sample());
            }
            catch (Exception ex)
            {
                if (!failureLogged) LogStepFailure("load-sampling", ex);
                failureLogged = true;
            }
            Report(progress, AnalysisStage.System, 5 + (SystemStageEnd - 5) * i / target);
        }

        return BuildObservation(samples, stopwatch.Elapsed, memory);
    }

    internal static LoadObservation BuildObservation(IReadOnlyList<SystemMetricsSample> samples, TimeSpan duration, MemoryInfo memory)
    {
        double cpuSum = 0, cpuMax = 0, memorySum = 0, diskSum = 0, gpuSum = 0;
        int count = 0, memoryCount = 0, diskCount = 0, gpuCount = 0;
        foreach (var s in samples)
        {
            if (!double.IsFinite(s.CpuPercent)) continue;
            var cpu = Math.Clamp(s.CpuPercent, 0, 100);
            cpuSum += cpu;
            cpuMax = Math.Max(cpuMax, cpu);
            count++;
            if (s.MemoryTotalBytes > 0 && double.IsFinite(s.MemoryUsedPercent)) { memorySum += Math.Clamp(s.MemoryUsedPercent, 0, 100); memoryCount++; }
            if (s.DiskActivePercent is double d && double.IsFinite(d)) { diskSum += Math.Clamp(d, 0, 100); diskCount++; }
            if (s.GpuPercent is double g && double.IsFinite(g)) { gpuSum += Math.Clamp(g, 0, 100); gpuCount++; }
        }

        // Mémoire : moyenne des échantillons, sinon l'instantané de GetMemoryInfo (mesuré), sinon 0 avec mémoire totale inconnue.
        var memoryPercent = memoryCount > 0 ? memorySum / memoryCount : memory.TotalBytes > 0 ? memory.UsedPercent : 0;
        return new LoadObservation(
            CpuAveragePercent: count > 0 ? cpuSum / count : 0,
            CpuMaxPercent: cpuMax,
            MemoryUsedPercent: memoryPercent,
            DiskActiveAveragePercent: diskCount > 0 ? diskSum / diskCount : null,
            GpuAveragePercent: gpuCount > 0 ? gpuSum / gpuCount : null,
            SamplingDuration: duration,
            SampleCount: count);
    }

    private async Task<long?> ScanCleanableBytesAsync(CancellationToken cancellationToken)
    {
        var safeIds = _cleanup.GetCategories()
            .Where(c => c.Safety == SafetyCategory.Safe)
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (safeIds.Count == 0) return 0;

        var results = await _cleanup.ScanAsync(safeIds, cancellationToken).ConfigureAwait(false);
        long total = 0;
        foreach (var r in results)
        {
            if (r.Available && safeIds.Contains(r.CategoryId)) total += Math.Max(0, r.Bytes);
        }
        return total;
    }

    private async Task<ProcessSummary> SummarizeProcessesAsync(CancellationToken cancellationToken)
    {
        var processes = await _processes.GetProcessesAsync(cancellationToken).ConfigureAwait(false);
        var currentSession = Try("current-session", () => _processProvider.CurrentSessionId, -1);
        var currentProcess = Try("current-process", () => _processProvider.CurrentProcessId, -1);

        var running = 0;
        var background = 0;
        var eligible = new List<ProcessInfo>(processes.Count);
        foreach (var p in processes)
        {
            if (p.ProcessId == 0) continue; // processus inactif du système (pseudo-processus)
            running++;
            if (p.ProcessId == 4 || p.Protection.IsCritical || p.ProcessId == currentProcess) continue;
            eligible.Add(p);
            if (p.IsCurrentUser && p.SessionId == currentSession && !p.HasWindow) background++;
        }

        var topMemory = eligible.Where(p => p.MemoryBytes > 0)
            .OrderByDescending(p => p.MemoryBytes).Take(5).Select(ToUsage).ToList();
        var topCpu = eligible.Where(p => p.CpuPercent > 0 && double.IsFinite(p.CpuPercent))
            .OrderByDescending(p => p.CpuPercent).Take(5).Select(ToUsage).ToList();
        return new ProcessSummary(running, background, topMemory, topCpu);

        static ProcessUsage ToUsage(ProcessInfo p) => new(p.ProcessId, p.Name, p.ExecutablePath, p.CpuPercent, p.MemoryBytes, p.IsCurrentUser);
    }

    private async Task SaveScanAsync(SystemAnalysisReport report, PerformanceScore score, IReadOnlyList<HealthFinding> findings, CancellationToken cancellationToken)
    {
        try
        {
            var record = new ScanRecord(Guid.NewGuid(), report.Timestamp, score.Value, findings.Count, AnalysisSummaryWriter.Write(report, score, findings));
            await _scanHistory.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogStepFailure("scan-history", ex);
        }
    }

    private HealthThresholds Thresholds()
    {
        try
        {
            return _settings.Current.Thresholds ?? new HealthThresholds();
        }
        catch (Exception ex)
        {
            LogStepFailure("settings", ex);
            return new HealthThresholds();
        }
    }

    private void RaiseCompleted(SystemAnalysisReport report)
    {
        try
        {
            AnalysisCompleted?.Invoke(this, report);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Un abonné à la fin d'analyse a levé une exception : {ErrorType} {Message}", ex.GetType().Name, PrivacyRedactor.Redact(ex.Message));
        }
    }

    private T Try<T>(string step, Func<T> action, T fallback)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            LogStepFailure(step, ex);
            return fallback;
        }
    }

    private async Task<T> TryAsync<T>(string step, Func<CancellationToken, Task<T>> action, T fallback, CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogStepFailure(step, ex);
            return fallback;
        }
    }

    private void LogStepFailure(string step, Exception ex)
        => _logger.LogWarning("Étape d'analyse « {Step} » ignorée ({ErrorKind}) : {ErrorType} {Message}",
            step, OperationResult.ClassifyException(ex), ex.GetType().Name, PrivacyRedactor.Redact(ex.Message));

    private static void Report(IProgress<AnalysisProgress>? progress, AnalysisStage stage, double percent)
        => progress?.Report(new AnalysisProgress(stage, Math.Clamp(percent, 0, 100)));

    private sealed record ProcessSummary(int Running, int Background, IReadOnlyList<ProcessUsage> TopMemory, IReadOnlyList<ProcessUsage> TopCpu)
    {
        public static ProcessSummary Empty { get; } = new(0, 0, [], []);
    }
}
