using Microsoft.Extensions.Logging;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Rollback;

/// <summary>
/// Consignation write-ahead d'une session : 1) validation de sûreté (cible interdite → Blocked, rien n'est exécuté),
/// 2) persistance du <see cref="ChangeRecord"/> <c>Pending</c> avec l'état « avant » et un numéro de séquence,
/// 3) exécution, 4) marquage <c>Applied</c> ou <c>Failed</c>.
/// </summary>
public sealed class ChangeRecorder : IChangeRecorderWithAfterState
{
    private readonly IOptimizationHistoryRepository _repository;
    private readonly IOptimizationSafetyValidator _validator;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly Action<Guid>? _onChanged;
    private int _sequence;

    internal ChangeRecorder(Guid sessionId, IOptimizationHistoryRepository repository, IOptimizationSafetyValidator validator,
        IClock clock, ILogger logger, Action<Guid>? onChanged, int initialSequence = 0)
    {
        SessionId = sessionId;
        _repository = repository;
        _validator = validator;
        _clock = clock;
        _logger = logger;
        _onChanged = onChanged;
        _sequence = initialSequence;
    }

    public Guid SessionId { get; }

    /// <summary>Nombre de modifications refusées par la validation de sûreté pendant cette session.</summary>
    public int BlockedCount { get; private set; }

    public Task<OperationResult> ApplyAsync(PendingChange change, Func<CancellationToken, Task<OperationResult>> apply, CancellationToken cancellationToken = default)
        => ApplyAsync(change, afterState: null, apply, cancellationToken);

    public async Task<OperationResult> ApplyAsync(PendingChange change, string? afterState, Func<CancellationToken, Task<OperationResult>> apply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(apply);
        cancellationToken.ThrowIfCancellationRequested();

        // 1) Validation : une cible interdite n'est jamais exécutée.
        var validation = _validator.ValidateChange(change);
        if (!validation.Allowed)
        {
            BlockedCount++;
            var codes = string.Join(", ", validation.Violations.Select(v => v.Code));
            var message = validation.Violations.FirstOrDefault(v => v.Blocking)?.Message ?? TextRef.Of("Opt_Safety_ForbiddenTarget");
            _logger.LogWarning("Modification refusée ({Codes}) : {Kind} pour {OptimizationId}", codes, change.Kind, change.OptimizationId);
            await TryPersistAsync(NewRecord(change, afterState, ChangeStatus.Failed) with { ErrorDetail = $"Blocked: {codes}" }, isNew: true).ConfigureAwait(false);
            return OperationResult.Fail(OperationErrorKind.Blocked, message, codes);
        }

        // 2) Write-ahead : l'état « avant » est persisté AVANT toute modification.
        var record = NewRecord(change, afterState, ChangeStatus.Pending);
        try
        {
            await _repository.AddChangeAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Sans journal de restauration, la modification ne doit pas être appliquée.
            _logger.LogError(ex, "Journal de restauration indisponible : modification {Kind} non appliquée", change.Kind);
            return OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Opt_Error_JournalUnavailable"), ex.GetType().Name);
        }
        _onChanged?.Invoke(SessionId);

        // 3) Exécution.
        OperationResult outcome;
        try
        {
            outcome = await apply(cancellationToken).ConfigureAwait(false) ?? OperationResult.Fail(OperationErrorKind.Failed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // L'état réel est incertain : la modification reste Pending (restauration idempotente).
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = OperationResult.FromException(ex);
        }

        // 4) Résultat.
        var final = record with
        {
            Status = outcome.Success ? ChangeStatus.Applied : ChangeStatus.Failed,
            ErrorDetail = outcome.Success ? null : Detail(outcome),
        };
        await TryPersistAsync(final, isNew: false).ConfigureAwait(false);
        return outcome;
    }

    public async Task RecordIrreversibleAsync(PendingChange change, OperationResult outcome, long bytesFreed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(outcome);
        var record = NewRecord(change, ChangeStateSerializer.Serialize(new IrreversibleOutcomeState(Math.Max(0, bytesFreed))),
                outcome.Success ? ChangeStatus.Irreversible : ChangeStatus.Failed)
            with
            {
                Reversible = false,
                ErrorDetail = outcome.Success ? null : Detail(outcome),
            };
        await TryPersistAsync(record, isNew: true).ConfigureAwait(false);
    }

    private ChangeRecord NewRecord(PendingChange change, string? afterState, ChangeStatus status) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = SessionId,
        OptimizationId = change.OptimizationId,
        Kind = change.Kind,
        Target = change.Target,
        Description = change.Description,
        BeforeState = change.BeforeState,
        AfterState = afterState,
        Reversible = change.Reversible,
        Status = status,
        RecordedAt = _clock.UtcNow,
        Sequence = Interlocked.Increment(ref _sequence),
    };

    private async Task TryPersistAsync(ChangeRecord record, bool isNew)
    {
        try
        {
            if (isNew) await _repository.AddChangeAsync(record, CancellationToken.None).ConfigureAwait(false);
            else await _repository.UpdateChangeAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Mise à jour du journal de restauration impossible ({Kind}, {Status})", record.Kind, record.Status);
        }
        _onChanged?.Invoke(SessionId);
    }

    internal static string Detail(OperationResult outcome)
        => string.IsNullOrWhiteSpace(outcome.TechnicalDetail) ? outcome.Error.ToString() : $"{outcome.Error}: {outcome.TechnicalDetail}";
}

/// <summary>Résultat consigné pour une action irréversible (AfterState).</summary>
public sealed record IrreversibleOutcomeState(long BytesFreed);
