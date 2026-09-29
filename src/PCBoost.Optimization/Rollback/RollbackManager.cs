using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Rollback;

/// <summary>
/// Journal de restauration (§10) : sessions persistées, consignation write-ahead, restauration en ordre inverse via les
/// <see cref="IChangeHandler"/>, purge selon la durée de conservation.
/// </summary>
public sealed class RollbackManager : IRollbackManager, IRollbackInternals
{
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(6);
    private static readonly Func<ChangeRecord, bool> AllChanges = static _ => true;

    private readonly IOptimizationHistoryRepository _repository;
    private readonly IOptimizationSafetyValidator _validator;
    private readonly Dictionary<string, IChangeHandler> _handlers;
    private readonly IActivityJournal _journal;
    private readonly ISettingsService _settings;
    private readonly IClock _clock;
    private readonly ILogger<RollbackManager> _logger;
    private readonly ConcurrentDictionary<Guid, ChangeRecorder> _open = new();
    private readonly SemaphoreSlim _restoreLock = new(1, 1);
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    public RollbackManager(
        IOptimizationHistoryRepository repository,
        IOptimizationSafetyValidator validator,
        IEnumerable<IChangeHandler> handlers,
        IActivityJournal journal,
        ISettingsService settings,
        IClock clock,
        ILogger<RollbackManager>? logger = null)
    {
        _repository = repository;
        _validator = validator;
        _journal = journal;
        _settings = settings;
        _clock = clock;
        _logger = logger ?? NullLogger<RollbackManager>.Instance;
        _handlers = new Dictionary<string, IChangeHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
            _handlers.TryAdd(handler.Kind, handler);
    }

    public event EventHandler<Guid>? SessionChanged;

    public async Task<IChangeRecorder> BeginSessionAsync(SessionType type, TextRef title, string? profileId = null, CancellationToken cancellationToken = default)
    {
        var session = new OptimizationSession
        {
            Id = Guid.NewGuid(),
            Type = type,
            StartedAt = _clock.UtcNow,
            Status = SessionStatus.InProgress,
            Title = title,
            ProfileId = profileId,
        };
        await _repository.CreateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        var recorder = new ChangeRecorder(session.Id, _repository, _validator, _clock, _logger, NotifySessionChanged);
        _open[session.Id] = recorder;
        _logger.LogInformation("Session de restauration {SessionId} ouverte ({Type})", session.Id, type);
        NotifySessionChanged(session.Id);
        return recorder;
    }

    public async Task CompleteSessionAsync(Guid sessionId, long bytesFreed = 0, bool requiresRestart = false, CancellationToken cancellationToken = default)
    {
        var session = await _repository.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _open.TryRemove(sessionId, out _);
        if (session is null)
        {
            _logger.LogWarning("Session {SessionId} introuvable à la clôture", sessionId);
            return;
        }

        // Une session déjà restaurée (ex. mode Jeu restauré avant sa clôture) conserve son statut de restauration.
        var status = session.Status is SessionStatus.InProgress or SessionStatus.Interrupted
            ? ComputeCompletionStatus(session.Changes)
            : session.Status;
        var completed = session with
        {
            Status = status,
            CompletedAt = _clock.UtcNow,
            BytesFreed = Math.Max(0, bytesFreed),
            RequiresRestart = requiresRestart,
        };
        await _repository.UpdateSessionAsync(completed, cancellationToken).ConfigureAwait(false);

        var applied = session.Changes.Count(c => c.Status is ChangeStatus.Applied or ChangeStatus.Irreversible);
        var failed = session.Changes.Count(c => c.Status is ChangeStatus.Failed or ChangeStatus.Pending);
        if (session.Changes.Count > 0)
        {
            await _journal.TryLogAsync(
                failed == 0 ? ActivityKind.Optimization : ActivityKind.Warning,
                TextRef.Of("Opt_Journal_SessionCompleted", applied, failed),
                $"session={sessionId} type={session.Type} status={status}",
                _logger).ConfigureAwait(false);
        }
        NotifySessionChanged(sessionId);
    }

    /// <summary>Statut final : aucune erreur → Completed ; aucune réussite → Failed ; sinon PartiallyCompleted.</summary>
    internal static SessionStatus ComputeCompletionStatus(IReadOnlyList<ChangeRecord> changes)
    {
        var succeeded = changes.Count(c => c.Status is ChangeStatus.Applied or ChangeStatus.Irreversible);
        // Une modification restée Pending a un état incertain : elle est comptée comme un échec.
        var failed = changes.Count(c => c.Status is ChangeStatus.Failed or ChangeStatus.Pending);
        if (failed == 0) return SessionStatus.Completed;
        return succeeded == 0 ? SessionStatus.Failed : SessionStatus.PartiallyCompleted;
    }

