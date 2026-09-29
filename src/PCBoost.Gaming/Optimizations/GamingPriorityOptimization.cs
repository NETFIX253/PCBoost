using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Gaming.Detection;

namespace PCBoost.Gaming.Optimizations;

/// <summary>
/// Priorité « Supérieure à la normale » pour le processus du jeu, jamais « Haute » ni « Temps réel ».
/// Si le jeu est protégé (anti-triche) et refuse la modification, le module est ignoré proprement : rien n'est consigné.
/// </summary>
public sealed class GamingPriorityOptimization : GamingOptimizationBase
{
    /// <summary>Priorité appliquée au jeu. Jamais plus haute : « Haute » et « Temps réel » peuvent figer le système.</summary>
    public const ProcessPriority TargetPriority = ProcessPriority.AboveNormal;

    private readonly IProcessProvider _processes;
    private readonly IProcessControl _control;
    private readonly ICriticalProcessProtection _protection;
    private readonly GameSignatureDatabase _signatures;
    private readonly ILogger<GamingPriorityOptimization> _logger;

    public GamingPriorityOptimization(
        IProcessProvider processes,
        IProcessControl control,
        ICriticalProcessProtection protection,
        GameSignatureDatabase signatures,
        IServiceProvider? services = null,
        ILogger<GamingPriorityOptimization>? logger = null)
        : base(services)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        _signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
        _logger = logger ?? NullLogger<GamingPriorityOptimization>.Instance;
    }

    public override string Id => GamingOptimizationIds.Priority;

    public override TextRef Name => TextRef.Of("Game_Opt_Priority_Name");

    public override TextRef Description => TextRef.Of("Game_Opt_Priority_Description");

    public override OptimizationCategory Category => OptimizationCategory.Cpu;

    public override RiskLevel RiskLevel => RiskLevel.Low;

    public override ImpactLevel ImpactLevel => ImpactLevel.Low;

    public override Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(Evaluate(context).Preview);
    }

    public override async Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(recorder);
        var evaluation = Evaluate(context);
        if (!evaluation.Preview.Applicable || evaluation.Process is null)
            return OptimizationResult.Skipped(Id, evaluation.Preview.NotApplicableReason ?? TextRef.Of("Game_Priority_NoGame"));

        var change = evaluation.Preview.Changes[0];
        if (!context.IsSelected(change)) return OptimizationResult.Skipped(Id, TextRef.Of("Game_NotSelected"));

        var process = evaluation.Process;
        var display = DisplayName(process.Name);

        // Sonde sans effet (même priorité) : un jeu protégé par son anti-triche refuse l'accès. On l'ignore sans rien consigner.
        var probe = _control.SetPriority(process.ProcessId, evaluation.Current);
        if (!probe.Success)
            return Ignored(probe, display);

        var before = ChangeStateSerializer.Serialize(new ProcessPriorityState(process.ProcessId, process.Name, process.StartTime, evaluation.Current));
        var pending = new PendingChange(ChangeKinds.ProcessPriority, Id, change.Target, change.Description, before, Reversible: true);
        var outcome = await recorder.ApplyAsync(pending, _ => Task.FromResult(_control.SetPriority(process.ProcessId, TargetPriority)), cancellationToken).ConfigureAwait(false);
        if (outcome.Success)
        {
            _logger.LogInformation("Mode Gaming : priorité du jeu (PID {ProcessId}) réglée sur {Priority}", process.ProcessId, TargetPriority);
            return Result(outcome, 1, 0, TextRef.Of("Game_Priority_Applied", display));
        }
        if (outcome.Error is OperationErrorKind.AccessDenied or OperationErrorKind.NotFound or OperationErrorKind.Blocked)
            return Ignored(outcome, display);
        return Result(outcome, 0, 1, outcome.Message ?? TextRef.Of("Game_Priority_Failed", display));
    }

    private OptimizationResult Ignored(OperationResult outcome, string display)
    {
        var reason = outcome.Error == OperationErrorKind.NotFound
            ? TextRef.Of("Game_Priority_GameNotRunning")
            : TextRef.Of("Game_Priority_Protected", display);
        _logger.LogInformation("Mode Gaming : priorité du jeu non modifiée ({Error}) — ignoré", outcome.Error);
        return OptimizationResult.Skipped(Id, reason);
    }

    private Evaluation Evaluate(OptimizationContext context)
    {
        var pid = GetGameProcessId(context);
        if (pid is null) return new Evaluation(NotApplicable(TextRef.Of("Game_Priority_NoGame")), null, ProcessPriority.Unknown);

        var process = _processes.GetProcess(pid.Value);
        if (process is null) return new Evaluation(NotApplicable(TextRef.Of("Game_Priority_GameNotRunning")), null, ProcessPriority.Unknown);
        var display = DisplayName(process.Name);

        if (_signatures.IsAntiCheat(process.Name) || _protection.GetProtection(process.Name, process.ExecutablePath).Level != ProtectionLevel.None)
            return new Evaluation(NotApplicable(TextRef.Of("Game_Priority_Protected", display)), null, ProcessPriority.Unknown);

        var current = _control.GetPriority(pid.Value);
        if (!current.Success)
        {
            var reason = current.Error == OperationErrorKind.AccessDenied
                ? TextRef.Of("Game_Priority_Protected", display)
                : current.Error == OperationErrorKind.NotFound ? TextRef.Of("Game_Priority_GameNotRunning") : TextRef.Of("Game_Priority_Unreadable", display);
            return new Evaluation(NotApplicable(reason), null, ProcessPriority.Unknown);
        }

        var priority = current.Value;
        if (priority == ProcessPriority.Unknown)
            return new Evaluation(NotApplicable(TextRef.Of("Game_Priority_Unreadable", display)), null, priority);
        if (priority >= TargetPriority)
            return new Evaluation(NotApplicable(TextRef.Of("Game_Priority_AlreadyHigh", display)), null, priority);

        var change = new PlannedChange(
            $"{Id}:{process.ProcessId}",
            TextRef.Of("Game_Priority_Change", display),
            $"process:{process.Name} ({process.ProcessId})",
            SelectedByDefault: true,
            Reversible: true,
            RiskLevel.Low);
        return new Evaluation(Applicable([change]), process, priority);
    }

    private sealed record Evaluation(OptimizationPreview Preview, ProcessSnapshot? Process, ProcessPriority Current);
}
