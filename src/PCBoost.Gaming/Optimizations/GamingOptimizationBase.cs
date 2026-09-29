using Microsoft.Extensions.DependencyInjection;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;

namespace PCBoost.Gaming.Optimizations;

/// <summary>
/// Base des modules du mode Gaming. L'annulation d'une session est déléguée au <see cref="IRollbackManager"/> (résolu à la
/// demande pour éviter toute dépendance circulaire), qui s'appuie sur les gestionnaires d'annulation par type de modification.
/// </summary>
public abstract class GamingOptimizationBase : IOptimization
{
    private readonly IServiceProvider? _services;

    protected GamingOptimizationBase(IServiceProvider? services)
    {
        _services = services;
    }

    public abstract string Id { get; }

    public abstract TextRef Name { get; }

    public abstract TextRef Description { get; }

    public abstract OptimizationCategory Category { get; }

    public abstract RiskLevel RiskLevel { get; }

    public abstract ImpactLevel ImpactLevel { get; }

    public bool IsReversible => true;

    public virtual int MinimumWindowsBuild => Core.Models.SystemInfo.OsInfo.MinimumSupportedBuild;

    public abstract Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default);

    public abstract Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default);

    /// <summary>Annule, dans l'ordre inverse, les modifications de ce module consignées dans la session.</summary>
    public async Task<RollbackResult> RollbackAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var manager = _services?.GetService<IRollbackManager>();
        if (manager is null)
            return new RollbackResult(0, 0, 0, [OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Game_RollbackUnavailable"))]);

        var session = await manager.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return RollbackResult.Empty;

        var mine = session.Changes.Where(c => string.Equals(c.OptimizationId, Id, StringComparison.Ordinal)).ToList();
        var toUndo = mine
            .Where(c => c.Reversible && c.Status is ChangeStatus.Applied or ChangeStatus.Pending)
            .OrderByDescending(c => c.Sequence)
            .ToList();
        int restored = 0, failed = 0;
        var errors = new List<OperationResult>();
        foreach (var change in toUndo)
        {
            var result = await manager.UndoChangeAsync(change.Id, cancellationToken).ConfigureAwait(false);
            if (result.Success) restored++;
            else
            {
                failed++;
                errors.Add(result);
            }
        }
        return new RollbackResult(restored, failed, mine.Count(c => c.Status == ChangeStatus.Irreversible), errors);
    }

    protected OptimizationPreview NotApplicable(TextRef reason)
        => OptimizationPreview.NotApplicable(Id, Name, reason, RiskLevel, ImpactLevel, IsReversible);

    protected OptimizationPreview Applicable(IReadOnlyList<PlannedChange> changes)
        => new(Id, Name, true, null, changes, RiskLevel, ImpactLevel, IsReversible, RequiresElevation: false, RequiresRestart: false);

    protected static int? GetGameProcessId(OptimizationContext context)
        => context.Items.TryGetValue(GamingContextKeys.GameProcessId, out var v) && v is int pid && pid > 0 ? pid : null;

    protected static string? GetGameExecutablePath(OptimizationContext context)
        => context.Items.TryGetValue(GamingContextKeys.GameExecutablePath, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s : null;

    protected static string? GetGameInstallDirectory(OptimizationContext context)
        => context.Items.TryGetValue(GamingContextKeys.GameInstallDirectory, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s : null;

    protected static string DisplayName(string processName)
        => processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;

    protected OptimizationResult Result(OperationResult outcome, int applied, int failed, params TextRef[] messages)
        => new(Id, outcome, applied, failed, 0, false, messages);
}
