using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;

namespace PCBoost.Diagnostics.Health;

/// <summary>Échantillon utile à la détection : charge, performance du processeur, conditions d'alimentation, température.</summary>
public readonly record struct ThrottlingSample(
    DateTimeOffset Timestamp,
    double CpuPercent,
    double? ProcessorPerformancePercent,
    bool OnAcPower,
    bool PowerSaverActive,
    double? CpuTemperatureC);

/// <summary>
/// Suivi pur (testable) des épisodes : charge ≥ 70 % et performance ≤ 60 % de la fréquence de base, sur secteur et hors
/// mode Économie d'énergie, pendant au moins 20 secondes. Deux échantillons normaux consécutifs terminent l'épisode.
/// </summary>
public sealed class ThrottlingEpisodeTracker
{
    public const double LoadThresholdPercent = 70;
    public const double PerformanceThresholdPercent = 60;
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(20);
    /// <summary>Un trou plus long entre deux échantillons (veille, pause de la surveillance) interrompt l'épisode en cours.</summary>
    public static readonly TimeSpan MaximumGap = TimeSpan.FromSeconds(30);

    private DateTimeOffset? _start;
    private DateTimeOffset _lastMatch;
    private int _normalStreak;
    private double _cpuSum, _perfSum;
    private int _count;
    private double? _maxTemperature;

    /// <summary>Ajoute un échantillon ; renvoie l'épisode qui vient de se terminer, s'il est assez long.</summary>
    public ThrottlingEpisode? Add(ThrottlingSample sample)
    {
        if (_start is not null && sample.Timestamp - _lastMatch > MaximumGap)
        {
            var interrupted = Close();
            if (interrupted is not null) return interrupted;
        }

        if (IsThrottled(sample))
        {
            _start ??= sample.Timestamp;
            _lastMatch = sample.Timestamp;
            _normalStreak = 0;
            _cpuSum += sample.CpuPercent;
            _perfSum += sample.ProcessorPerformancePercent!.Value;
            _count++;
            if (sample.CpuTemperatureC is { } t) _maxTemperature = Math.Max(_maxTemperature ?? t, t);
            return null;
        }

        if (_start is null) return null;
        _normalStreak++;
        return _normalStreak >= 2 ? Close() : null;
    }

    /// <summary>Termine l'épisode en cours (arrêt de la surveillance).</summary>
    public ThrottlingEpisode? Close()
    {
        ThrottlingEpisode? episode = null;
        if (_start is { } start && _count > 0 && _lastMatch - start >= MinimumDuration)
            episode = new ThrottlingEpisode(start, _lastMatch - start, _cpuSum / _count, _perfSum / _count, _maxTemperature);
        _start = null;
        _normalStreak = 0;
        _cpuSum = _perfSum = 0;
        _count = 0;
        _maxTemperature = null;
        return episode;
    }

    public static bool IsThrottled(ThrottlingSample sample)
        => sample.OnAcPower && !sample.PowerSaverActive
           && sample.CpuPercent >= LoadThresholdPercent
           && sample.ProcessorPerformancePercent is { } perf && perf <= PerformanceThresholdPercent;
}

/// <summary>
/// Détection en continu à partir des échantillons du moniteur de performances. Notification au plus une fois par jour.
/// Les conditions d'alimentation (secteur, mode Économie d'énergie) sont relues au plus toutes les minutes.
/// </summary>
public sealed class ThermalThrottlingDetector : IThermalThrottlingDetector
{
    internal const string LastEpisodeKey = "health.thermal.last-episode";
    internal const string LastNotificationKey = "health.thermal.last-notified";
    internal static readonly TimeSpan PowerRefreshInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan NotificationCooldown = TimeSpan.FromDays(1);

    private readonly IPerformanceMonitor _monitor;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly IPowerProvider _power;
    private readonly IKeyValueStore _store;
    private readonly INotificationService _notifications;
    private readonly IClock _clock;
    private readonly ILogger<ThermalThrottlingDetector> _logger;
    private readonly ThrottlingEpisodeTracker _tracker = new();
    private readonly Lock _lock = new();
    private DateTimeOffset _powerReadAt = DateTimeOffset.MinValue;
    private bool _onAc = true, _powerSaver;
    private bool _running, _loaded;
    private int _episodes;

