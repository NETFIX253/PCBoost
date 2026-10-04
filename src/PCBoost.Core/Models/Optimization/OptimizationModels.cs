using PCBoost.Core.Common;

namespace PCBoost.Core.Models.Optimization;

public enum OptimizationCategory { Cleanup = 0, Startup, Power, Background, Visual, Cpu, Memory, Gpu, Windows, Network, Storage }

public enum SessionType { OneClick = 0, Manual, Cleanup, Startup, Profile, Gaming, OldPcAssistant, Automatic, DriverUpdate }

public enum SessionStatus
{
    /// <summary>En cours : si l'application s'arrête dans cet état, la récupération est proposée au démarrage.</summary>
    InProgress = 0,
    Completed,
    PartiallyCompleted,
    Failed,
    RolledBack,
    PartiallyRolledBack,
    /// <summary>Interrompue (plantage, arrêt) puis détectée par RecoveryManager.</summary>
    Interrupted,
    /// <summary>Session interrompue que l'utilisateur a choisi de conserver telle quelle.</summary>
    Dismissed,
}

public enum ChangeStatus
{
    /// <summary>Journalisée avant application (write-ahead). Si elle reste Pending, elle a pu être appliquée.</summary>
    Pending = 0,
    Applied,
    Failed,
    RolledBack,
    RollbackFailed,
    /// <summary>Action irréversible par nature (suppression de fichiers temporaires), consignée pour l'historique.</summary>
    Irreversible,
}

/// <summary>Modification système consignée dans le journal de restauration.</summary>
public sealed record ChangeRecord
{
    public required Guid Id { get; init; }
    public required Guid SessionId { get; init; }
    public required string OptimizationId { get; init; }
    /// <summary>Type de modification (ex. "registry.value", "power.scheme") : sélectionne le gestionnaire d'annulation.</summary>
    public required string Kind { get; init; }
    /// <summary>Cible lisible (ex. "HKCU\…\StartupApproved\Run : Discord").</summary>
    public required string Target { get; init; }
    public required TextRef Description { get; init; }
    /// <summary>État avant modification, sérialisé JSON (propre au type).</summary>
    public string? BeforeState { get; init; }
    public string? AfterState { get; init; }
    public required bool Reversible { get; init; }
    public ChangeStatus Status { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public DateTimeOffset? RolledBackAt { get; init; }
    public string? ErrorDetail { get; init; }
    public int Sequence { get; init; }
}

/// <summary>Modification à consigner avant application.</summary>
public sealed record PendingChange(
    string Kind,
    string OptimizationId,
    string Target,
    TextRef Description,
    string? BeforeState,
    bool Reversible);

/// <summary>Groupe de modifications (ChangeSet §10).</summary>
public sealed record OptimizationSession
{
    public required Guid Id { get; init; }
    public required SessionType Type { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public SessionStatus Status { get; init; }
    public TextRef? Title { get; init; }
    public string? ProfileId { get; init; }
    public long BytesFreed { get; init; }
    public bool RequiresRestart { get; init; }
    public IReadOnlyList<ChangeRecord> Changes { get; init; } = [];

    public int ReversibleChangeCount => Changes.Count(c => c.Reversible && c.Status == ChangeStatus.Applied);
}

public sealed record PlannedChange(
    string Id,
    TextRef Description,
    string Target,
    bool SelectedByDefault,
    bool Reversible,
    RiskLevel Risk,
    long? EstimatedBytes = null);

/// <summary>Aperçu (dry-run) d'une optimisation : rien n'est modifié (§39).</summary>
public sealed record OptimizationPreview(
    string OptimizationId,
    TextRef Name,
    bool Applicable,
    TextRef? NotApplicableReason,
    IReadOnlyList<PlannedChange> Changes,
    RiskLevel Risk,
    ImpactLevel Impact,
    bool Reversible,
    bool RequiresElevation,
    bool RequiresRestart)
{
    public long EstimatedBytes => Changes.Sum(c => c.EstimatedBytes ?? 0);

    public static OptimizationPreview NotApplicable(string id, TextRef name, TextRef reason, RiskLevel risk, ImpactLevel impact, bool reversible)
        => new(id, name, false, reason, [], risk, impact, reversible, false, false);
}

public sealed record OptimizationResult(
    string OptimizationId,
    OperationResult Outcome,
    int ChangesApplied,
    int ChangesFailed,
    long BytesFreed,
    bool RequiresRestart,
    IReadOnlyList<TextRef> Messages)
{
    public static OptimizationResult Skipped(string id, TextRef reason)
        => new(id, OperationResult.Ok(reason), 0, 0, 0, false, [reason]);
}

public sealed record RollbackResult(int Restored, int Failed, int Irreversible, IReadOnlyList<OperationResult> Errors)
{
    public bool Success => Failed == 0;
    public static RollbackResult Empty { get; } = new(0, 0, 0, []);
}

/// <summary>Plan d'exécution construit à partir des aperçus, avec la sélection de l'utilisateur.</summary>
public sealed record OptimizationPlan(
    SessionType SessionType,
    IReadOnlyList<OptimizationPreview> Previews,
    IReadOnlySet<string> SelectedChangeIds,
    string? ProfileId = null,
    // L’utilisateur a explicitement confirmé les actions irréversibles affichées.
    bool IrreversibleActionsConfirmed = false,
    bool HighRiskActionsConfirmed = false)
{
    public IEnumerable<OptimizationPreview> ApplicablePreviews => Previews.Where(p => p.Applicable);

    public int SelectedCount => Previews.SelectMany(p => p.Changes).Count(c => SelectedChangeIds.Contains(c.Id));
}

public enum OptimizationStage { Analysis = 0, Preparation, Backup, Optimization, Verification, Completed }

public sealed record OptimizationProgress(OptimizationStage Stage, double Percent, TextRef? CurrentAction);

/// <summary>Rapport final (§71).</summary>
public sealed record OptimizationRunReport(
    Guid SessionId,
    SessionStatus Status,
    int ActionsPerformed,
    int ActionsFailed,
    long BytesFreed,
    int StartupItemsDisabled,
    int ProcessesAdjusted,
    bool RequiresRestart,
    string? ProfileId,
    IReadOnlyList<OptimizationResult> Results,
    IReadOnlyList<TextRef> Warnings);

public sealed record SafetyViolation(string Code, TextRef Message, bool Blocking);

public sealed record SafetyValidationResult(bool Allowed, IReadOnlyList<SafetyViolation> Violations)
{
    public static SafetyValidationResult Ok { get; } = new(true, []);
}

/// <summary>Profil d'utilisation (§15).</summary>
public sealed record PerformanceProfile(
    string Id,
    TextRef Name,
    TextRef Description,
    IReadOnlyList<string> OptimizationIds,
    bool IsBuiltIn,
    string IconGlyph)
{
    public const string BalancedId = "balanced";
    public const string ProductivityId = "productivity";
    public const string GamingId = "gaming";
    public const string PowerSaverId = "power-saver";
    public const string CustomId = "custom";
}

public sealed record ProfileState(string? ActiveProfileId, Guid? ActiveSessionId, DateTimeOffset? ActivatedAt);

public enum OldPcLevel { Essential = 0, Standard = 1, Advanced = 2 }

public sealed record OldPcAssessment(
    bool LowMemory,
    bool OldOrWeakCpu,
    bool MechanicalSystemDrive,
    bool LowDiskSpace,
    bool HeavyStartup,
    bool ManyBackgroundProcesses,
    IReadOnlyList<TextRef> Findings,
    IReadOnlyDictionary<OldPcLevel, IReadOnlyList<string>> OptimizationsByLevel);
