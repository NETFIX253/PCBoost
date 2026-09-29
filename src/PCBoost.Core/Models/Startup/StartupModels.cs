using PCBoost.Core.Common;
using PCBoost.Core.Models.Processes;

namespace PCBoost.Core.Models.Startup;

public enum StartupLocation
{
    RegistryRunUser = 0,
    RegistryRunMachine,
    RegistryRunMachine32,
    StartupFolderUser,
    StartupFolderCommon,
    ScheduledTaskLogon,
}

public enum StartupImpact { NotMeasured = 0, None, Low, Medium, High }

public enum StartupRecommendation { Keep = 0, Optional, Review, CanDisable }

/// <summary>Preuves mesurées utilisées pour estimer l'impact (processus actuellement lancé).</summary>
public sealed record StartupImpactEvidence(bool ProcessRunning, long? WorkingSetBytes, TimeSpan? CpuTime, long? IoReadBytes);

public sealed record StartupEntry
{
    /// <summary>Identifiant stable : emplacement + nom.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required StartupLocation Location { get; init; }
    /// <summary>Clé de registre, dossier de démarrage ou chemin de tâche planifiée.</summary>
    public required string SourcePath { get; init; }
    /// <summary>Nom de valeur de registre, nom de fichier du raccourci ou nom de tâche.</summary>
    public required string ItemName { get; init; }
    public string? Command { get; init; }
    public string? ExecutablePath { get; init; }
    public bool ExecutableExists { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public bool IsEnabled { get; init; }
    public bool RequiresElevation { get; init; }
    public SignatureInfo Signature { get; init; } = SignatureInfo.NotChecked;
    public bool IsMicrosoft { get; init; }
    public bool IsSecuritySoftware { get; init; }
    public StartupImpact Impact { get; init; } = StartupImpact.NotMeasured;
    public StartupImpactEvidence? Evidence { get; init; }
    public StartupRecommendation Recommendation { get; init; } = StartupRecommendation.Optional;
    public TextRef? RecommendationReason { get; init; }
}
