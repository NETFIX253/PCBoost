using Microsoft.Extensions.Logging;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Services;
using PCBoost.Infrastructure.Settings;

namespace PCBoost.Infrastructure.Activity;

/// <summary>
/// Journal des actions visible par l'utilisateur (§78). Les entrées plus anciennes que la durée de conservation
/// de l'historique (<c>AppSettings.HistoryRetentionDays</c>) sont purgées au plus une fois par jour, lors d'une écriture.
/// Un échec du stockage n'interrompt jamais l'opération journalisée.
/// </summary>
public sealed class ActivityJournal : IActivityJournal
{
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(24);

    private readonly IActivityLogRepository _repository;
    private readonly IClock _clock;
    private readonly ISettingsService _settings;
    private readonly ILogger<ActivityJournal> _logger;
    private readonly Lock _purgeGate = new();
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;
    private bool _purging;

    public ActivityJournal(IActivityLogRepository repository, IClock clock, ISettingsService settings, ILogger<ActivityJournal> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler<ActivityLogEntry>? EntryAdded;

    public async Task LogAsync(ActivityKind kind, TextRef message, string? detail = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var entry = new ActivityLogEntry(Guid.NewGuid(), _clock.UtcNow, kind, message, detail);
        try
        {
            await _repository.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Impossible d'enregistrer une entrée du journal d'activité ({Kind}, {Key}).", kind, message.Key);
        }

        RaiseEntryAdded(entry);
        await PurgeIfDueAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ActivityLogEntry>> GetRecentAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _repository.GetRecentAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Impossible de lire le journal d'activité.");
            return [];
        }
    }

    /// <summary>Supprime les entrées plus anciennes que la durée de conservation. Renvoie le nombre d'entrées supprimées.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var retentionDays = Math.Clamp(_settings.Current.HistoryRetentionDays,
            AppSettingsNormalizer.MinHistoryRetentionDays, AppSettingsNormalizer.MaxHistoryRetentionDays);
        try
        {
            var removed = await _repository.PurgeOlderThanAsync(now - TimeSpan.FromDays(retentionDays), cancellationToken).ConfigureAwait(false);
            lock (_purgeGate) _lastPurge = now;
            if (removed > 0)
                _logger.LogInformation("Journal d'activité : {Count} entrée(s) de plus de {Days} jours supprimée(s).", removed, retentionDays);
            return removed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "La purge du journal d'activité a échoué.");
            return 0;
        }
    }

    private async Task PurgeIfDueAsync(CancellationToken cancellationToken)
    {
        lock (_purgeGate)
        {
            if (_purging || _clock.UtcNow - _lastPurge < PurgeInterval) return;
            _purging = true;
        }
        try
        {
            await PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_purgeGate) _purging = false;
        }
    }

    private void RaiseEntryAdded(ActivityLogEntry entry)
    {
        var handlers = EntryAdded;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<ActivityLogEntry>>())
        {
            try
            {
                handler(this, entry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Un abonné au journal d'activité a échoué.");
            }
        }
    }
}
