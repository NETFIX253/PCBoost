using Microsoft.Extensions.Logging;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;

namespace PCBoost.Diagnostics.Monitoring;

/// <summary>
/// Surveillance temps réel adaptative (§22, §47). Un <see cref="PeriodicTimer"/> tourne sur le pool de threads :
/// Active = 1 s, Background = 5 s, Paused = aucun échantillonnage (minuterie suspendue). Températures toutes les 5 s en
/// Active, 30 s en Background. Historique en mémoire dans un tampon circulaire de 30 min (thread-safe, pré-alloué).
/// Les événements sont déclenchés sur le pool de threads : l'interface doit revenir sur son thread avant d'afficher.
/// </summary>
public sealed class PerformanceMonitor : IPerformanceMonitor
{
    private const int MaxSampleCapacity = 36_000;
    private const int MaxTemperatureCapacity = 7_200;

    private static readonly TemperatureReadings NotYetRead = new(
        SensorReading.Unavailable(Availability.Unavailable),
        SensorReading.Unavailable(Availability.Unavailable),
        SensorReading.Unavailable(Availability.Unavailable));

    private readonly ISystemMetricsProvider _metrics;
    private readonly IHardwareProvider _hardware;
    private readonly IClock _clock;
    private readonly ILogger<PerformanceMonitor> _logger;
    private readonly PerformanceMonitorOptions _options;

    private readonly Lock _stateLock = new();
    private readonly Lock _historyLock = new();
    private readonly Lock _tickLock = new();
    private readonly RingBuffer<SystemMetricsSample> _samples;
    private readonly RingBuffer<TemperaturePoint> _temperatureHistory;

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private MonitoringMode _mode = MonitoringMode.Background;
    private volatile SystemMetricsSample? _latest;
    private volatile TemperatureReadings _temperatures = NotYetRead;
    private DateTimeOffset _lastTemperatureRead = DateTimeOffset.MinValue;
    private bool _samplingErrorLogged;
    private bool _temperatureErrorLogged;
    private bool _disposed;

    public PerformanceMonitor(ISystemMetricsProvider metrics, IHardwareProvider hardware, IClock clock, ILogger<PerformanceMonitor> logger,
        PerformanceMonitorOptions? options = null)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new PerformanceMonitorOptions();

        ValidateInterval(_options.ActiveInterval, nameof(_options.ActiveInterval));
        ValidateInterval(_options.BackgroundInterval, nameof(_options.BackgroundInterval));
        ValidateInterval(_options.ActiveTemperatureInterval, nameof(_options.ActiveTemperatureInterval));
        ValidateInterval(_options.BackgroundTemperatureInterval, nameof(_options.BackgroundTemperatureInterval));
        ValidateInterval(_options.HistoryDuration, nameof(_options.HistoryDuration));

