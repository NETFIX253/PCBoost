using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Cleanup;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Cleanup;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Orchestration;

/// <summary>
/// Surveillance légère (§29), toutes les 30 minutes : notifie si la mémoire reste saturée ou si beaucoup d'espace est récupérable.
/// La seule action automatique possible — uniquement si l'utilisateur l'a autorisée — est le nettoyage des fichiers
/// temporaires de l'utilisateur de plus de 7 jours. Rien pendant une session Gaming.
/// </summary>
public sealed class SmartOptimizationService : ISmartOptimizationService
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan MemoryWindow = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MinimumMemoryCoverage = TimeSpan.FromMinutes(4);
    internal const double MemoryPressurePercent = 90;
    internal static readonly TimeSpan MemoryNotificationCooldown = TimeSpan.FromHours(4);
    internal static readonly TimeSpan CleanableScanInterval = TimeSpan.FromHours(6);
    internal static readonly TimeSpan AutomaticCleanupMinimumAge = TimeSpan.FromDays(7);

    internal const string LastMemoryNotificationKey = "smart.lastMemoryNotification";
    internal const string LastCleanableNotificationKey = "smart.lastCleanableNotificationDay";
    internal const string LastCleanableScanKey = "smart.lastCleanableScan";
    internal const string LastAutomaticCleanupKey = "smart.lastAutomaticCleanupDay";

    private readonly IPerformanceMonitor _monitor;
    private readonly SafeCleanupEngine _cleanup;
    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly IKeyValueStore _store;
    private readonly IRollbackManager _rollback;
    private readonly IActivityJournal _journal;
    private readonly IClock _clock;
    private readonly Func<IGamingService?> _gaming;
    private readonly TimeSpan _interval;
    private readonly ILogger<SmartOptimizationService> _logger;
    private readonly SemaphoreSlim _evaluationLock = new(1, 1);
    private readonly Lock _timerLock = new();
    private Timer? _timer;
    private bool _disposed;

    public SmartOptimizationService(IPerformanceMonitor monitor, SafeCleanupEngine cleanup, INotificationService notifications, ISettingsService settings,
        IKeyValueStore store, IRollbackManager rollback, IActivityJournal journal, IClock clock, IServiceProvider services,
        ILogger<SmartOptimizationService>? logger = null)
        : this(monitor, cleanup, notifications, settings, store, rollback, journal, clock, () => services.GetService<IGamingService>(), DefaultInterval, logger)
    {
    }

    internal SmartOptimizationService(IPerformanceMonitor monitor, SafeCleanupEngine cleanup, INotificationService notifications, ISettingsService settings,
        IKeyValueStore store, IRollbackManager rollback, IActivityJournal journal, IClock clock, Func<IGamingService?> gaming, TimeSpan interval,
        ILogger<SmartOptimizationService>? logger)
    {
        _monitor = monitor;
        _cleanup = cleanup;
        _notifications = notifications;
        _settings = settings;
        _store = store;
        _rollback = rollback;
        _journal = journal;
        _clock = clock;
        _gaming = gaming;
        _interval = interval;
        _logger = logger ?? NullLogger<SmartOptimizationService>.Instance;
    }

    public void Start()
    {
        lock (_timerLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null) return;
            _timer = new Timer(_ => _ = TickAsync(), null, _interval, _interval);
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

    private async Task TickAsync()
    {
        try
        {
            await EvaluateNowAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Évaluation périodique impossible");
        }
    }

    public async Task EvaluateNowAsync(CancellationToken cancellationToken = default)
    {
        // Une seule évaluation à la fois ; une évaluation en cours rend la suivante inutile.
        if (!await _evaluationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            if (IsGamingActive()) return;
            await CheckMemoryPressureAsync(cancellationToken).ConfigureAwait(false);
            await CheckCleanableSpaceAsync(cancellationToken).ConfigureAwait(false);
            if (_settings.Current.AllowAutomaticSafeOptimizations && !IsGamingActive())
                await RunAutomaticCleanupAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _evaluationLock.Release();
        }
    }

    private bool IsGamingActive()
    {
        try
        {
            return _gaming()?.State is GamingState.Activating or GamingState.Active or GamingState.Restoring;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "État du mode Gaming indisponible");
            return false;
        }
    }

    private async Task CheckMemoryPressureAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var history = _monitor.GetHistory(MemoryWindow).Where(s => s.Timestamp >= now - MemoryWindow).OrderBy(s => s.Timestamp).ToList();
        if (history.Count < 2) return;
        if (history[^1].Timestamp - history[0].Timestamp < MinimumMemoryCoverage) return;
        if (history.Any(s => s.MemoryUsedPercent <= MemoryPressurePercent)) return;

        var last = await _store.GetAsync<DateTimeOffset?>(LastMemoryNotificationKey, cancellationToken).ConfigureAwait(false);
        if (last is not null && now - last.Value < MemoryNotificationCooldown) return;

        _notifications.Show(new NotificationRequest(
            TextRef.Of("Opt_Smart_SlowTitle"),
            TextRef.Of("Opt_Smart_SlowBody", Math.Round(history.Average(s => s.MemoryUsedPercent))),
            ActionId: "navigate:diagnosis",
            ActionLabel: TextRef.Of("Opt_Smart_SlowAction"),
            Tag: "pcboost.smart.memory"));
        await _store.SetAsync<DateTimeOffset?>(LastMemoryNotificationKey, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckCleanableSpaceAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var today = now.UtcDateTime.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (await _store.GetAsync<string>(LastCleanableNotificationKey, cancellationToken).ConfigureAwait(false) == today) return;

        var lastScan = await _store.GetAsync<DateTimeOffset?>(LastCleanableScanKey, cancellationToken).ConfigureAwait(false);
        if (lastScan is not null && now - lastScan.Value < CleanableScanInterval) return;
        await _store.SetAsync<DateTimeOffset?>(LastCleanableScanKey, now, cancellationToken).ConfigureAwait(false);

        var safe = CleanupCatalog.All.Where(c => c.Safety == SafetyCategory.Safe && !c.IsRecycleBin).ToList();
        var scans = await _cleanup.ScanAsync(safe, cancellationToken).ConfigureAwait(false);
        var bytes = scans.Where(s => s.Available).Sum(s => s.Bytes);
        var threshold = Math.Max(ByteSize.MiB, _settings.Current.Thresholds.CleanableWarningBytes);
        if (bytes <= threshold) return;

        _notifications.Show(new NotificationRequest(
            TextRef.Of("Opt_Smart_CleanableTitle"),
            TextRef.Of("Opt_Smart_CleanableBody", bytes / (double)ByteSize.GiB),
            ActionId: "navigate:cleanup",
            ActionLabel: TextRef.Of("Opt_Smart_CleanableAction"),
            Tag: "pcboost.smart.cleanable"));
        await _store.SetAsync(LastCleanableNotificationKey, today, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Définition dérivée du catalogue : fichiers temporaires de l'utilisateur d'au moins 7 jours.</summary>
    internal static CleanupCategoryDefinition AutomaticCategory()
    {
        var baseDefinition = CleanupCatalog.Find(CleanupCatalog.UserTemp)
            ?? throw new InvalidOperationException("Catégorie user-temp absente du catalogue.");
        return baseDefinition with
        {
            MinimumFileAge = baseDefinition.MinimumFileAge > AutomaticCleanupMinimumAge ? baseDefinition.MinimumFileAge : AutomaticCleanupMinimumAge,
        };
    }

    private async Task RunAutomaticCleanupAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var today = now.UtcDateTime.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (await _store.GetAsync<string>(LastAutomaticCleanupKey, cancellationToken).ConfigureAwait(false) == today) return;
        await _store.SetAsync(LastAutomaticCleanupKey, today, cancellationToken).ConfigureAwait(false);

        var category = AutomaticCategory();
        Core.Optimization.IChangeRecorder recorder;
        try
        {
            recorder = await _rollback.BeginSessionAsync(SessionType.Automatic, TextRef.Of("Opt_Session_Automatic"), null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogWarning(ex, "Journal de restauration indisponible : nettoyage automatique reporté");
            return;
        }
        IReadOnlyList<Core.Models.Cleanup.CleanupExecutionResult> results = [];
        try
        {
            results = await _cleanup.CleanAsync([category], null, cancellationToken).ConfigureAwait(false);
            await CleanupService.RecordAsync(recorder, OptimizationIds.AutomaticCleanup, results).ConfigureAwait(false);
        }
        finally
        {
            await _rollback.CompleteSessionAsync(recorder.SessionId, results.Sum(r => r.BytesFreed), false, CancellationToken.None).ConfigureAwait(false);
        }

        var bytes = results.Sum(r => r.BytesFreed);
        var files = results.Sum(r => r.FilesDeleted);
        await _journal.TryLogAsync(ActivityKind.Cleanup, TextRef.Of("Opt_Journal_AutomaticCleanup", bytes / (double)ByteSize.MiB, files),
            $"session={recorder.SessionId}", _logger).ConfigureAwait(false);
        if (bytes > 0)
        {
            _notifications.Show(new NotificationRequest(
                TextRef.Of("Opt_Smart_AutoCleanTitle"),
                TextRef.Of("Opt_Smart_AutoCleanBody", bytes / (double)ByteSize.MiB),
                ActionId: "navigate:history",
                ActionLabel: TextRef.Of("Opt_Smart_AutoCleanAction"),
                Tag: "pcboost.smart.autoclean"));
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
    }
}
