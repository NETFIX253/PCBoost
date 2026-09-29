using PCBoost.Core.Common;
using PCBoost.Core.Models.SystemInfo;

namespace PCBoost.Core.Abstractions.Platform;

/// <summary>Lecture des processus (sans modification).</summary>
public interface IProcessProvider
{
    /// <summary>Instantané complet (mémoire, temps CPU, E/S). Les processus inaccessibles sont inclus avec des valeurs partielles.</summary>
    IReadOnlyList<ProcessSnapshot> GetProcesses();

    /// <summary>Énumération légère (PID + nom) pour la surveillance des jeux.</summary>
    IReadOnlyList<ProcessIdentity> GetProcessIdentities();

    ProcessSnapshot? GetProcess(int processId);

    /// <summary>Chemin complet de l'exécutable, ou null si inaccessible.</summary>
    string? GetExecutablePath(int processId);

    bool IsRunning(int processId, DateTimeOffset? expectedStartTime = null);

    int CurrentProcessId { get; }

    int CurrentSessionId { get; }
}

/// <summary>Actions sur les processus. Toute action passe d'abord par CriticalProcessProtection côté service.</summary>
public interface IProcessControl
{
    /// <summary>Demande une fermeture propre (WM_CLOSE sur la fenêtre principale).</summary>
    OperationResult RequestClose(int processId);

    /// <summary>Termine le processus. Réservé aux actions explicites de l'utilisateur.</summary>
    OperationResult Terminate(int processId);

    OperationResult<ProcessPriority> GetPriority(int processId);

    OperationResult SetPriority(int processId, ProcessPriority priority);

    /// <summary>Mode efficacité (EcoQoS / Power Throttling, API documentée SetProcessInformation).</summary>
    OperationResult<bool> GetEfficiencyMode(int processId);

    OperationResult SetEfficiencyMode(int processId, bool enabled);

    bool IsEfficiencyModeSupported { get; }
}
