using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Cleanup;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Cleanup;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Cleanup;

/// <summary>
/// Service de nettoyage de la page « Nettoyage » : analyse, puis suppression confirmée par l'utilisateur.
/// Chaque catégorie nettoyée est consignée <c>Irreversible</c> dans une session <see cref="SessionType.Cleanup"/>.
/// </summary>
public sealed class CleanupService : ICleanupService
{
    private readonly SafeCleanupEngine _engine;
    private readonly IRollbackManager _rollback;
    private readonly IActivityJournal _journal;
    private readonly ILogger<CleanupService> _logger;

    public CleanupService(SafeCleanupEngine engine, IRollbackManager rollback, IActivityJournal journal, ILogger<CleanupService>? logger = null)
    {
        _engine = engine;
        _rollback = rollback;
        _journal = journal;
        _logger = logger ?? NullLogger<CleanupService>.Instance;
    }

    public IReadOnlyList<CleanupCategory> GetCategories() => _engine.Categories;

    public Task<IReadOnlyList<CleanupScanResult>> ScanAsync(IReadOnlyCollection<string>? categoryIds = null, CancellationToken cancellationToken = default)
        => _engine.ScanAsync(SafeCleanupEngine.Resolve(categoryIds), cancellationToken);

    public async Task<CleanupSummary> CleanAsync(IReadOnlyCollection<string> categoryIds, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categoryIds);
        var categories = SafeCleanupEngine.Resolve(categoryIds);
        if (categories.Count == 0) return new CleanupSummary([]);

        IChangeRecorder recorder;
        try
        {
            recorder = await _rollback.BeginSessionAsync(SessionType.Cleanup, TextRef.Of("Opt_Session_Cleanup"), null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // Sans journal, rien n'est supprimé (principe « consigner avant d'agir »).
            _logger.LogError(ex, "Journal de restauration indisponible : nettoyage annulé");
            var unavailable = OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Opt_Error_JournalUnavailable"), ex.GetType().Name);
            return new CleanupSummary(categories.Select(c => new CleanupExecutionResult(c.Id, 0, 0, 0, unavailable)).ToList());
        }

        IReadOnlyList<CleanupExecutionResult> results = [];
        try
        {
            results = await _engine.CleanAsync(categories, progress, cancellationToken).ConfigureAwait(false);
            await RecordAsync(recorder, OptimizationIds.CleanupManual, results).ConfigureAwait(false);
        }
        finally
        {
            var bytes = results.Sum(r => r.BytesFreed);
            await _rollback.CompleteSessionAsync(recorder.SessionId, bytes, false, CancellationToken.None).ConfigureAwait(false);
        }

        var summary = new CleanupSummary(results);
        await _journal.TryLogAsync(ActivityKind.Cleanup,
            TextRef.Of("Opt_Journal_CleanupDone", summary.TotalBytesFreed / (double)ByteSize.MiB, summary.TotalFilesDeleted),
            string.Join(", ", results.Select(r => $"{r.CategoryId}={r.BytesFreed}/{r.Outcome.Error}")), _logger).ConfigureAwait(false);
        return summary;
    }

    /// <summary>Consigne chaque catégorie traitée comme action irréversible.</summary>
    internal static async Task RecordAsync(IChangeRecorder recorder, string optimizationId, IEnumerable<CleanupExecutionResult> results)
    {
        foreach (var result in results)
        {
            var isRecycleBin = CleanupCatalog.Find(result.CategoryId)?.IsRecycleBin == true;
            var change = new PendingChange(
                isRecycleBin ? ChangeKinds.RecycleBin : ChangeKinds.FileDeletion,
                optimizationId,
                "cleanup:" + result.CategoryId,
                TextRef.Of($"Cleanup_{result.CategoryId}_Name"),
                ChangeStateSerializer.Serialize(new FileDeletionRecord(result.CategoryId, result.FilesDeleted, result.BytesFreed)),
                Reversible: false);
            await recorder.RecordIrreversibleAsync(change, result.Outcome, result.BytesFreed, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