    public async Task<OperationResult> UndoChangeAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        var (session, change) = await FindChangeAsync(changeId, cancellationToken).ConfigureAwait(false);
        if (session is null || change is null)
            return OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Opt_Rollback_ChangeNotFound"));
        if (!change.Reversible || change.Status == ChangeStatus.Irreversible)
            return OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Opt_Undo_Irreversible"));
        if (change.Status == ChangeStatus.RolledBack)
            return OperationResult.Ok(TextRef.Of("Opt_Undo_AlreadyRestored"));
        if (change.Status == ChangeStatus.Failed)
            return OperationResult.Ok(TextRef.Of("Opt_Undo_NothingToRestore"));

        await _restoreLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await UndoOneAsync(change, cancellationToken).ConfigureAwait(false);
            var refreshed = await _repository.GetSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
            if (refreshed is not null && !IsOpenInCurrentProcess(session.Id))
            {
                var remaining = refreshed.Changes.Count(IsRestorable);
                var restored = refreshed.Changes.Count(c => c.Status == ChangeStatus.RolledBack);
                var status = remaining == 0 ? SessionStatus.RolledBack : restored > 0 ? SessionStatus.PartiallyRolledBack : refreshed.Status;
                if (status != refreshed.Status)
                    await _repository.UpdateSessionAsync(refreshed with { Status = status }, cancellationToken).ConfigureAwait(false);
            }
            await _journal.TryLogAsync(outcome.Success ? ActivityKind.Rollback : ActivityKind.Warning,
                TextRef.Of(outcome.Success ? "Opt_Journal_ChangeUndone" : "Opt_Journal_ChangeUndoFailed"),
                $"kind={change.Kind} session={session.Id}", _logger).ConfigureAwait(false);
            NotifySessionChanged(session.Id);
            return outcome;
        }
        finally
        {
            _restoreLock.Release();
        }
    }

    public Task<RollbackResult> RestoreSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => RestoreChangesAsync(sessionId, AllChanges, cancellationToken);

    public async Task<RollbackResult> RestoreChangesAsync(Guid sessionId, Func<ChangeRecord, bool> filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await _restoreLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await _repository.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                return new RollbackResult(0, 0, 0,
                    [OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Opt_Rollback_SessionNotFound"))]);
            }

            var scope = session.Changes.Where(filter).ToList();
            var irreversible = scope.Count(c => c.Status == ChangeStatus.Irreversible
                                                || (!c.Reversible && c.Status is ChangeStatus.Applied or ChangeStatus.Pending));
            // Ordre inverse d'application ; une modification Pending a peut-être été appliquée : elle est annulée (idempotent).
            var candidates = scope.Where(IsRestorable).OrderByDescending(c => c.Sequence).ToList();

            int restored = 0, failed = 0;
            var errors = new List<OperationResult>();
            foreach (var change in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await UndoOneAsync(change, cancellationToken).ConfigureAwait(false);
                if (outcome.Success)
                {
                    restored++;
                }
                else
                {
                    failed++;
                    errors.Add(outcome);
                }
            }

            var fullSession = ReferenceEquals(filter, AllChanges) || scope.Count == session.Changes.Count;
            if (fullSession) _open.TryRemove(sessionId, out _);
            var refreshed = await _repository.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) ?? session;
            var newStatus = ComputeRestoreStatus(refreshed, restored, failed, candidates.Count, fullSession);
            if (newStatus != refreshed.Status && (fullSession || !IsOpenInCurrentProcess(sessionId)))
                await _repository.UpdateSessionAsync(refreshed with { Status = newStatus, CompletedAt = refreshed.CompletedAt ?? _clock.UtcNow }, cancellationToken).ConfigureAwait(false);

            var result = new RollbackResult(restored, failed, irreversible, errors);
            if (candidates.Count > 0 || irreversible > 0)
            {
                await _journal.TryLogAsync(failed == 0 ? ActivityKind.Rollback : ActivityKind.Warning,
                    TextRef.Of("Opt_Journal_SessionRestored", restored, failed, irreversible),
                    $"session={sessionId} status={newStatus}", _logger).ConfigureAwait(false);
            }
            _logger.LogInformation("Restauration de la session {SessionId} : {Restored} restaurée(s), {Failed} échec(s), {Irreversible} irréversible(s)",
                sessionId, restored, failed, irreversible);
            NotifySessionChanged(sessionId);
            return result;
        }
        finally
        {
            _restoreLock.Release();
        }
    }

    private static SessionStatus ComputeRestoreStatus(OptimizationSession session, int restored, int failed, int candidates, bool fullSession)
    {
        if (candidates == 0)
        {
            // Rien à annuler : une session interrompue ou restée ouverte est close comme restaurée.
            return session.Status is SessionStatus.InProgress or SessionStatus.Interrupted ? SessionStatus.RolledBack : session.Status;
        }
        var remaining = session.Changes.Count(IsRestorable);
        if (failed == 0 && remaining == 0) return SessionStatus.RolledBack;
        if (failed == 0 && !fullSession) return restored > 0 ? SessionStatus.PartiallyRolledBack : session.Status;
        return SessionStatus.PartiallyRolledBack;
    }

    /// <summary>Modification réversible dont l'effet est (peut-être) en place.</summary>
    private static bool IsRestorable(ChangeRecord change)
        => change.Reversible && change.Status is ChangeStatus.Applied or ChangeStatus.Pending or ChangeStatus.RollbackFailed;

    private async Task<OperationResult> UndoOneAsync(ChangeRecord change, CancellationToken cancellationToken)
    {
        OperationResult outcome;
        if (!_handlers.TryGetValue(change.Kind, out var handler) || !handler.CanUndo(change))
        {
            outcome = OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Opt_Undo_NoHandler"), change.Kind);
        }
        else
        {
            try
            {
                outcome = await handler.UndoAsync(change, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                outcome = OperationResult.FromException(ex);
            }
        }

        var updated = outcome.Success
            ? change with { Status = ChangeStatus.RolledBack, RolledBackAt = _clock.UtcNow, ErrorDetail = null }
            : change with { Status = ChangeStatus.RollbackFailed, ErrorDetail = ChangeRecorder.Detail(outcome) };
        try
        {
            await _repository.UpdateChangeAsync(updated, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Mise à jour du journal de restauration impossible après annulation ({Kind})", change.Kind);
        }
        if (!outcome.Success)
            _logger.LogWarning("Annulation impossible ({Kind}) : {Error}", change.Kind, outcome.Error);
        return outcome;
    }

    public bool CanRollback(OptimizationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Status == SessionStatus.InProgress && IsOpenInCurrentProcess(session.Id)) return false;
        return session.Changes.Any(c => IsRestorable(c) && _handlers.TryGetValue(c.Kind, out var h) && h.CanUndo(c));
    }

    public async Task<IReadOnlyList<OptimizationSession>> GetHistoryAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await PurgeIfDueAsync(cancellationToken).ConfigureAwait(false);
        return await _repository.GetRecentSessionsAsync(Math.Clamp(limit, 1, 10_000), cancellationToken).ConfigureAwait(false);
    }

    public Task<OptimizationSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _repository.GetSessionAsync(sessionId, cancellationToken);

    public bool IsOpenInCurrentProcess(Guid sessionId) => _open.ContainsKey(sessionId);

    public void NotifySessionChanged(Guid sessionId)
    {
        try
        {
            SessionChanged?.Invoke(this, sessionId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Abonné SessionChanged en erreur");
        }
    }

    private async Task PurgeIfDueAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (now - _lastPurge < PurgeInterval) return;
        _lastPurge = now;
        var days = Math.Max(1, _settings.Current.HistoryRetentionDays);
        try
        {
            var purged = await _repository.PurgeOlderThanAsync(now - TimeSpan.FromDays(days), cancellationToken).ConfigureAwait(false);
            if (purged > 0) _logger.LogInformation("{Count} session(s) de plus de {Days} jours purgée(s)", purged, days);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogWarning(ex, "Purge de l'historique de restauration impossible");
        }
    }

    private async Task<(OptimizationSession? Session, ChangeRecord? Change)> FindChangeAsync(Guid changeId, CancellationToken cancellationToken)
    {
        // Core n'expose pas de recherche par identifiant de modification : parcours des sessions récentes.
        var sessions = await _repository.GetRecentSessionsAsync(2000, cancellationToken).ConfigureAwait(false);
        foreach (var session in sessions)
        {
            var change = session.Changes.FirstOrDefault(c => c.Id == changeId);
            if (change is not null) return (session, change);
        }
        return (null, null);
    }
}
