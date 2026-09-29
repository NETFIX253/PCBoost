using System.Globalization;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Security;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Modules;

/// <summary>
/// background-apps : applique le mode efficacité (EcoQoS, Windows 11) ou, à défaut, la priorité « Inférieure à la normale »
/// aux applications de l'utilisateur qui travaillent en arrière-plan. Aucun processus n'est fermé. Effet limité à la vie du
/// processus, annulable. Exclus : processus critiques/sensibles, sécurité, premier plan, exclusions de l'utilisateur, PCBoost.
/// </summary>
public sealed class BackgroundAppsOptimization : OptimizationModuleBase
{
    internal const int MaxProcesses = 15;
    internal const double CpuThresholdPercent = 5;

    private readonly IProcessService _processService;
    private readonly IProcessProvider _processes;
    private readonly IProcessControl _control;
    private readonly IForegroundWindowProvider _foreground;
    private readonly ISettingsService _settings;
    private readonly ICriticalProcessProtection _protection;
    private readonly ISecurityService _security;

    public BackgroundAppsOptimization(IProcessService processService, IProcessProvider processes, IProcessControl control,
        IForegroundWindowProvider foreground, ISettingsService settings, ICriticalProcessProtection protection, ISecurityService security,
        IRollbackManager rollback) : base(rollback)
    {
        _processService = processService;
        _processes = processes;
        _control = control;
        _foreground = foreground;
        _settings = settings;
        _protection = protection;
        _security = security;
    }

    public override string Id => OptimizationIds.BackgroundApps;
    protected override string Key => "BackgroundApps";
    public override OptimizationCategory Category => OptimizationCategory.Background;
    public override RiskLevel RiskLevel => RiskLevel.Medium;
    public override ImpactLevel ImpactLevel => ImpactLevel.Medium;
    public override bool IsReversible => true;

    internal static string ChangeId(int processId, DateTimeOffset? startTime)
        => string.Create(CultureInfo.InvariantCulture, $"{OptimizationIds.BackgroundApps}:{processId}:{startTime?.UtcTicks ?? 0}");

