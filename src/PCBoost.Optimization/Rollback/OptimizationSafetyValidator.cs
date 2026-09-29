using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Security;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Rollback;

/// <summary>
/// Validation de sûreté (§57). Refuse : version de Windows insuffisante, risque élevé non confirmé, action irréversible
/// non confirmée, aperçu non applicable, cible interdite par <see cref="ForbiddenTargetPolicy"/>, type de modification inconnu.
/// </summary>
public sealed class OptimizationSafetyValidator : IOptimizationSafetyValidator
{
    public const string CodeWindowsBuild = "WINDOWS_BUILD";
    public const string CodeNotApplicable = "NOT_APPLICABLE";
    public const string CodePreviewMismatch = "PREVIEW_MISMATCH";
    public const string CodeHighRisk = "HIGH_RISK_UNCONFIRMED";
    public const string CodeIrreversible = "IRREVERSIBLE_UNCONFIRMED";
    public const string CodeForbiddenTarget = "FORBIDDEN_TARGET";
    public const string CodeUnknownKind = "UNKNOWN_KIND";
    public const string CodeMissingBeforeState = "MISSING_BEFORE_STATE";
    public const string CodeMicrosoftTask = "MICROSOFT_TASK";

    private static readonly string[] BuiltInKinds =
    [
        ChangeKinds.RegistryValue, ChangeKinds.PowerScheme, ChangeKinds.ProcessPriority, ChangeKinds.ProcessEfficiency,
        ChangeKinds.VisualEffects, ChangeKinds.ScheduledTask, ChangeKinds.FileDeletion, ChangeKinds.RecycleBin,
    ];

    private readonly ISystemInfoProvider _systemInfo;
    private readonly HashSet<string> _knownKinds;
    private int? _build;

    public OptimizationSafetyValidator(ISystemInfoProvider systemInfo, IEnumerable<IChangeHandler>? handlers = null)
    {
        _systemInfo = systemInfo;
        _knownKinds = new HashSet<string>(BuiltInKinds, StringComparer.Ordinal);
        foreach (var handler in handlers ?? []) _knownKinds.Add(handler.Kind);
    }

    public SafetyValidationResult Validate(IOptimization optimization, OptimizationPreview preview, OptimizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(optimization);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(plan);
        var violations = new List<SafetyViolation>();

        var build = _build ??= ReadBuild();
        if (build is null || build.Value < optimization.MinimumWindowsBuild)
        {
            violations.Add(new SafetyViolation(CodeWindowsBuild,
                TextRef.Of("Opt_Safety_WindowsTooOld", optimization.MinimumWindowsBuild, build ?? 0), Blocking: true));
        }

        if (!string.Equals(preview.OptimizationId, optimization.Id, StringComparison.Ordinal))
            violations.Add(new SafetyViolation(CodePreviewMismatch, TextRef.Of("Opt_Safety_PreviewMismatch"), Blocking: true));

        if (!preview.Applicable)
            violations.Add(new SafetyViolation(CodeNotApplicable, preview.NotApplicableReason ?? TextRef.Of("Opt_Safety_NotApplicable"), Blocking: true));

        var selected = preview.Changes.Where(c => plan.SelectedChangeIds.Contains(c.Id)).ToList();
        var highRisk = optimization.RiskLevel == RiskLevel.High || preview.Risk == RiskLevel.High || selected.Any(c => c.Risk == RiskLevel.High);
        if (selected.Count > 0 && highRisk && !plan.HighRiskActionsConfirmed)
            violations.Add(new SafetyViolation(CodeHighRisk, TextRef.Of("Opt_Safety_HighRiskNotConfirmed"), Blocking: true));

        if (selected.Any(c => !c.Reversible) && !plan.IrreversibleActionsConfirmed)
            violations.Add(new SafetyViolation(CodeIrreversible, TextRef.Of("Opt_Safety_IrreversibleNotConfirmed"), Blocking: true));

        if (selected.Any(c => ForbiddenTargetPolicy.IsForbiddenTarget(c.Target)))
            violations.Add(new SafetyViolation(CodeForbiddenTarget, TextRef.Of("Opt_Safety_ForbiddenTarget"), Blocking: true));

        return Result(violations);
    }

    public SafetyValidationResult ValidateChange(PendingChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var violations = new List<SafetyViolation>();

        if (string.IsNullOrWhiteSpace(change.Kind) || !_knownKinds.Contains(change.Kind))
            violations.Add(new SafetyViolation(CodeUnknownKind, TextRef.Of("Opt_Safety_UnknownKind"), Blocking: true));

        if (ForbiddenTargetPolicy.IsForbiddenTarget(change.Target))
            violations.Add(new SafetyViolation(CodeForbiddenTarget, TextRef.Of("Opt_Safety_ForbiddenTarget"), Blocking: true));

        if (change.Reversible && string.IsNullOrWhiteSpace(change.BeforeState))
            violations.Add(new SafetyViolation(CodeMissingBeforeState, TextRef.Of("Opt_Safety_MissingBeforeState"), Blocking: true));

        // Défense en profondeur : la cible réelle est celle de l'état sérialisé, pas seulement le libellé.
        switch (change.Kind)
        {
            case ChangeKinds.RegistryValue:
                var registry = ChangeStateSerializer.Deserialize<RegistryValueState>(change.BeforeState);
                if (registry is null && change.Reversible)
                    violations.Add(new SafetyViolation(CodeMissingBeforeState, TextRef.Of("Opt_Safety_MissingBeforeState"), Blocking: true));
                else if (registry is not null && ForbiddenTargetPolicy.IsForbiddenRegistryPath(registry.KeyPath))
                    violations.Add(new SafetyViolation(CodeForbiddenTarget, TextRef.Of("Opt_Safety_ForbiddenTarget"), Blocking: true));
                break;
            case ChangeKinds.ScheduledTask:
                var task = ChangeStateSerializer.Deserialize<ScheduledTaskState>(change.BeforeState);
                if (task is not null && ScheduledTaskWriter.IsMicrosoftTaskPath(task.TaskPath))
                    violations.Add(new SafetyViolation(CodeMicrosoftTask, TextRef.Of("Opt_Safety_MicrosoftTask"), Blocking: true));
                break;
        }

        return Result(violations);
    }

    private static SafetyValidationResult Result(List<SafetyViolation> violations)
    {
        if (violations.Count == 0) return SafetyValidationResult.Ok;
        var distinct = violations.GroupBy(v => v.Code, StringComparer.Ordinal).Select(g => g.First()).ToList();
        return new SafetyValidationResult(!distinct.Any(v => v.Blocking), distinct);
    }

    private int? ReadBuild()
    {
        try
        {
            return _systemInfo.GetOsInfo().BuildNumber;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
