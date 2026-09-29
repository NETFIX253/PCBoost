using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Cleanup;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Optimization;

namespace PCBoost.Core.Services;

public interface IProcessService
{
    /// <summary>Liste enrichie (CPU % calculé entre deux échantillons, confiance, protection).</summary>
    Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken cancellationToken = default);

    /// <summary>Fermeture propre d'une application (fenêtre principale).</summary>
    OperationResult CloseApplication(int processId);

    /// <summary>Terminaison explicite demandée par l'utilisateur. Refusée pour les processus critiques.</summary>
    OperationResult TerminateProcess(int processId);

    ProtectionInfo GetProtection(string processName, string? executablePath);
}

/// <summary>Protection des processus critiques de Windows (§13, §53). Liste extensible.</summary>
public interface ICriticalProcessProtection
{
    ProtectionInfo GetProtection(string processName, string? executablePath);

    bool IsCritical(string processName);

    IReadOnlyCollection<string> CriticalProcessNames { get; }
}

public interface ISecurityService
{
    /// <summary>Évalue signature, éditeur et emplacement. Résultats mis en cache par chemin.</summary>
    TrustAssessment Assess(string? executablePath);
}

public interface IStartupService
{
    /// <summary>Entrées enrichies (impact mesuré, recommandation, signature).</summary>
    Task<IReadOnlyList<StartupEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);

    /// <summary>Active/désactive via le mécanisme StartupApproved de Windows (réversible, visible dans le Gestionnaire des tâches).</summary>
    Task<OperationResult> SetEnabledAsync(StartupEntry entry, bool enabled, CancellationToken cancellationToken = default);
}

public interface ICleanupService
{
    IReadOnlyList<CleanupCategory> GetCategories();

    Task<IReadOnlyList<CleanupScanResult>> ScanAsync(IReadOnlyCollection<string>? categoryIds = null, CancellationToken cancellationToken = default);

    /// <summary>Supprime le contenu des catégories. Irréversible : l'appelant doit avoir obtenu la confirmation.</summary>
    Task<CleanupSummary> CleanAsync(IReadOnlyCollection<string> categoryIds, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Orchestrateur des optimisations (§9, §38).</summary>
public interface IOptimizationManager
{
    IReadOnlyList<IOptimization> Optimizations { get; }

    IOptimization? Find(string optimizationId);

    /// <summary>Plan « Optimiser mon PC » : aperçus des modules pertinents, rien n'est modifié.</summary>
    Task<OptimizationPlan> BuildOneClickPlanAsync(SystemAnalysisReport analysis, CancellationToken cancellationToken = default);

    Task<OptimizationPlan> BuildPlanAsync(SessionType sessionType, IReadOnlyCollection<string> optimizationIds, SystemAnalysisReport? analysis, string? profileId = null, CancellationToken cancellationToken = default);

    /// <summary>Exécute le plan : validation de sûreté, session de restauration, application, vérification.</summary>
    Task<OptimizationRunReport> ExecuteAsync(OptimizationPlan plan, SystemAnalysisReport? analysis, IProgress<OptimizationProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Gestion du journal de restauration (§10).</summary>
public interface IRollbackManager
{
    Task<IChangeRecorder> BeginSessionAsync(SessionType type, TextRef title, string? profileId = null, CancellationToken cancellationToken = default);

    Task CompleteSessionAsync(Guid sessionId, long bytesFreed = 0, bool requiresRestart = false, CancellationToken cancellationToken = default);

    Task<OperationResult> UndoChangeAsync(Guid changeId, CancellationToken cancellationToken = default);

    Task<RollbackResult> RestoreSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    bool CanRollback(OptimizationSession session);

    Task<IReadOnlyList<OptimizationSession>> GetHistoryAsync(int limit = 100, CancellationToken cancellationToken = default);

    Task<OptimizationSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    event EventHandler<Guid>? SessionChanged;
}

/// <summary>Récupération après plantage (§69, §70).</summary>
public interface IRecoveryManager
{
    Task<IReadOnlyList<OptimizationSession>> FindInterruptedSessionsAsync(CancellationToken cancellationToken = default);

    Task<RollbackResult> RecoverAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task DismissAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public interface IProfileService
{
    IReadOnlyList<PerformanceProfile> GetProfiles();

    ProfileState State { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task<OptimizationPlan> PreviewActivationAsync(string profileId, CancellationToken cancellationToken = default);

    /// <summary>Active un profil : restaure d'abord le profil précédent, puis applique le nouveau.</summary>
    Task<OptimizationRunReport> ActivateAsync(OptimizationPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Revient à l'état d'avant le profil actif.</summary>
    Task<RollbackResult> DeactivateAsync(CancellationToken cancellationToken = default);

    Task SaveCustomProfileAsync(IReadOnlyList<string> optimizationIds, CancellationToken cancellationToken = default);

    event EventHandler? StateChanged;
}

/// <summary>Assistant « Optimiser un ancien PC » (§23).</summary>
public interface IOldPcAssistant
{
    Task<OldPcAssessment> AssessAsync(SystemAnalysisReport analysis, CancellationToken cancellationToken = default);

    Task<OptimizationPlan> BuildPlanAsync(OldPcLevel level, SystemAnalysisReport analysis, CancellationToken cancellationToken = default);
}

/// <summary>Surveillance légère et optimisations automatiques sûres uniquement si autorisées (§29).</summary>
public interface ISmartOptimizationService : IDisposable
{
    void Start();

    void Stop();

    /// <summary>Évalue l'état courant et notifie si pertinent. N'applique rien sans autorisation explicite.</summary>
    Task EvaluateNowAsync(CancellationToken cancellationToken = default);
}
