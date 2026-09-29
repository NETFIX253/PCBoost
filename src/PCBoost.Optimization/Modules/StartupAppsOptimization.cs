using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Startup;

namespace PCBoost.Optimization.Modules;

/// <summary>
/// startup-apps : propose de désactiver (via StartupApproved, réversible) les entrées activées recommandées « Peut être désactivé ».
/// Présélection si l'impact mesuré est élevé ou moyen. Jamais les entrées « À conserver » ni les logiciels de sécurité.
/// </summary>
public sealed class StartupAppsOptimization : OptimizationModuleBase
{
    private readonly IStartupService _startup;
    private readonly StartupToggler _toggler;

    internal StartupAppsOptimization(IStartupService startup, StartupToggler toggler, IRollbackManager rollback) : base(rollback)
    {
        _startup = startup;
        _toggler = toggler;
    }

    public override string Id => OptimizationIds.StartupApps;
    protected override string Key => "StartupApps";
    public override OptimizationCategory Category => OptimizationCategory.Startup;
    public override RiskLevel RiskLevel => RiskLevel.Low;
    public override ImpactLevel ImpactLevel => ImpactLevel.High;
    public override bool IsReversible => true;

    internal static string ChangeId(StartupEntry entry) => $"{OptimizationIds.StartupApps}:{entry.Id}";

    internal static bool IsCandidate(StartupEntry entry, StartupImpact minimumImpact)
        => entry.IsEnabled
           && entry.Recommendation == StartupRecommendation.CanDisable
           && !entry.IsSecuritySoftware
           && (minimumImpact <= StartupImpact.NotMeasured || entry.Impact >= minimumImpact);

    private static StartupImpact MinimumImpact(OptimizationContext context)
        => context.Items.TryGetValue(OptimizationIds.StartupMinimumImpactItemKey, out var value) && value is StartupImpact impact
            ? impact
            : StartupImpact.NotMeasured;

    public override async Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default)
    {
        var entries = await _startup.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
        var minimum = MinimumImpact(context);
        var candidates = entries.Where(e => IsCandidate(e, minimum)).ToList();
        if (candidates.Count == 0) return NotApplicable(TextRef.Of("Opt_StartupApps_NothingToDisable"));

        var changes = candidates
            .OrderByDescending(e => e.Impact)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new PlannedChange(
                ChangeId(e),
                TextRef.Of("Opt_StartupApps_Disable", e.Name),
                StartupToggler.TargetOf(e),
                SelectedByDefault: e.Impact is StartupImpact.High or StartupImpact.Medium,
                Reversible: true,
                Risk: RiskLevel.Low))
            .ToList();
        return Applicable(changes, requiresElevation: candidates.Any(e => e.RequiresElevation));
    }

    public override async Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default)
    {
        var entries = await _startup.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
        var outcomes = new List<OperationResult>();
        foreach (var entry in entries.Where(e => IsCandidate(e, StartupImpact.NotMeasured)))
        {
            var planned = new PlannedChange(ChangeId(entry), TextRef.Of("Opt_StartupApps_Disable", entry.Name), StartupToggler.TargetOf(entry),
                entry.Impact is StartupImpact.High or StartupImpact.Medium, true, RiskLevel.Low);
            if (!context.IsSelected(planned)) continue;
            if (context.SelectedChangeIds is null && !IsCandidate(entry, MinimumImpact(context))) continue;

            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await _toggler.SetEnabledAsync(entry, enabled: false, recorder, Id, cancellationToken).ConfigureAwait(false);
            if (!StartupToggler.IsNoChange(outcome)) outcomes.Add(outcome);
        }
        return Summarize(outcomes);
    }
}
