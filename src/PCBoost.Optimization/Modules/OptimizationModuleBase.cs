using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Modules;

/// <summary>Base commune : métadonnées, annulation des seuls changements du module via le gestionnaire de restauration.</summary>
public abstract class OptimizationModuleBase : IOptimization
{
    private readonly IRollbackManager _rollback;

    protected OptimizationModuleBase(IRollbackManager rollback) => _rollback = rollback;

    public abstract string Id { get; }

    public TextRef Name => TextRef.Of($"Opt_Module_{Key}_Name");

    public TextRef Description => TextRef.Of($"Opt_Module_{Key}_Description");

    public abstract OptimizationCategory Category { get; }

    public abstract RiskLevel RiskLevel { get; }

    public abstract ImpactLevel ImpactLevel { get; }

    public abstract bool IsReversible { get; }

    public virtual int MinimumWindowsBuild => OsInfo.MinimumSupportedBuild;

    /// <summary>Suffixe des clés de ressources (ex. « TempFiles »).</summary>
    protected abstract string Key { get; }

    public abstract Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default);

    public abstract Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default);

    public Task<RollbackResult> RollbackAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _rollback is IRollbackInternals internals
            ? internals.RestoreChangesAsync(sessionId, c => string.Equals(c.OptimizationId, Id, StringComparison.Ordinal), cancellationToken)
            : _rollback.RestoreSessionAsync(sessionId, cancellationToken);

    protected OptimizationPreview NotApplicable(TextRef reason)
        => OptimizationPreview.NotApplicable(Id, Name, reason, RiskLevel, ImpactLevel, IsReversible);

    protected OptimizationPreview Applicable(IReadOnlyList<PlannedChange> changes, ImpactLevel? impact = null, bool requiresElevation = false, bool requiresRestart = false)
        => new(Id, Name, true, null, changes, RiskLevel, impact ?? ImpactLevel, IsReversible, requiresElevation, requiresRestart);

    /// <summary>Résultat agrégé d'une série de modifications consignées.</summary>
    protected OptimizationResult Summarize(IReadOnlyList<OperationResult> outcomes, long bytesFreed = 0, bool requiresRestart = false, IEnumerable<TextRef>? extraMessages = null)
    {
        var applied = outcomes.Count(o => o.Success);
        var failed = outcomes.Count - applied;
        var messages = outcomes.Where(o => !o.Success && o.Message is not null).Select(o => o.Message!)
            .Concat(extraMessages ?? [])
            .DistinctBy(m => m.ToString())
            .ToList();
        if (outcomes.Count == 0)
            return OptimizationResult.Skipped(Id, TextRef.Of("Opt_Result_NothingSelected"));

        OperationResult overall;
        if (failed == 0) overall = OperationResult.Ok();
        else if (applied > 0) overall = OperationResult.Ok(TextRef.Of("Opt_Result_Partial", applied, failed));
        else
        {
            var first = outcomes.First(o => !o.Success);
            overall = OperationResult.Fail(first.Error, first.Message ?? TextRef.Of("Opt_Result_Failed"), first.TechnicalDetail);
        }
        return new OptimizationResult(Id, overall, applied, failed, bytesFreed, requiresRestart, messages);
    }
}
