using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Startup;

namespace PCBoost.Core.Abstractions.Platform;

public sealed record ScheduledTaskInfo(string Path, string Name, string? Author, string? ExecutablePath, string? Arguments, bool Enabled, bool IsMicrosoft);

/// <summary>Tâches planifiées déclenchées à l'ouverture de session (Task Scheduler COM).</summary>
public interface IScheduledTaskProvider
{
    IReadOnlyList<ScheduledTaskInfo> GetLogonTasks();

    OperationResult SetEnabled(string taskPath, bool enabled);

    bool? IsEnabled(string taskPath);
}

/// <summary>Source des programmes lancés au démarrage (Run, dossiers Démarrage, tâches).</summary>
public interface IStartupProvider
{
    Task<IReadOnlyList<StartupEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Paramètres d'effets visuels de Windows (SystemParametersInfo, transparence).</summary>
public sealed record VisualEffectsState(
    bool ClientAreaAnimation,
    bool MenuAnimation,
    bool ComboBoxAnimation,
    bool ListBoxSmoothScrolling,
    bool TooltipAnimation,
    bool WindowMinMaxAnimation,
    bool CursorShadow,
    bool DragFullWindows,
    bool Transparency);

public interface IVisualEffectsProvider
{
    VisualEffectsState? GetState();

    OperationResult SetState(VisualEffectsState state);
}

/// <summary>Demande d'opération privilégiée ponctuelle, exécutée par PCBoost.Elevator (liste blanche stricte).</summary>
public sealed record ElevatedRequest(string Operation, IReadOnlyDictionary<string, string> Parameters);

public sealed record ElevatedResponse(OperationResult Outcome, IReadOnlyDictionary<string, string> Data);

public static class ElevatedOperations
{
    /// <summary>Nettoyage d'une catégorie système (le chemin est résolu par l'Elevator, jamais transmis).</summary>
    public const string CleanupCategory = "cleanup.category";
    /// <summary>Écriture d'une valeur sous une clé HKLM de la liste blanche (StartupApproved).</summary>
    public const string RegistrySetValue = "registry.set";
    public const string RegistryDeleteValue = "registry.delete";
    /// <summary>Activation/désactivation d'une tâche planifiée de démarrage non Microsoft.</summary>
    public const string ScheduledTaskSetEnabled = "task.setenabled";
    /// <summary>Capture ETW des présentations d'images pour un PID, diffusée par canal nommé.</summary>
    public const string FrameCapture = "frames.capture";
}

public interface IElevationService
{
    bool IsElevated { get; }

    /// <summary>
    /// Lance PCBoost.Elevator (UAC). Renvoie ElevationCancelled si l'utilisateur refuse.
    /// L'opération frames.capture passe exclusivement par <see cref="IFrameTimeSource"/> (refusée ici avec InvalidInput).
    /// </summary>
    Task<ElevatedResponse> RunAsync(ElevatedRequest request, CancellationToken cancellationToken = default);
}

public sealed record CommandSpec(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout);

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Exécution sécurisée de commandes système (§65) : exécutables d'une liste blanche, arguments échappés, journalisés.</summary>
public interface ICommandRunner
{
    Task<OperationResult<CommandResult>> RunAsync(CommandSpec spec, CancellationToken cancellationToken = default);
}

/// <summary>Intégration avec l'Explorateur et le navigateur (actions explicites de l'utilisateur).</summary>
public interface IShellService
{
    OperationResult OpenFolder(string path);

    OperationResult RevealInExplorer(string filePath);

    OperationResult ShowFileProperties(string filePath);

    OperationResult OpenUri(Uri uri);

    /// <summary>Recherche web explicite sur un nom de processus (aucune donnée envoyée sans clic).</summary>
    OperationResult SearchOnline(string term);
}

/// <summary>Source d'horodatages de présentation d'images, sans injection dans le jeu (ETW DXGI/D3D9).</summary>
public interface IFrameTimeSource
{
    FrameCaptureAvailability GetAvailability();

    /// <summary>Démarre la capture (autorisation administrateur demandée). Null en cas d'échec : voir <see cref="LastError"/>.</summary>
    Task<IFrameCaptureSession?> StartAsync(int processId, CancellationToken cancellationToken = default);

    /// <summary>Raison du dernier échec de <see cref="StartAsync"/> (UAC refusé, assistant absent…).</summary>
    TextRef? LastError { get; }
}

public interface IFrameCaptureSession : IAsyncDisposable
{
    int ProcessId { get; }

    bool IsActive { get; }

    TextRef? Error { get; }

    event EventHandler<FrameBatch>? FramesReceived;
}

/// <summary>Enregistrement du lancement avec Windows (HKCU\…\Run).</summary>
public interface IAutoStartRegistration
{
    bool IsEnabled();

    OperationResult SetEnabled(bool enabled);
}
