using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Cleanup;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;

namespace PCBoost.Diagnostics.Tests.TestSupport;

internal sealed class FakeStartupService : IStartupService
{
    public IReadOnlyList<StartupEntry> Entries { get; set; } = Reports.StartupEntries(4, 2);
    public Exception? Throw { get; set; }
    public int Calls { get; private set; }

    public Task<IReadOnlyList<StartupEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (Throw is not null) throw Throw;
        return Task.FromResult(Entries);
    }

    public Task<OperationResult> SetEnabledAsync(StartupEntry entry, bool enabled, CancellationToken cancellationToken = default)
        => Task.FromResult(OperationResult.Fail(OperationErrorKind.NotSupported));
}

internal sealed class FakeCleanupService : ICleanupService
{
    public List<CleanupCategory> Categories { get; } =
    [
        new("user-temp", TextRef.Of("Cleanup_user-temp_Name"), TextRef.Of("Cleanup_user-temp_Description"), SafetyCategory.Safe, false, true, TimeSpan.FromDays(1)),
        new("recycle-bin", TextRef.Of("Cleanup_recycle-bin_Name"), TextRef.Of("Cleanup_recycle-bin_Description"), SafetyCategory.Safe, false, false, TimeSpan.Zero),
        new("windows-temp", TextRef.Of("Cleanup_windows-temp_Name"), TextRef.Of("Cleanup_windows-temp_Description"), SafetyCategory.Safe, true, true, TimeSpan.FromDays(1)),
        new("system-crash-dumps", TextRef.Of("Cleanup_system-crash-dumps_Name"), TextRef.Of("Cleanup_system-crash-dumps_Description"), SafetyCategory.Caution, true, false, TimeSpan.Zero),
    ];

    public Dictionary<string, CleanupScanResult> Results { get; } = new()
    {
        ["user-temp"] = new("user-temp", 300 * ByteSize.MiB, 120, true, null, false),
        ["recycle-bin"] = new("recycle-bin", 200 * ByteSize.MiB, 10, true, null, false),
        ["windows-temp"] = new("windows-temp", 900 * ByteSize.MiB, 50, false, TextRef.Of("Opt_RequiresElevation"), true),
        ["system-crash-dumps"] = new("system-crash-dumps", 5L * ByteSize.GiB, 2, true, null, true),
    };

    public Exception? Throw { get; set; }
    public IReadOnlyCollection<string>? LastRequestedIds { get; private set; }

    public IReadOnlyList<CleanupCategory> GetCategories() => Categories;

    public Task<IReadOnlyList<CleanupScanResult>> ScanAsync(IReadOnlyCollection<string>? categoryIds = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Throw is not null) throw Throw;
        LastRequestedIds = categoryIds?.ToList();
        // Renvoie volontairement tous les résultats : l'analyseur doit lui-même ne garder que les catégories SAFE.
        return Task.FromResult<IReadOnlyList<CleanupScanResult>>(Results.Values.ToList());
    }

    public Task<CleanupSummary> CleanAsync(IReadOnlyCollection<string> categoryIds, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("L'analyse ne doit jamais nettoyer.");
}

internal sealed class FakeProcessService : IProcessService
{
    public List<ProcessInfo> Processes { get; } = [];
    public Exception? Throw { get; set; }

    public FakeProcessService Add(int pid, string name, double cpu = 0, long memory = 50 * ByteSize.MiB, bool hasWindow = false,
        bool currentUser = true, int session = 1, ProtectionLevel protection = ProtectionLevel.None, string? path = null)
    {
        Processes.Add(new ProcessInfo(pid, name, path, cpu, memory, null, ProcessPriority.Normal, hasWindow, hasWindow ? name : null,
            currentUser, session, null, new TrustAssessment(TrustLevel.Unknown, SignatureInfo.NotChecked, null, null, false, false),
            new ProtectionInfo(protection, null)));
        return this;
    }

    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Throw is not null) throw Throw;
        return Task.FromResult<IReadOnlyList<ProcessInfo>>(Processes.ToList());
    }

    public OperationResult CloseApplication(int processId) => throw new InvalidOperationException("L'analyse ne ferme aucun processus.");

    public OperationResult TerminateProcess(int processId) => throw new InvalidOperationException("L'analyse ne termine aucun processus.");

    public ProtectionInfo GetProtection(string processName, string? executablePath) => ProtectionInfo.None;
}

