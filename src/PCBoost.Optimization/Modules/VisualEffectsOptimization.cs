using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Modules;

/// <summary>
/// visual-effects : réduit les animations, ombres, défilement fluide et transparence (SystemParametersInfo + EnableTransparency),
/// sur les petites configurations ou si <see cref="OptimizationIds.ForceItemKey"/> est vrai. État complet restauré à l'annulation.
/// </summary>
public sealed class VisualEffectsOptimization : OptimizationModuleBase
{
    private readonly IVisualEffectsProvider _visualEffects;

    public VisualEffectsOptimization(IVisualEffectsProvider visualEffects, IRollbackManager rollback) : base(rollback)
        => _visualEffects = visualEffects;

    public override string Id => OptimizationIds.VisualEffects;
    protected override string Key => "VisualEffects";
    public override OptimizationCategory Category => OptimizationCategory.Visual;
    public override RiskLevel RiskLevel => RiskLevel.Low;
    public override ImpactLevel ImpactLevel => ImpactLevel.Medium;
    public override bool IsReversible => true;

    internal const string ChangeIdReduce = OptimizationIds.VisualEffects + ":reduce";

    internal static bool IsSmallConfiguration(HardwareTier? tier)
        => tier is HardwareTier.LegacyLowResource or HardwareTier.Entry or HardwareTier.LowEnd;

    internal static bool IsForced(OptimizationContext context)
        => context.Items.TryGetValue(OptimizationIds.ForceItemKey, out var value)
           && value switch
           {
               bool b => b,
               string s => bool.TryParse(s, out var parsed) && parsed,
               _ => false,
           };

    /// <summary>État cible : animations, ombres, défilement fluide et transparence désactivés ; le reste inchangé.</summary>
    internal static VisualEffectsState Reduced(VisualEffectsState current) => current with
    {
        ClientAreaAnimation = false,
        MenuAnimation = false,
        ComboBoxAnimation = false,
        ListBoxSmoothScrolling = false,
        TooltipAnimation = false,
        WindowMinMaxAnimation = false,
        CursorShadow = false,
        Transparency = false,
    };

    private static PlannedChange Planned() => new(ChangeIdReduce, TextRef.Of("Opt_VisualEffects_Reduce"), "visual:effects",
        SelectedByDefault: true, Reversible: true, Risk: RiskLevel.Low);

    private (VisualEffectsState? Current, TextRef? Reason) Evaluate(OptimizationContext context)
    {
        if (!IsSmallConfiguration(context.Analysis?.HardwareProfile?.Tier) && !IsForced(context))
            return (null, TextRef.Of("Opt_VisualEffects_NotNeeded"));
        var current = _visualEffects.GetState();
        if (current is null) return (null, TextRef.Of("Opt_VisualEffects_Unavailable"));
        if (Reduced(current) == current) return (current, TextRef.Of("Opt_VisualEffects_AlreadyReduced"));
        return (current, null);
    }

    public override Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default)
    {
        var (_, reason) = Evaluate(context);
        return Task.FromResult(reason is not null ? NotApplicable(reason) : Applicable([Planned()]));
    }

    public override async Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default)
    {
        var (current, reason) = Evaluate(context);
        if (reason is not null || current is null) return OptimizationResult.Skipped(Id, reason ?? TextRef.Of("Opt_VisualEffects_Unavailable"));
        var planned = Planned();
        if (!context.IsSelected(planned)) return OptimizationResult.Skipped(Id, TextRef.Of("Opt_Result_NothingSelected"));

        var target = Reduced(current);
        var change = new PendingChange(ChangeKinds.VisualEffects, Id, planned.Target, planned.Description,
            ChangeStateSerializer.Serialize(current), Reversible: true);
        var outcome = await recorder.ApplyWithAfterStateAsync(change, ChangeStateSerializer.Serialize(target),
            _ => Task.FromResult(_visualEffects.SetState(target)), cancellationToken).ConfigureAwait(false);
        return Summarize([outcome]);
    }
}
