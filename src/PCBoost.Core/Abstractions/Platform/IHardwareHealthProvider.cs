using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;

namespace PCBoost.Core.Abstractions.Platform;

/// <summary>
/// Sources Windows de l'état matériel lisibles sans autorisation administrateur. Aucune valeur n'est inventée :
/// une source illisible renvoie un échec (NotSupported, AccessDenied…) plutôt qu'une liste trompeuse.
/// </summary>
public interface IHardwareHealthProvider
{
    /// <summary>Disques physiques et état de santé déclaré par Windows (sans les compteurs de fiabilité, réservés à l'administrateur).</summary>
    OperationResult<IReadOnlyList<DiskHealthInfo>> GetDisks();

    /// <summary>Batteries (liste vide sur un PC de bureau).</summary>
    OperationResult<IReadOnlyList<BatteryInfo>> GetBatteries();

    /// <summary>Périphériques présents signalés en erreur (codes exclus : voir <see cref="DeviceProblem.IsReportable"/>).</summary>
    OperationResult<IReadOnlyList<DeviceProblem>> GetDeviceProblems();

    /// <summary>Horodatages des événements « vitesse du processeur limitée par le microprogramme » depuis <paramref name="since"/>.</summary>
    IReadOnlyList<DateTimeOffset> GetFirmwareLimitEvents(DateTimeOffset since);

    /// <summary>Démarrages récents (journal Système), du plus récent au plus ancien.</summary>
    IReadOnlyList<BootSession> GetRecentBoots(int max);
}

/// <summary>Opérations de l'assistant administrateur dédiées aux diagnostics et à la sécurité (voir <see cref="ElevatedOperations"/>).</summary>
public static class ElevatedHealthOperations
{
    /// <summary>Lecture des compteurs de fiabilité des disques (MSFT_StorageReliabilityCounter). Lecture seule.</summary>
    public const string DiskReliability = "disk.reliability";
    /// <summary>Lecture des mesures de démarrage de Windows (journal Diagnostics-Performance). Lecture seule.</summary>
    public const string BootPerformance = "boot.performance";
    /// <summary>Création d'un point de restauration Windows (description fixe), avec vérification.</summary>
    public const string RestorePointCreate = "restorepoint.create";
    /// <summary>Dernière exécution des programmes d'après le dossier Prefetch de Windows. Lecture seule.</summary>
    public const string AppsLastRun = "apps.lastrun";
}
