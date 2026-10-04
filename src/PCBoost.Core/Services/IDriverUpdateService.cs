using PCBoost.Core.Common;
using PCBoost.Core.Models.Drivers;

namespace PCBoost.Core.Services;

/// <summary>
/// Mises à jour de pilotes sécurisées : recherche dans Windows Update (pilotes signés et validés par Microsoft),
/// classement selon <see cref="Drivers.DriverUpdatePolicy"/>, installation après un point de restauration Windows neuf
/// et obligatoire, chaque pilote étant inscrit au journal de restauration (retour au pilote précédent).
/// </summary>
public interface IDriverUpdateService
{
    /// <summary>Dernière recherche de cette session, s'il y en a une.</summary>
    DriverScanResult? Latest { get; }

    /// <summary>Recherche les mises à jour (aucune modification, aucune autorisation administrateur).</summary>
    Task<DriverScanResult> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installe les mises à jour choisies (classées installables) : une seule autorisation administrateur pour le point
    /// de restauration et les installations. <paramref name="enableSystemProtection"/> : activer la protection du système
    /// si elle est désactivée (choix explicite de l'utilisateur), faute de quoi rien n'est installé.
    /// </summary>
    Task<DriverInstallResult> InstallAsync(IReadOnlyList<DriverUpdateCandidate> updates, bool enableSystemProtection,
        IProgress<DriverInstallProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Revient au pilote précédent pour une mise à jour inscrite au journal de restauration.</summary>
    Task<OperationResult> RollbackAsync(Guid changeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Crée un point de restauration Windows neuf (une autorisation administrateur), par exemple avant d'utiliser l'outil
    /// officiel d'un fabricant, dont les installations ne sont pas inscrites au journal de PCBoost.
    /// </summary>
    Task<DriverRestorePointResult> CreateRestorePointAsync(bool enableSystemProtection, CancellationToken cancellationToken = default);
}
