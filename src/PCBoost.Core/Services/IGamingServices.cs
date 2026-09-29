using PCBoost.Core.Models.Gaming;

namespace PCBoost.Core.Services;

public interface IGameDetectionService : IDisposable
{
    /// <summary>Jeux installés détectés (bibliothèques, registre, manifestes, GameConfigStore, jeux personnalisés).</summary>
    Task<IReadOnlyList<GameInfo>> GetInstalledGamesAsync(bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>Jeu actuellement en cours d'exécution, s'il est reconnu.</summary>
    Task<DetectedGameProcess?> DetectRunningGameAsync(CancellationToken cancellationToken = default);

    /// <summary>Démarre la surveillance légère des processus (intervalle de quelques secondes).</summary>
    void StartWatching();

    void StopWatching();

    bool IsWatching { get; }

    event EventHandler<DetectedGameProcess>? GameStarted;

    event EventHandler<DetectedGameProcess>? GameExited;
}

public interface IGamingService
{
    GamingState State { get; }

    GamingSession? CurrentSession { get; }

    DetectedGameProcess? CurrentGame { get; }

    /// <summary>Aperçu des optimisations Gaming applicables (rien n'est modifié).</summary>
    Task<IReadOnlyList<ActiveGamingOptimization>> PreviewAsync(DetectedGameProcess? game, CancellationToken cancellationToken = default);

    /// <summary>Active le mode Gaming (instantané + optimisations sûres + surveillance).</summary>
    Task<GamingActivationReport> ActivateAsync(DetectedGameProcess? game, CancellationToken cancellationToken = default);

    /// <summary>Désactive et restaure chaque modification effectuée pendant la session.</summary>
    Task<GamingRestoreReport> DeactivateAsync(CancellationToken cancellationToken = default);

    GamingLiveMetrics? LiveMetrics { get; }

    /// <summary>Vérifications des paramètres graphiques Windows (mode Jeu, jeux fenêtrés, GPU).</summary>
    IReadOnlyList<GameSettingCheck> CheckWindowsGameSettings(DetectedGameProcess? game);

    event EventHandler? StateChanged;

    event EventHandler<GamingLiveMetrics>? LiveMetricsUpdated;
}

/// <summary>Détection automatique des jeux et restauration à la fermeture (§19).</summary>
public interface IAutoGamingMode : IDisposable
{
    void Start();

    void Stop();

    /// <summary>Jeu détecté alors que l'activation automatique est en mode « Demander ».</summary>
    event EventHandler<DetectedGameProcess>? ActivationSuggested;
}

public interface IBenchmarkService
{
    /// <summary>Mesure pendant <paramref name="duration"/> (CPU, GPU, RAM, disque, et images si un jeu est suivi).</summary>
    Task<BenchmarkRun> RunAsync(BenchmarkPhase phase, TimeSpan duration, string? label, int? gameProcessId, Guid? pairedRunId, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BenchmarkRun>> GetHistoryAsync(int limit = 50, CancellationToken cancellationToken = default);

    BenchmarkComparison Compare(BenchmarkRun before, BenchmarkRun after);
}
