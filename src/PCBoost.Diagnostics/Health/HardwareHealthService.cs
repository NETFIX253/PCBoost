using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Services;

namespace PCBoost.Diagnostics.Health;

/// <summary>
/// Relevé de la santé matérielle. Les sources lisibles sans autorisation sont lues à chaque relevé ; les compteurs de
/// fiabilité des disques (autorisation administrateur) sont conservés localement avec leur date de mesure.
/// Vérification périodique : notification si un disque est signalé en mauvais état (au plus une fois par jour et par disque).
/// </summary>
public sealed class HardwareHealthService : IHardwareHealthService
{
    internal const string ReliabilityKey = "health.disk-reliability";
    internal const string DiskAlertKey = "health.disk-alerts";
    internal static readonly TimeSpan FirmwareEventWindow = TimeSpan.FromDays(30);
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    internal static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan AlertCooldown = TimeSpan.FromDays(1);

    private readonly IHardwareHealthProvider _provider;
    private readonly IElevationService _elevation;
    private readonly IKeyValueStore _store;
    private readonly INotificationService _notifications;
    private readonly IThermalThrottlingDetector? _thermal;
    private readonly IClock _clock;
    private readonly ILogger<HardwareHealthService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Lock _timerLock = new();
    private Timer? _timer;
    private bool _disposed;

    public HardwareHealthService(IHardwareHealthProvider provider, IElevationService elevation, IKeyValueStore store, INotificationService notifications,
        IClock clock, IThermalThrottlingDetector? thermal = null, ILogger<HardwareHealthService>? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _thermal = thermal;
        _logger = logger ?? NullLogger<HardwareHealthService>.Instance;
    }

    public HardwareHealthReport? Latest { get; private set; }

    public event EventHandler<HardwareHealthReport>? ReportUpdated;

    public void Start()
    {
        lock (_timerLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timer ??= new Timer(_ => _ = CheckAsync(), null, FirstCheckDelay, CheckInterval);
        }
    }

    public void Stop()
    {
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        _refreshLock.Dispose();
    }

    public async Task<HardwareHealthReport> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = await LoadReliabilityAsync(cancellationToken).ConfigureAwait(false);
            var report = await Task.Run(() => Build(cached), cancellationToken).ConfigureAwait(false);
            Latest = report;
            ReportUpdated?.Invoke(this, report);
            return report;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<OperationResult> ReadDiskReliabilityAsync(CancellationToken cancellationToken = default)
    {
        var response = await _elevation.RunAsync(new ElevatedRequest(ElevatedHealthOperations.DiskReliability, new Dictionary<string, string>()), cancellationToken)
            .ConfigureAwait(false);
        if (!response.Outcome.Success) return response.Outcome;

        var counters = HealthElevatedData.DecodeDisks(response.Data, _clock.UtcNow);
        await _store.SetAsync(ReliabilityKey, counters.ToList(), cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return counters.Any(c => c.HasAnyValue)
            ? OperationResult.Ok()
            : OperationResult.Ok(TextRef.Of(DiagText.HealthReliabilityNotProvided));
    }

    internal async Task CheckAsync()
    {
        try
        {
            var report = await RefreshAsync().ConfigureAwait(false);
            await NotifyCriticalDisksAsync(report).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Vérification de la santé matérielle interrompue");
        }
    }

    internal async Task NotifyCriticalDisksAsync(HardwareHealthReport report)
    {
        var critical = report.Disks.Where(d => d.IsCritical).ToList();
        if (critical.Count == 0) return;

        var now = _clock.UtcNow;
        var alerts = await _store.GetAsync<Dictionary<string, DateTimeOffset>>(DiskAlertKey).ConfigureAwait(false) ?? [];
        var changed = false;
        foreach (var disk in critical)
        {
            if (alerts.TryGetValue(disk.DeviceId, out var last) && now - last < AlertCooldown) continue;
            _notifications.Show(new NotificationRequest(
                TextRef.Of(DiagText.HealthNotifyDiskTitle),
                TextRef.Of(DiagText.HealthNotifyDiskBody, disk.FriendlyName),
                ActionId: "navigate:health",
                ActionLabel: TextRef.Of(DiagText.HealthNotifyOpen),
                Tag: "health-disk"));
            alerts[disk.DeviceId] = now;
            changed = true;
        }
        if (changed) await _store.SetAsync(DiskAlertKey, alerts).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DiskReliability>> LoadReliabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _store.GetAsync<List<DiskReliability>>(ReliabilityKey, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Compteurs de fiabilité enregistrés illisibles");
            return [];
        }
    }

    private HardwareHealthReport Build(IReadOnlyList<DiskReliability> cached)
    {
        var now = _clock.UtcNow;

        var disks = Read("disks", _provider.GetDisks);
        var diskList = disks.Success && disks.Value is { } d
            ? d.Select(disk => disk with { Reliability = cached.FirstOrDefault(c => string.Equals(c.DeviceId, disk.DeviceId, StringComparison.Ordinal)) }).ToList()
            : [];

        var batteries = Read("batteries", _provider.GetBatteries);
        var devices = Read("devices", _provider.GetDeviceProblems);

        IReadOnlyList<DateTimeOffset> firmwareEvents;
        try
        {
            firmwareEvents = _provider.GetFirmwareLimitEvents(now - FirmwareEventWindow);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Événements de limitation du processeur illisibles");
            firmwareEvents = [];
        }

        var thermal = new ThermalLimitInfo(
            firmwareEvents.Count,
            firmwareEvents.Count == 0 ? null : firmwareEvents.Max(),
            _thermal?.EpisodeCount ?? 0,
            _thermal?.LastEpisode);

        return new HardwareHealthReport(
            now,
            diskList,
            AvailabilityOf(disks),
            cached.Count == 0 ? null : cached.Max(c => c.MeasuredAt),
            batteries.Success && batteries.Value is { } b ? b : [],
            AvailabilityOf(batteries),
            devices.Success && devices.Value is { } p ? p : [],
            AvailabilityOf(devices),
            thermal);
    }

    private OperationResult<IReadOnlyList<T>> Read<T>(string step, Func<OperationResult<IReadOnlyList<T>>> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Relevé de santé {Step} impossible", step);
            return OperationResult<IReadOnlyList<T>>.Fail(OperationErrorKind.Failed);
        }
    }

    private static Availability AvailabilityOf<T>(OperationResult<IReadOnlyList<T>> result) => result switch
    {
        { Success: true } => Availability.Available,
        { Error: OperationErrorKind.NotSupported } => Availability.NotSupported,
        { Error: OperationErrorKind.AccessDenied or OperationErrorKind.RequiresElevation } => Availability.RequiresElevation,
        _ => Availability.Unavailable,
    };
}
