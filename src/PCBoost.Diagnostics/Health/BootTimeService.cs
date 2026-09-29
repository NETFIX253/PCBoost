using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;

namespace PCBoost.Diagnostics.Health;

/// <summary>
/// Durée de démarrage : démarrages récents (journal Système, sans autorisation) et mesures de Windows (journal
/// Diagnostics-Performance, lu avec autorisation puis conservé). La comparaison avant / après repose sur la date du
/// dernier changement de programmes au démarrage enregistré dans l'historique de PCBoost : seuls des démarrages
/// réellement mesurés sont comparés, jamais une estimation.
/// </summary>
public sealed class BootTimeService : IBootTimeService
{
    internal const string MeasurementsKey = "health.boot-performance";
    internal const int SessionCount = 10;
    internal const int ComparisonWindow = 5;
    private const int HistorySessions = 200;

    private readonly IHardwareHealthProvider _provider;
    private readonly IElevationService _elevation;
    private readonly IKeyValueStore _store;
    private readonly IOptimizationHistoryRepository _history;
    private readonly IClock _clock;
    private readonly ILogger<BootTimeService> _logger;

    public BootTimeService(IHardwareHealthProvider provider, IElevationService elevation, IKeyValueStore store, IOptimizationHistoryRepository history,
        IClock clock, ILogger<BootTimeService>? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? NullLogger<BootTimeService>.Instance;
    }

    public async Task<BootTimeReport> GetReportAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BootSession> sessions;
        try
        {
            sessions = await Task.Run(() => _provider.GetRecentBoots(SessionCount), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Démarrages récents illisibles");
            sessions = [];
        }

        BootPerformanceData? measurements = null;
        try
        {
            measurements = await _store.GetAsync<BootPerformanceData>(MeasurementsKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Mesures de démarrage enregistrées illisibles");
        }

        var lastChange = await LastStartupChangeAsync(cancellationToken).ConfigureAwait(false);
        var comparison = measurements is null || lastChange is null ? null : Compare(measurements.Boots, lastChange.Value);
        return new BootTimeReport(sessions, measurements, lastChange, comparison);
    }

    public async Task<OperationResult> ReadMeasurementsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _elevation.RunAsync(new ElevatedRequest(ElevatedHealthOperations.BootPerformance, new Dictionary<string, string>()), cancellationToken)
            .ConfigureAwait(false);
        if (!response.Outcome.Success) return response.Outcome;

        var fresh = HealthElevatedData.DecodeBoot(response.Data, _clock.UtcNow);
        var previous = await _store.GetAsync<BootPerformanceData>(MeasurementsKey, cancellationToken).ConfigureAwait(false);
        var merged = Merge(previous, fresh);
        await _store.SetAsync(MeasurementsKey, merged, cancellationToken).ConfigureAwait(false);
        return merged.Boots.Count > 0 ? OperationResult.Ok() : OperationResult.Ok(TextRef.Of(DiagText.BootNoMeasurement));
    }

    /// <summary>
    /// Fusionne avec les mesures déjà conservées : le journal de Windows est circulaire, l'historique de PCBoost garde
    /// les mesures plus anciennes utiles à la comparaison avant / après.
    /// </summary>
    internal static BootPerformanceData Merge(BootPerformanceData? previous, BootPerformanceData fresh)
    {
        if (previous is null) return fresh;
        var boots = fresh.Boots.Concat(previous.Boots)
            .GroupBy(b => b.Timestamp).Select(g => g.First())
            .OrderByDescending(b => b.Timestamp).Take(HealthElevatedData.MaxBoots).ToList();
        var degradations = fresh.Degradations.Concat(previous.Degradations)
            .GroupBy(d => (d.Timestamp, d.Name)).Select(g => g.First())
            .OrderByDescending(d => d.Timestamp).Take(HealthElevatedData.MaxDegradations).ToList();
        return new BootPerformanceData(fresh.ReadAt, boots, degradations);
    }

    /// <summary>Moyennes des (au plus) 5 démarrages mesurés avant et après <paramref name="changedAt"/>. Null si l'un des côtés est vide.</summary>
    internal static BootComparison? Compare(IReadOnlyList<BootRecord> boots, DateTimeOffset changedAt)
    {
        var before = boots.Where(b => b.Timestamp < changedAt).OrderByDescending(b => b.Timestamp).Take(ComparisonWindow).ToList();
        var after = boots.Where(b => b.Timestamp >= changedAt).OrderBy(b => b.Timestamp).Take(ComparisonWindow).ToList();
        if (before.Count == 0 || after.Count == 0) return null;
        return new BootComparison(changedAt,
            TimeSpan.FromMilliseconds(before.Average(b => b.BootTime.TotalMilliseconds)), before.Count,
            TimeSpan.FromMilliseconds(after.Average(b => b.BootTime.TotalMilliseconds)), after.Count);
    }

    /// <summary>Dernière modification (appliquée ou annulée) des programmes au démarrage, d'après l'historique de restauration.</summary>
    private async Task<DateTimeOffset?> LastStartupChangeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sessions = await _history.GetRecentSessionsAsync(HistorySessions, cancellationToken).ConfigureAwait(false);
            DateTimeOffset? latest = null;
            foreach (var change in sessions.SelectMany(s => s.Changes).Where(c => StartupChangeSources.IsStartupChange(c.OptimizationId)))
            {
                DateTimeOffset? when = change.Status switch
                {
                    ChangeStatus.Applied => change.RecordedAt,
                    ChangeStatus.RolledBack => change.RolledBackAt ?? change.RecordedAt,
                    _ => null,
                };
                if (when is { } t && (latest is null || t > latest)) latest = t;
            }
            return latest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Historique des modifications illisible");
            return null;
        }
    }
}
