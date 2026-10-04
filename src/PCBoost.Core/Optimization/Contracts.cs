using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Optimization;

namespace PCBoost.Core.Optimization;

/// <summary>
/// Contexte transmis aux optimisations : dernière analyse, sélection de l'utilisateur, consignation des modifications.
/// </summary>
public sealed class OptimizationContext
{
    public OptimizationContext(SystemAnalysisReport? analysis, IReadOnlySet<string>? selectedChangeIds = null, string? profileId = null)
    {
        Analysis = analysis;
        SelectedChangeIds = selectedChangeIds;
        ProfileId = profileId;
    }

    public SystemAnalysisReport? Analysis { get; }

    /// <summary>Null = sélection par défaut de l'aperçu.</summary>
    public IReadOnlySet<string>? SelectedChangeIds { get; }

    public string? ProfileId { get; }

    /// <summary>Données complémentaires (ex. PID du jeu pour les optimisations Gaming).</summary>
    public IDictionary<string, object> Items { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

    public bool IsSelected(PlannedChange change)
        => SelectedChangeIds is null ? change.SelectedByDefault : SelectedChangeIds.Contains(change.Id);
}

/// <summary>
/// Module d'optimisation indépendant (§38, §76). Une optimisation :
/// 1) vérifie sa pertinence (<see cref="PreviewAsync"/>, dry-run, ne modifie rien),
/// 2) applique uniquement les changements sélectionnés en les consignant AVANT application via <see cref="IChangeRecorder"/>,
/// 3) sait annuler ses changements (délégué aux <see cref="IChangeHandler"/> par type).
/// </summary>
public interface IOptimization
{
    string Id { get; }

    TextRef Name { get; }

    TextRef Description { get; }

    OptimizationCategory Category { get; }

    RiskLevel RiskLevel { get; }

    ImpactLevel ImpactLevel { get; }

    /// <summary>Toutes les modifications de ce module peuvent-elles être annulées ?</summary>
    bool IsReversible { get; }

    /// <summary>Version minimale de Windows (numéro de build) requise.</summary>
    int MinimumWindowsBuild { get; }

    Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default);

    Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default);

    Task<RollbackResult> RollbackAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Consignation write-ahead des modifications d'une session.</summary>
public interface IChangeRecorder
{
    Guid SessionId { get; }

    /// <summary>
    /// Consigne la modification (état « avant » inclus) PUIS exécute <paramref name="apply"/>.
    /// Si l'application échoue, la modification est marquée Failed. Aucune exception n'est propagée
    /// hors annulation.
    /// </summary>
    Task<OperationResult> ApplyAsync(PendingChange change, Func<CancellationToken, Task<OperationResult>> apply, CancellationToken cancellationToken = default);

    /// <summary>Consigne une action irréversible déjà effectuée (ex. suppression de fichiers temporaires).</summary>
    Task RecordIrreversibleAsync(PendingChange change, OperationResult outcome, long bytesFreed, CancellationToken cancellationToken = default);
}

/// <summary>Annule un type de modification à partir de son état « avant » sérialisé.</summary>
public interface IChangeHandler
{
    string Kind { get; }

    bool CanUndo(ChangeRecord change);

    Task<OperationResult> UndoAsync(ChangeRecord change, CancellationToken cancellationToken = default);
}

public static class ChangeKinds
{
    public const string RegistryValue = "registry.value";
    public const string PowerScheme = "power.scheme";
    public const string ProcessPriority = "process.priority";
    public const string ProcessEfficiency = "process.efficiency";
    public const string VisualEffects = "visual.effects";
    public const string ScheduledTask = "scheduledtask.enabled";
    public const string FileDeletion = "file.delete";
    public const string RecycleBin = "recyclebin.empty";
    /// <summary>Mise à jour de pilote par Windows Update ; annulation = retour au pilote précédent (DriverUpdateState).</summary>
    public const string DriverUpdate = "driver.update";
    /// <summary>
    /// Protection du système activée avant une mise à jour de pilotes (autorisée par l'utilisateur) : consignée comme
    /// action non annulée automatiquement (la désactiver supprimerait les points de restauration).
    /// </summary>
    public const string SystemProtection = "systemprotection.enable";
}

/// <summary>Validation de sûreté avant application (§57).</summary>
public interface IOptimizationSafetyValidator
{
    SafetyValidationResult Validate(IOptimization optimization, OptimizationPreview preview, OptimizationPlan plan);

    /// <summary>Vérifie qu'une modification ne vise pas une cible interdite (Defender, pare-feu, Windows Update…).</summary>
    SafetyValidationResult ValidateChange(PendingChange change);
}