    public ThermalThrottlingDetector(IPerformanceMonitor monitor, ISystemInfoProvider systemInfo, IPowerProvider power, IKeyValueStore store,
        INotificationService notifications, IClock clock, ILogger<ThermalThrottlingDetector>? logger = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _power = power ?? throw new ArgumentNullException(nameof(power));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? NullLogger<ThermalThrottlingDetector>.Instance;
    }

    public int EpisodeCount
    {
        get { lock (_lock) return _episodes; }
    }

    public ThrottlingEpisode? LastEpisode { get; private set; }

    public event EventHandler<ThrottlingEpisode>? EpisodeDetected;

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
        }
        _monitor.SampleAvailable += OnSample;
        if (!_loaded)
        {
            _loaded = true;
            _ = LoadLastEpisodeAsync();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
        }
        _monitor.SampleAvailable -= OnSample;
        ThrottlingEpisode? episode;
        lock (_lock) episode = _tracker.Close();
        if (episode is not null) _ = RecordAsync(episode);
    }

    public void Dispose() => Stop();

    private void OnSample(object? sender, SystemMetricsSample sample)
    {
        try
        {
            RefreshPowerIfNeeded(sample.Timestamp);
            var temperature = _monitor.Temperatures.Cpu is { HasValue: true } t ? t.Value : null;
            ThrottlingEpisode? episode;
            lock (_lock)
            {
                episode = _tracker.Add(new ThrottlingSample(sample.Timestamp, sample.CpuPercent, sample.ProcessorPerformancePercent, _onAc, _powerSaver, temperature));
            }
            if (episode is not null) _ = RecordAsync(episode);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Échantillon ignoré par la détection de limitation thermique");
        }
    }

    private void RefreshPowerIfNeeded(DateTimeOffset now)
    {
        if (now - _powerReadAt < PowerRefreshInterval) return;
        _powerReadAt = now;
        var status = _systemInfo.GetPowerStatus();
        _onAc = status.Source != PowerSource.Battery;
        _powerSaver = _power.GetActiveScheme()?.Id == PowerScheme.PowerSaver;
    }

    internal async Task RecordAsync(ThrottlingEpisode episode)
    {
        lock (_lock) _episodes++;
        LastEpisode = episode;
        EpisodeDetected?.Invoke(this, episode);
        _logger.LogInformation("Limitation probable du processeur : {Seconds} s à {Performance:F0} % de la fréquence de base sous {Cpu:F0} % de charge.",
            episode.Duration.TotalSeconds, episode.AverageProcessorPerformancePercent, episode.AverageCpuPercent);
        try
        {
            await _store.SetAsync(LastEpisodeKey, episode).ConfigureAwait(false);
            var last = await _store.GetAsync<DateTimeOffset?>(LastNotificationKey).ConfigureAwait(false);
            var now = _clock.UtcNow;
            if (last is { } previous && now - previous < NotificationCooldown) return;
            _notifications.Show(new NotificationRequest(
                TextRef.Of(DiagText.HealthNotifyThermalTitle),
                TextRef.Of(DiagText.HealthNotifyThermalBody, Math.Round(episode.AverageProcessorPerformancePercent), Math.Round(episode.Duration.TotalSeconds)),
                ActionId: "navigate:health",
                ActionLabel: TextRef.Of(DiagText.HealthNotifyOpen),
                Tag: "health-thermal"));
            await _store.SetAsync<DateTimeOffset?>(LastNotificationKey, now).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Épisode de limitation non enregistré");
        }
    }

    private async Task LoadLastEpisodeAsync()
    {
        try
        {
            LastEpisode ??= await _store.GetAsync<ThrottlingEpisode>(LastEpisodeKey).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Dernier épisode de limitation illisible");
        }
    }
}
