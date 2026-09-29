using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;

namespace PCBoost.Core.Services;

/// <summary>
/// Santé du matériel : disques, batterie, périphériques, limitation thermique. Lecture seule. Une vérification
/// périodique (démarrée par l'application) prévient l'utilisateur si un disque est signalé en mauvais état.
/// </summary>
public interface IHardwareHealthService : IDisposable
{
    void Start();

    void Stop();

    /// <summary>Dernier état relevé pendant cette session, s'il existe.</summary>
    HardwareHealthReport? Latest { get; }

    /// <summary>Relève l'état actuel (sources lisibles sans autorisation, compteurs de fiabilité conservés localement).</summary>
    Task<HardwareHealthReport> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Lit les compteurs de fiabilité des disques (autorisation administrateur ponctuelle), puis relève l'état.</summary>
    Task<OperationResult> ReadDiskReliabilityAsync(CancellationToken cancellationToken = default);

    event EventHandler<HardwareHealthReport>? ReportUpdated;
}

/// <summary>Durée de démarrage de Windows et comparaison avant / après les changements de programmes au démarrage.</summary>
public interface IBootTimeService
{
    Task<BootTimeReport> GetReportAsync(CancellationToken cancellationToken = default);

    /// <summary>Lit les mesures de démarrage de Windows (autorisation administrateur ponctuelle) et les conserve localement.</summary>
    Task<OperationResult> ReadMeasurementsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Surveille les échantillons de performance : un épisode est retenu quand le processeur, très sollicité, fonctionne
/// nettement en dessous de sa fréquence de base (sur secteur, hors mode Économie d'énergie).
/// </summary>
public interface IThermalThrottlingDetector : IDisposable
{
    int EpisodeCount { get; }

    ThrottlingEpisode? LastEpisode { get; }

    void Start();

    void Stop();

    event EventHandler<ThrottlingEpisode>? EpisodeDetected;
}

/// <summary>Point de restauration Windows créé avant les optimisations avancées (autorisation administrateur ponctuelle).</summary>
public interface IRestorePointService
{
    Task<RestorePointResult> CreateAsync(CancellationToken cancellationToken = default);
}
