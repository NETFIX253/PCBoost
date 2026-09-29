using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Rollback;

/// <summary>
/// Récupération après plantage (§69, §70) : au démarrage, les sessions restées <c>InProgress</c> (hors celles ouvertes par le
/// processus courant) sont marquées <c>Interrupted</c> et proposées à la restauration.
/// </summary>
public sealed class RecoveryManager : IRecoveryManager
{
    private readonly IOptimizationHistoryRepository _repository;
    private readonly IRollbackManager _rollback;
    private readonly IActivityJournal _journal;
    private readonly ILogger<RecoveryManager> _logger;

    public RecoveryManager(IOptimizationHistoryRepository repository, IRollbackManager rollback, IActivityJournal journal, ILogger<RecoveryManager>? logger = null)
    {
        _repository = repository;
        _rollback = rollback;
        _journal = journal;
        _logger = logger ?? NullLogger<RecoveryManager>.Instance;
    }

    public async Task<IReadOnlyList<OptimizationSession>> FindInterruptedSessionsAsync(CancellationToken cancellationToken = default)
    {
        var internals = _rollback as IRollbackInternals;
        var inProgress = await _repository.GetSessionsByStatusAsync([SessionStatus.InProgress], cancellationToken).ConfigureAwait(false);
        foreach (var session in inProgress)
        {
            if (internals?.IsOpenInCurrentProcess(session.Id) == true) continue;
            await _repository.UpdateSessionAsync(session with { Status = SessionStatus.Interrupted }, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Session {SessionId} ({Type}) interrompue détectée", session.Id, session.Type);
            await _journal.TryLogAsync(ActivityKind.Warning, TextRef.Of("Opt_Journal_SessionInterrupted"), $"session={session.Id} type={session.Type}", _logger).ConfigureAwait(false);
            internals?.NotifySessionChanged(session.Id);
        }

        var interrupted = await _repository.GetSessionsByStatusAsync([SessionStatus.Interrupted], cancellationToken).ConfigureAwait(false);
        // Seules les sessions dont des modifications réversibles sont (peut-être) en place demandent une décision.
        return interrupted
            .Where(s => internals?.IsOpenInCurrentProcess(s.Id) != true && _rollback.CanRollback(s))
            .OrderByDescending(s => s.StartedAt)
            .ToList();
    }

    public async Task<RollbackResult> RecoverAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var result = await _rollback.RestoreSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await _journal.TryLogAsync(result.Success ? ActivityKind.Rollback : ActivityKind.Warning,
            TextRef.Of("Opt_Journal_SessionRecovered", result.Restored, result.Failed), $"session={sessionId}", _logger).ConfigureAwait(false);
        return result;
    }

    public async Task DismissAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _repository.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.Status is not (SessionStatus.Interrupted or SessionStatus.InProgress)) return;
        if (session.Status == SessionStatus.InProgress && (_rollback as IRollbackInternals)?.IsOpenInCurrentProcess(sessionId) == true) return;

        await _repository.UpdateSessionAsync(session with { Status = SessionStatus.Dismissed }, cancellationToken).ConfigureAwait(false);
        await _journal.TryLogAsync(ActivityKind.Info, TextRef.Of("Opt_Journal_SessionDismissed"), $"session={sessionId}", _logger).ConfigureAwait(false);
        (_rollback as IRollbackInternals)?.NotifySessionChanged(sessionId);
    }
}
