using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Modules;

/// <summary>
/// power-plan : choisit le plan d'alimentation Windows adapté au profil (API PowerSetActiveScheme, réversible).
/// Sans profil : uniquement « Économie d'énergie » sur secteur → « Utilisation normale ».
/// </summary>
public sealed class PowerPlanOptimization : OptimizationModuleBase
{
    private readonly IPowerProvider _power;
    private readonly ISystemInfoProvider _systemInfo;

    public PowerPlanOptimization(IPowerProvider power, ISystemInfoProvider systemInfo, IRollbackManager rollback) : base(rollback)
    {
        _power = power;
        _systemInfo = systemInfo;
    }

    public override string Id => OptimizationIds.PowerPlan;
    protected override string Key => "PowerPlan";
    public override OptimizationCategory Category => OptimizationCategory.Power;
    public override RiskLevel RiskLevel => RiskLevel.Low;
    public override ImpactLevel ImpactLevel => ImpactLevel.Medium;
    public override bool IsReversible => true;

    private sealed record Decision(PowerScheme? Active, PowerScheme? Target, TextRef? NotApplicableReason);

    private Decision Decide(string? profileId)
    {
        var schemes = _power.GetSchemes();
        var active = _power.GetActiveScheme();
        PowerScheme? Find(Guid id) => schemes.FirstOrDefault(s => s.Id == id);

        PowerScheme? target;
        switch (profileId)
        {
            case PerformanceProfile.BalancedId:
            case PerformanceProfile.ProductivityId:
                target = Find(PowerScheme.Balanced);
                if (target is null) return new Decision(active, null, TextRef.Of("Opt_PowerPlan_SchemeMissing"));
                break;
            case PerformanceProfile.PowerSaverId:
                target = Find(PowerScheme.PowerSaver);
                if (target is null) return new Decision(active, null, TextRef.Of("Opt_PowerPlan_SchemeMissing"));
                break;
            case PerformanceProfile.GamingId:
                // Fréquemment absents sur les PC « Modern Standby » : jamais créés par PCBoost.
                target = Find(PowerScheme.UltimatePerformance) ?? Find(PowerScheme.HighPerformance);
                if (target is null) return new Decision(active, null, TextRef.Of("Opt_PowerPlan_NoPerformanceScheme"));
                break;
            default:
                if (active?.Id != PowerScheme.PowerSaver)
                    return new Decision(active, null, TextRef.Of("Opt_PowerPlan_AlreadySuitable"));
                PowerStatus power;
                try
                {
                    power = _systemInfo.GetPowerStatus();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return new Decision(active, null, TextRef.Of("Opt_PowerPlan_PowerSourceUnknown"));
                }
                if (power.Source != PowerSource.AC)
                    return new Decision(active, null, TextRef.Of("Opt_PowerPlan_OnBattery"));
                target = Find(PowerScheme.Balanced);
                if (target is null) return new Decision(active, null, TextRef.Of("Opt_PowerPlan_SchemeMissing"));
                break;
        }

        if (active is not null && active.Id == target.Id)
            return new Decision(active, target, TextRef.Of("Opt_PowerPlan_AlreadyActive", target.Name));
        return new Decision(active, target, null);
    }

    internal static string ChangeId(Guid schemeId) => $"{OptimizationIds.PowerPlan}:{schemeId:D}";

    private static PlannedChange Planned(PowerScheme? active, PowerScheme target) => new(
        ChangeId(target.Id),
        active is null ? TextRef.Of("Opt_PowerPlan_SwitchTo", target.Name) : TextRef.Of("Opt_PowerPlan_Switch", active.Name, target.Name),
        "power:" + target.Id.ToString("D"),
        SelectedByDefault: true,
        Reversible: true,
        Risk: RiskLevel.Low);

    public override Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default)
    {
        var decision = Decide(context.ProfileId);
        return Task.FromResult(decision.NotApplicableReason is not null || decision.Target is null
            ? NotApplicable(decision.NotApplicableReason ?? TextRef.Of("Opt_PowerPlan_AlreadySuitable"))
            : Applicable([Planned(decision.Active, decision.Target)]));
    }

    public override async Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default)
    {
        var decision = Decide(context.ProfileId);
        if (decision.NotApplicableReason is not null || decision.Target is null)
            return OptimizationResult.Skipped(Id, decision.NotApplicableReason ?? TextRef.Of("Opt_PowerPlan_AlreadySuitable"));

        var planned = Planned(decision.Active, decision.Target);
        if (!context.IsSelected(planned)) return OptimizationResult.Skipped(Id, TextRef.Of("Opt_Result_NothingSelected"));
        if (decision.Active is null)
            return Summarize([OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Opt_PowerPlan_ActiveUnknown"))]);

        var target = decision.Target;
        var change = new PendingChange(ChangeKinds.PowerScheme, Id, planned.Target, planned.Description,
            ChangeStateSerializer.Serialize(new PowerSchemeState(decision.Active.Id, decision.Active.Name)), Reversible: true);
        var outcome = await recorder.ApplyWithAfterStateAsync(change, ChangeStateSerializer.Serialize(new PowerSchemeState(target.Id, target.Name)),
            _ => Task.FromResult(_power.SetActiveScheme(target.Id)), cancellationToken).ConfigureAwait(false);
        return Summarize([outcome]);
    }
}