    internal static bool TryParseChangeId(string id, out int processId, out long startTicks)
    {
        processId = 0;
        startTicks = 0;
        var parts = id.Split(':');
        return parts.Length == 3
               && parts[0] == OptimizationIds.BackgroundApps
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out processId)
               && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out startTicks);
    }

    private sealed record Candidate(int ProcessId, string Name, DateTimeOffset? StartTime, double CpuPercent, bool KnownHeavy, bool HasWindow);

    /// <summary>Garde-fous indépendants de la mesure (appliqués à l'aperçu ET juste avant la modification).</summary>
    private bool IsEligible(string name, string? path, int processId, bool isCurrentUser, int sessionId, int? foregroundPid,
        IReadOnlySet<string> exclusions, ProcessPriority priority, ProtectionInfo protection, TrustAssessment trust)
    {
        var normalized = CriticalProcessProtection.Normalize(name);
        if (KnownSoftware.NeverTouchProcesses.Contains(normalized)) return false;
        if (_protection.IsCritical(name) || protection.Level != ProtectionLevel.None) return false;
        if (processId <= 4 || processId == _processes.CurrentProcessId || processId == foregroundPid) return false;
        if (!isCurrentUser || sessionId != _processes.CurrentSessionId) return false;
        if (exclusions.Contains(normalized)) return false;
        if (priority is not (ProcessPriority.Normal or ProcessPriority.AboveNormal)) return false;
        if (KnownSoftware.IsSecuritySoftware(normalized, path, trust.Publisher)) return false;
        return true;
    }

    private IReadOnlySet<string> Exclusions()
        => (_settings.Current.Gaming.BackgroundExclusions ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(CriticalProcessProtection.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private int? ForegroundProcessId()
    {
        try
        {
            return _foreground.GetForegroundProcessId();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<Candidate>> FindCandidatesAsync(CancellationToken cancellationToken)
    {
        var processes = await _processService.GetProcessesAsync(cancellationToken).ConfigureAwait(false);
        var foreground = ForegroundProcessId();
        var exclusions = Exclusions();
        return processes
            .Where(p => IsEligible(p.Name, p.ExecutablePath, p.ProcessId, p.IsCurrentUser, p.SessionId, foreground, exclusions, p.Priority, p.Protection, p.Trust))
            .Select(p => new Candidate(p.ProcessId, p.Name, p.StartTime, p.CpuPercent,
                KnownSoftware.HeavyBackgroundProcesses.Contains(CriticalProcessProtection.Normalize(p.Name)), p.HasWindow))
            // Connu comme lourd (hors premier plan), ou mesuré au-delà du seuil sans aucune fenêtre.
            .Where(c => c.KnownHeavy || (c.CpuPercent > CpuThresholdPercent && !c.HasWindow))
            .OrderByDescending(c => c.CpuPercent)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxProcesses)
            .ToList();
    }

    private static PlannedChange Planned(Candidate candidate) => new(
        ChangeId(candidate.ProcessId, candidate.StartTime),
        TextRef.Of(candidate.KnownHeavy ? "Opt_BackgroundApps_ThrottleKnown" : "Opt_BackgroundApps_ThrottleMeasured",
            candidate.Name, Math.Round(candidate.CpuPercent, 1)),
        $"process:{candidate.Name} ({candidate.ProcessId.ToString(CultureInfo.InvariantCulture)})",
        SelectedByDefault: true,
        Reversible: true,
        Risk: RiskLevel.Medium);

    public override async Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default)
    {
        var candidates = await FindCandidatesAsync(cancellationToken).ConfigureAwait(false);
        return candidates.Count == 0
            ? NotApplicable(TextRef.Of("Opt_BackgroundApps_NothingToDo"))
            : Applicable(candidates.Select(Planned).ToList());
    }

    public override async Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default)
    {
        IEnumerable<(int ProcessId, long StartTicks)> targets;
        if (context.SelectedChangeIds is null)
        {
            var candidates = await FindCandidatesAsync(cancellationToken).ConfigureAwait(false);
            targets = candidates.Select(c => (c.ProcessId, c.StartTime?.UtcTicks ?? 0L));
        }
        else
        {
            targets = context.SelectedChangeIds
                .Select(id => TryParseChangeId(id, out var pid, out var ticks) ? (pid, ticks) : (0, 0L))
                .Where(t => t.Item1 > 0);
        }

        var foreground = ForegroundProcessId();
        var exclusions = Exclusions();
        var outcomes = new List<OperationResult>();
        foreach (var (processId, startTicks) in targets.Distinct().Take(MaxProcesses))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _processes.GetProcess(processId);
            // Processus terminé ou PID réutilisé : rien à faire.
            if (snapshot is null || (snapshot.StartTime?.UtcTicks ?? 0L) != startTicks) continue;

            var protection = _protection.GetProtection(snapshot.Name, snapshot.ExecutablePath);
            var trust = _security.Assess(snapshot.ExecutablePath);
            if (!IsEligible(snapshot.Name, snapshot.ExecutablePath, snapshot.ProcessId, snapshot.IsCurrentUser, snapshot.SessionId, foreground,
                    exclusions, snapshot.Priority, protection, trust))
                continue;

            var outcome = await ThrottleAsync(snapshot, recorder, cancellationToken).ConfigureAwait(false);
            if (outcome is not null) outcomes.Add(outcome);
        }
        return Summarize(outcomes);
    }

    /// <summary>Null si le processus est déjà ralenti (rien à consigner).</summary>
    private async Task<OperationResult?> ThrottleAsync(ProcessSnapshot process, IChangeRecorder recorder, CancellationToken cancellationToken)
    {
        var target = $"process:{process.Name} ({process.ProcessId.ToString(CultureInfo.InvariantCulture)})";
        if (_control.IsEfficiencyModeSupported)
        {
            var current = _control.GetEfficiencyMode(process.ProcessId);
            if (!current.Success) return current.ToResult();
            if (current.Value) return null;

            var change = new PendingChange(ChangeKinds.ProcessEfficiency, Id, target, TextRef.Of("Opt_Change_EfficiencyMode", process.Name),
                ChangeStateSerializer.Serialize(new ProcessEfficiencyState(process.ProcessId, process.Name, process.StartTime, false)), Reversible: true);
            return await recorder.ApplyWithAfterStateAsync(change,
                ChangeStateSerializer.Serialize(new ProcessEfficiencyState(process.ProcessId, process.Name, process.StartTime, true)),
                _ => Task.FromResult(_control.SetEfficiencyMode(process.ProcessId, true)), cancellationToken).ConfigureAwait(false);
        }

        var priority = _control.GetPriority(process.ProcessId);
        if (!priority.Success) return priority.ToResult();
        if (priority.Value is not (ProcessPriority.Normal or ProcessPriority.AboveNormal)) return null;

        var priorityChange = new PendingChange(ChangeKinds.ProcessPriority, Id, target, TextRef.Of("Opt_Change_LowerPriority", process.Name),
            ChangeStateSerializer.Serialize(new ProcessPriorityState(process.ProcessId, process.Name, process.StartTime, priority.Value)), Reversible: true);
        return await recorder.ApplyWithAfterStateAsync(priorityChange,
            ChangeStateSerializer.Serialize(new ProcessPriorityState(process.ProcessId, process.Name, process.StartTime, ProcessPriority.BelowNormal)),
            _ => Task.FromResult(_control.SetPriority(process.ProcessId, ProcessPriority.BelowNormal)), cancellationToken).ConfigureAwait(false);
    }
}
