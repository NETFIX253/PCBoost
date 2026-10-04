using PCBoost.Core.Common;
using PCBoost.Core.Models.Drivers;

namespace PCBoost.Core.Abstractions.Platform;

/// <summary>Périphériques présents et pilotes installés (lecture seule, sans autorisation administrateur).</summary>
public interface IDeviceDriverProvider
{
    OperationResult<IReadOnlyList<InstalledDriver>> GetInstalledDrivers();

    /// <summary>Fabricant et modèle du PC (null si illisible).</summary>
    Drivers.ComputerIdentity? GetComputerIdentity();
}

/// <summary>Résultat d'une recherche Windows Update (pilotes uniquement).</summary>
public sealed record DriverSearchResult(
    OperationResult Outcome,
    IReadOnlyList<DriverUpdateOffer> Offers,
    bool RebootRequired,
    bool InstallerBusy)
{
    public static DriverSearchResult Failed(OperationResult outcome) => new(outcome, [], false, false);
}

/// <summary>
/// Recherche des mises à jour de pilotes proposées par Windows Update pour ce PC (agent Windows Update, sans
/// autorisation administrateur). Rien n'est téléchargé ni installé. Le serveur configuré par l'organisation (WSUS)
/// et les mises à jour masquées par l'utilisateur sont respectés.
/// </summary>
public interface IDriverUpdateSource
{
    Task<DriverSearchResult> SearchAsync(CancellationToken cancellationToken = default);
}

/// <summary>Coût de la connexion Internet active (connexion limitée : données mobiles, forfait).</summary>
public interface INetworkCostProvider
{
    /// <summary>true = connexion limitée ; null = inconnu ou aucune connexion.</summary>
    bool? IsMeteredConnection();
}

/// <summary>Opérations de l'assistant administrateur consacrées aux pilotes (voir <see cref="ElevatedOperations"/>).</summary>
public static class ElevatedDriverOperations
{
    /// <summary>
    /// Point de restauration Windows neuf (obligatoire), puis téléchargement et installation, par l'agent Windows Update,
    /// des mises à jour de pilotes désignées par leur identifiant, revérifiées par <see cref="Drivers.DriverUpdatePolicy"/>.
    /// </summary>
    public const string Install = "drivers.install";

    /// <summary>Retour au pilote précédent (« Restaurer le pilote » de Windows) pour des périphériques désignés.</summary>
    public const string Rollback = "driver.rollback";

    /// <summary>
    /// Point de restauration Windows neuf, à la demande, avant l'utilisation de l'outil d'un fabricant ; activation de la
    /// protection du système seulement si l'utilisateur l'a explicitement autorisée.
    /// </summary>
    public const string RestorePoint = "restorepoint.drivers";
}