        _samples = new RingBuffer<SystemMetricsSample>(Capacity(_options.HistoryDuration, _options.ActiveInterval, MaxSampleCapacity));
        _temperatureHistory = new RingBuffer<TemperaturePoint>(Capacity(_options.HistoryDuration, _options.ActiveTemperatureInterval, MaxTemperatureCapacity));
    }

    public MonitoringMode Mode
    {
        get { lock (_stateLock) return _mode; }
    }

    public bool IsRunning
    {
        get { lock (_stateLock) return _timer is not null; }
    }

    public SystemMetricsSample? Latest => _latest;

    public TemperatureReadings Temperatures => _temperatures;

    /// <summary>Intervalle d'échantillonnage du mode courant (<see cref="Timeout.InfiniteTimeSpan"/> en pause).</summary>
    public TimeSpan CurrentInterval => IntervalFor(Mode);

    public event EventHandler<SystemMetricsSample>? SampleAvailable;

    public event EventHandler? ModeChanged;

    public void Start()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null) return;
            var cts = new CancellationTokenSource();
            var timer = new PeriodicTimer(IntervalFor(_mode));
            var sampleNow = _mode != MonitoringMode.Paused;
            _cts = cts;
            _timer = timer;
            _ = Task.Run(() => RunAsync(timer, cts, sampleNow));
        }
        _logger.LogDebug("Surveillance démarrée.");
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (_timer is null) return;
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // La boucle s'est déjà terminée (erreur inattendue) et a libéré sa source d'annulation.
            }
            _timer.Dispose();
            _timer = null;
            _cts = null;
        }
        _logger.LogDebug("Surveillance arrêtée.");
    }

    public void SetMode(MonitoringMode mode)
    {
        lock (_stateLock)
        {
            // Après Dispose (fermeture de l'application), un changement de mode tardif de l'interface est ignoré.
            if (_disposed || _mode == mode) return;
            _mode = mode;
            // Modifier la période redémarre la minuterie ; en pause, elle ne se déclenche plus du tout.
            if (_timer is not null) _timer.Period = IntervalFor(mode);
        }
        ThreadPool.UnsafeQueueUserWorkItem(static monitor => monitor.RaiseModeChanged(), this, preferLocal: false);
    }

    public IReadOnlyList<SystemMetricsSample> GetHistory(TimeSpan window)
    {
        var cutoff = Cutoff(window);
        var result = new List<SystemMetricsSample>();
        if (cutoff is null) return result;
        lock (_historyLock)
        {
            for (var i = 0; i < _samples.Count; i++)
            {
                var sample = _samples[i];
                if (sample.Timestamp >= cutoff.Value) result.Add(sample);
            }
        }
        return result;
    }

    public MetricStatistics GetStatistics(MetricKind metric, TimeSpan window)
    {
        var cutoff = Cutoff(window);
        if (cutoff is null) return new MetricStatistics(metric, null, null, null, 0);

        double sum = 0, max = double.MinValue;
        double? current = null;
        var count = 0;
        lock (_historyLock)
        {
            if (metric is MetricKind.CpuTemperature or MetricKind.GpuTemperature)
            {
                for (var i = 0; i < _temperatureHistory.Count; i++)
                {
                    var point = _temperatureHistory[i];
                    if (point.Timestamp < cutoff.Value) continue;
                    Accumulate(metric == MetricKind.CpuTemperature ? point.Cpu : point.Gpu);
                }
            }
            else
            {
                for (var i = 0; i < _samples.Count; i++)
                {
                    var sample = _samples[i];
                    if (sample.Timestamp < cutoff.Value) continue;
                    Accumulate(Select(metric, sample));
                }
            }
        }

        return count == 0
            ? new MetricStatistics(metric, null, null, null, 0)
            : new MetricStatistics(metric, current, sum / count, max, count);

        void Accumulate(double? value)
        {
            if (value is not double v || !double.IsFinite(v)) return;
            sum += v;
            count++;
            if (v > max) max = v;
            current = v; // parcours du plus ancien au plus récent : la dernière valeur mesurée gagne
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_stateLock) _disposed = true;
    }

    /// <summary>Prend un échantillon (et les températures si leur intervalle est écoulé), puis notifie. Appelé par la minuterie.</summary>
    internal void Tick()
    {
        SystemMetricsSample? sample;
        lock (_tickLock)
        {
            sample = TakeSample();
            if (sample is null) return;
            lock (_historyLock) _samples.Add(sample);
            _latest = sample;

            var now = _clock.UtcNow;
            if (now - _lastTemperatureRead >= TemperatureIntervalFor(Mode))
            {
                _lastTemperatureRead = now;
                ReadTemperatures(now);
            }
        }

        try
        {
            SampleAvailable?.Invoke(this, sample);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Un abonné à la surveillance a levé une exception : {ErrorType} {Message}", ex.GetType().Name, PrivacyRedactor.Redact(ex.Message));
        }
    }

    internal TimeSpan IntervalFor(MonitoringMode mode) => mode switch
    {
        MonitoringMode.Active => _options.ActiveInterval,
        MonitoringMode.Background => _options.BackgroundInterval,
        _ => Timeout.InfiniteTimeSpan,
    };

    internal TimeSpan TemperatureIntervalFor(MonitoringMode mode)
        => mode == MonitoringMode.Active ? _options.ActiveTemperatureInterval : _options.BackgroundTemperatureInterval;

    private async Task RunAsync(PeriodicTimer timer, CancellationTokenSource cts, bool sampleImmediately)
    {
        var token = cts.Token;
        try
        {
            if (sampleImmediately) Tick();
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (Mode == MonitoringMode.Paused) continue;
                Tick();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Arrêt demandé.
        }
        catch (Exception ex)
        {
            _logger.LogError("La boucle de surveillance s'est arrêtée : {ErrorType} {Message}", ex.GetType().Name, PrivacyRedactor.Redact(ex.Message));
        }
        finally
        {
            cts.Dispose();
        }
    }

    private SystemMetricsSample? TakeSample()
    {
        try
        {
            return _metrics.Sample();
        }
        catch (Exception ex)
        {
            if (!_samplingErrorLogged)
            {
                _samplingErrorLogged = true;
                _logger.LogWarning("Échantillonnage des métriques impossible : {ErrorType} {Message}", ex.GetType().Name, PrivacyRedactor.Redact(ex.Message));
            }
            return null;
        }
    }

    private void ReadTemperatures(DateTimeOffset now)
    {
        try
        {
            var readings = _hardware.GetTemperatures();
            _temperatures = readings;
            var point = new TemperaturePoint(now, AnalysisMetrics.Temperature(readings.Cpu), AnalysisMetrics.Temperature(readings.Gpu));
            lock (_historyLock) _temperatureHistory.Add(point);
        }
        catch (Exception ex)
        {
            if (!_temperatureErrorLogged)
            {
                _temperatureErrorLogged = true;
                _logger.LogWarning("Lecture des températures impossible : {ErrorType} {Message}", ex.GetType().Name, PrivacyRedactor.Redact(ex.Message));
            }
        }
    }

    private void RaiseModeChanged()
    {
        try
        {
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Un abonné au changement de mode a levé une exception : {ErrorType} {Message}", ex.GetType().Name, PrivacyRedactor.Redact(ex.Message));
        }
    }

    private DateTimeOffset? Cutoff(TimeSpan window)
    {
        if (window <= TimeSpan.Zero) return null;
        if (window > _options.HistoryDuration) window = _options.HistoryDuration;
        return _clock.UtcNow - window;
    }

    private static double? Select(MetricKind metric, SystemMetricsSample sample) => metric switch
    {
        MetricKind.Cpu => sample.CpuPercent,
        MetricKind.Memory => sample.MemoryTotalBytes > 0 ? sample.MemoryUsedPercent : null,
        MetricKind.Disk => sample.DiskActivePercent,
        MetricKind.Gpu => sample.GpuPercent,
        MetricKind.NetworkReceive => sample.NetworkReceiveBytesPerSec,
        MetricKind.NetworkSend => sample.NetworkSendBytesPerSec,
        _ => null,
    };

    private static int Capacity(TimeSpan duration, TimeSpan interval, int max)
        => (int)Math.Clamp(Math.Ceiling(duration / interval) + 1, 16, max);

    private static void ValidateInterval(TimeSpan value, string name)
    {
        if (value < TimeSpan.FromMilliseconds(1) || value.TotalMilliseconds >= uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(name, value, "L'intervalle doit être compris entre 1 ms et environ 49 jours.");
    }

    private readonly record struct TemperaturePoint(DateTimeOffset Timestamp, double? Cpu, double? Gpu);
}
