using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;

namespace PCBoost.Gaming.Optimizations;

/// <summary>
/// Mode de gestion de l'alimentation « Performances optimales » s'il existe, sinon « Performances élevées », le temps de la
/// session. Non applicable si aucun des deux n'existe ou si l'un d'eux est déjà actif. Sur batterie : proposé mais non
/// sélectionné par défaut (autonomie). Le mode précédent est rétabli à la restauration.
/// </summary>
public sealed class GamingPowerOptimization : GamingOptimizationBase
{
    private readonly IPowerProvider _power;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly ILogger<GamingPowerOptimization> _logger;

    public GamingPowerOptimization(IPowerProvider power, ISystemInfoProvider systemInfo, IServiceProvider? services = null, ILogger<GamingPowerOptimization>? logger = null)
        : base(services)
    {
        _power = power ?? throw new ArgumentNullException(nameof(power));
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _logger = logger ?? NullLogger<GamingPowerOptimization>.Instance;
    }

    public override string Id => GamingOptimizationIds.Power;

    public override TextRef Name => TextRef.Of("Game_Opt_Power_Name");

    public override TextRef Description => TextRef.Of("Game_Opt_Power_Description");

    public override OptimizationCategory Category => OptimizationCategory.Power;

    public override RiskLevel RiskLevel => RiskLevel.Low;

    public override ImpactLevel ImpactLevel => ImpactLevel.Medium;

    public override Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(Evaluate().Preview);
    }

    public override async Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(recorder);
        var evaluation = Evaluate();
        if (!evaluation.Preview.Applicable || evaluation.Active is null || evaluation.Target is null)
            return OptimizationResult.Skipped(Id, evaluation.Preview.NotApplicableReason ?? TextRef.Of("Game_Power_NotApplicable"));

        var change = evaluation.Preview.Changes[0];
        if (!context.IsSelected(change))
            return OptimizationResult.Skipped(Id, TextRef.Of(evaluation.OnBattery ? "Game_Power_OnBatteryNotSelected" : "Game_NotSelected"));

        var active = evaluation.Active;
        var target = evaluation.Target;
        var before = ChangeStateSerializer.Serialize(new PowerSchemeState(active.Id, active.Name));
        var pending = new PendingChange(ChangeKinds.PowerScheme, Id, change.Target, change.Description, before, Reversible: true);
        var outcome = await recorder.ApplyAsync(pending, _ => Task.FromResult(_power.SetActiveScheme(target.Id)), cancellationToken).ConfigureAwait(false);
        if (outcome.Success)
        {
            _logger.LogInformation("Mode Gaming : mode d'alimentation {Target} activé (précédent : {Previous})", target.Id, active.Id);
            return Result(outcome, 1, 0, TextRef.Of("Game_Power_Applied", target.Name));
        }
        _logger.LogWarning("Mode Gaming : changement du mode d'alimentation refusé ({Error})", outcome.Error);
        return Result(outcome, 0, 1, outcome.Message ?? TextRef.Of("Game_Power_Failed"));
    }

    private Evaluation Evaluate()
    {
        var active = _power.GetActiveScheme();
        if (active is null) return new Evaluation(NotApplicable(TextRef.Of("Game_Power_Unknown")), null, null, false);

        if (active.Id == PowerScheme.UltimatePerformance || active.Id == PowerScheme.HighPerformance)
            return new Evaluation(NotApplicable(TextRef.Of("Game_Power_AlreadyActive", active.Name)), active, null, false);

        var schemes = _power.GetSchemes();
        var target = schemes.FirstOrDefault(s => s.Id == PowerScheme.UltimatePerformance)
            ?? schemes.FirstOrDefault(s => s.Id == PowerScheme.HighPerformance);
        if (target is null) return new Evaluation(NotApplicable(TextRef.Of("Game_Power_NoPerformancePlan")), active, null, false);

        var onBattery = _systemInfo.GetPowerStatus().Source == PowerSource.Battery;
        var change = new PlannedChange(
            $"{Id}:scheme",
            TextRef.Of(onBattery ? "Game_Power_ChangeOnBattery" : "Game_Power_Change", active.Name, target.Name),
            $"power:{target.Id:D}",
            SelectedByDefault: !onBattery,
            Reversible: true,
            RiskLevel.Low);
        return new Evaluation(Applicable([change]), active, target, onBattery);
    }

    private sealed record Evaluation(OptimizationPreview Preview, PowerScheme? Active, PowerScheme? Target, bool OnBattery);
}
