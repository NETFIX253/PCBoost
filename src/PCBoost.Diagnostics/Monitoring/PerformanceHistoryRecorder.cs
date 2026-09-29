using Microsoft.Extensions.Logging;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;

namespace PCBoost.Diagnostics.Monitoring;

/// <summary>
/// Historique persistant des performances (§66) : agrège les échantillons du moniteur par minute (moyennes) et enregistre
/// un <see cref="PerformanceSnapshot"/> par minute écoulée. Purge les instantanés de plus de 30 jours une fois par jour.
/// Aucun instantané n'est produit pendant que la surveillance est en pause (pas de données, pas de valeur supposée).
/// L'application appelle <see cref="Start"/> au démarrage et <see cref="FlushAsync"/> (ou <see cref="Stop"/>) à la fermeture.
/// </summary>
public sealed class PerformanceHistoryRecorder : IPerformanceHistoryService, IDisposable
{
    private readonly IPerformanceMonitor _monitor;
    private readonly IPerformanceSnapshotRepository _repository;
    private readonly IClock _clock;
    private readonly ILogger<PerformanceHistoryRecorder> _logger;
    private readonly PerformanceHistoryOptions _options;

    private readonly Lock _lock = new();
    private MinuteBucket _bucket;
    private Task _writes = Task.CompletedTask;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;
    private bool _recording;

    public PerformanceHistoryRecorder(IPerformanceMonitor monitor, IPerformanceSnapshotRepository repository, IClock clock,
        ILogger<PerformanceHistoryRecorder> logger, PerformanceHistoryOptions? options = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new PerformanceHistoryOptions();
    }

    public bool IsRecording
    {
        get { lock (_lock) return _recording; }
    }

    /// <summary>S'abonne aux échantillons du moniteur (ne démarre pas le moniteur lui-même) et planifie la purge.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_recording) return;
            _recording = true;
            _monitor.SampleAvailable += OnSampleAvailable;
            EnqueueLocked(null);
        }
    }

    /// <summary>Se désabonne et enregistre la minute en cours (moyenne partielle) si elle contient des échantillons.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_recording) return;
            _recording = false;
            _monitor.SampleAvailable -= OnSampleAvailable;
            FlushBucketLocked();
        }
    }

    /// <summary>Enregistre la minute en cours et attend la fin des écritures en attente.</summary>
    public Task FlushAsync()
    {
        Task pending;
        lock (_lock)
        {
            FlushBucketLocked();
            pending = _writes;
        }
        return pending;
    }

    public async Task<IReadOnlyList<PerformanceSnapshot>> GetHistoryAsync(TimeSpan window, CancellationToken cancellationToken = default)
    {
        if (window <= TimeSpan.Zero) return [];
        var now = _clock.UtcNow;
        return await _repository.GetRangeAsync(now - window, now, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => Stop();

    /// <summary>Ajoute un échantillon à la minute en cours ; une minute terminée est enregistrée en arrière-plan.</summary>
    internal void Record(SystemMetricsSample sample, TemperatureReadings temperatures)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var minute = TruncateToMinute(sample.Timestamp);
        lock (_lock)
        {
            if (_bucket.Count > 0 && minute != _bucket.Minute) FlushBucketLocked();
            if (_bucket.Count == 0) _bucket = new MinuteBucket(minute);
            _bucket.Add(sample, AnalysisMetrics.Temperature(temperatures.Cpu), AnalysisMetrics.Temperature(temperatures.Gpu));
        }
    }

    internal static DateTimeOffset TruncateToMinute(DateTimeOffset timestamp)
    {
        var utc = timestamp.UtcTicks;
        return new DateTimeOffset(utc - utc % TimeSpan.TicksPerMinute, TimeSpan.Zero);
    }

    private void OnSampleAvailable(object? sender, SystemMetricsSample sample)
    {
        try
        {
            Record(sample, _monitor.Temperatures);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Échantillon ignoré par l'historique : {ErrorType} {Message}", ex.GetType().Name, PrivacyRedactor.Redact(ex.Message));
        }
    }

    private void FlushBucketLocked()
    {
        if (_bucket.Count == 0) return;
        var snapshot = _bucket.ToSnapshot();
        _bucket = default;
        EnqueueLocked(snapshot);
    }

    /// <summary>Chaîne les écritures (ordre conservé, jamais concurrentes). <paramref name="snapshot"/> null = purge seule.</summary>
    private void EnqueueLocked(PerformanceSnapshot? snapshot)
        => _writes = _writes.ContinueWith(_ => PersistAsync(snapshot), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

    private async Task PersistAsync(PerformanceSnapshot? snapshot)
    {
        if (snapshot is not null)
        {
            try
            {
                await _repository.AddRangeAsync([snapshot]).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Instantané de performance non enregistré : {ErrorType} {Message}", ex.GetType().Name, PrivacyRedactor.Redact(ex.Message));
            }
        }

        var now = _clock.UtcNow;
        if (now - _lastPurge < _options.PurgeInterval) return;
        _lastPurge = now;
        try
        {
            var removed = await _repository.PurgeOlderThanAsync(now - _options.Retention).ConfigureAwait(false);
            if (removed > 0) _logger.LogInformation("Historique des performances : {Count} instantanés anciens supprimés.", removed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Purge de l'historique des performances impossible : {ErrorType} {Message}", ex.GetType().Name, PrivacyRedactor.Redact(ex.Message));
        }
    }

    /// <summary>Sommes d'une minute d'échantillons (valeurs non mesurées exclues des moyennes).</summary>
    private struct MinuteBucket
    {
        private double _cpu, _memory, _disk, _gpu, _cpuTemp, _gpuTemp;
        private int _memoryCount, _diskCount, _gpuCount, _cpuTempCount, _gpuTempCount;

        public MinuteBucket(DateTimeOffset minute) => Minute = minute;

        public DateTimeOffset Minute { get; }

        public int Count { get; private set; }

        public void Add(SystemMetricsSample s, double? cpuTemperature, double? gpuTemperature)
        {
            Count++;
            _cpu += Finite(s.CpuPercent) ?? 0;
            if (s.MemoryTotalBytes > 0 && Finite(s.MemoryUsedPercent) is double m) { _memory += m; _memoryCount++; }
            if (Finite(s.DiskActivePercent) is double d) { _disk += d; _diskCount++; }
            if (Finite(s.GpuPercent) is double g) { _gpu += g; _gpuCount++; }
            if (cpuTemperature is double ct) { _cpuTemp += ct; _cpuTempCount++; }
            if (gpuTemperature is double gt) { _gpuTemp += gt; _gpuTempCount++; }
        }

        public readonly PerformanceSnapshot ToSnapshot() => new(
            Minute,
            Math.Round(_cpu / Count, 2),
            _memoryCount > 0 ? Math.Round(_memory / _memoryCount, 2) : 0,
            Average(_disk, _diskCount),
            Average(_gpu, _gpuCount),
            Average(_cpuTemp, _cpuTempCount),
            Average(_gpuTemp, _gpuTempCount),
            Fps: null,
            FrameTimeMs: null);

        private static double? Average(double sum, int count) => count > 0 ? Math.Round(sum / count, 2) : null;

        private static double? Finite(double? value) => value is double v && double.IsFinite(v) ? v : null;
    }
}