/// <summary>Moniteur piloté par le test (historique fourni, événements déclenchés à la main).</summary>
internal sealed class FakePerformanceMonitor : IPerformanceMonitor
{
    public MonitoringMode Mode { get; set; } = MonitoringMode.Background;
    public bool IsRunning { get; set; }
    public SystemMetricsSample? Latest { get; set; }
    public TemperatureReadings Temperatures { get; set; } = TemperatureReadings.None;
    public TimeSpan CurrentInterval => TimeSpan.FromSeconds(1);
    public List<SystemMetricsSample> History { get; } = [];
    public Func<DateTimeOffset>? Now { get; set; }

    public event EventHandler<SystemMetricsSample>? SampleAvailable;
    public event EventHandler? ModeChanged;

    public void Start() => IsRunning = true;
    public void Stop() => IsRunning = false;
    public void SetMode(MonitoringMode mode) { Mode = mode; ModeChanged?.Invoke(this, EventArgs.Empty); }

    public IReadOnlyList<SystemMetricsSample> GetHistory(TimeSpan window)
    {
        var now = Now?.Invoke() ?? History.LastOrDefault()?.Timestamp ?? DateTimeOffset.UtcNow;
        return History.Where(s => s.Timestamp >= now - window).ToList();
    }

    public MetricStatistics GetStatistics(MetricKind metric, TimeSpan window) => new(metric, null, null, null, 0);

    public void Raise(SystemMetricsSample sample) => SampleAvailable?.Invoke(this, sample);

    public int SubscriberCount => SampleAvailable?.GetInvocationList().Length ?? 0;

    public void Dispose() => IsRunning = false;
}

/// <summary>Capteurs de température avec compteur d'appels.</summary>
internal sealed class CountingHardwareProvider : IHardwareProvider
{
    public TemperatureReadings Readings { get; set; } = new(SensorReading.Of(55, "test"), SensorReading.Of(60, "test"), SensorReading.Unavailable(Availability.NoSensor));
    public int Calls { get; private set; }
    public bool Throw { get; set; }

    public TemperatureReadings GetTemperatures()
    {
        Calls++;
        if (Throw) throw new UnauthorizedAccessException("capteurs");
        return Readings;
    }
}

/// <summary>Métriques qui lèvent systématiquement (capteur absent, accès refusé).</summary>
internal sealed class ThrowingMetricsProvider : ISystemMetricsProvider
{
    public int Calls { get; private set; }
    public SystemMetricsSample Sample()
    {
        Calls++;
        throw new UnauthorizedAccessException("pdh");
    }
    public void Dispose() { }
}

/// <summary>IProgress synchrone (Progress&lt;T&gt; poste sur le contexte : non déterministe en test).</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    public List<T> Values { get; } = [];
    public void Report(T value) { lock (Values) Values.Add(value); }
}

/// <summary>Analyseur piloté par le test pour le diagnostic « PC lent ».</summary>
internal sealed class FakeSystemAnalyzer : ISystemAnalyzer
{
    public SystemAnalysisReport? LastReport { get; set; }
    public SystemAnalysisReport NextReport { get; set; } = Reports.Healthy();
    public List<AnalysisOptions> Calls { get; } = [];

    public event EventHandler<SystemAnalysisReport>? AnalysisCompleted;

    public Task<SystemAnalysisReport> AnalyzeAsync(AnalysisOptions options, IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(options);
        LastReport = NextReport;
        AnalysisCompleted?.Invoke(this, NextReport);
        return Task.FromResult(NextReport);
    }
}
