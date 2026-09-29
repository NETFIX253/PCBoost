using PCBoost.Core.Common;
using PCBoost.Core.Models.Programs;

namespace PCBoost.Core.Services;

/// <summary>
/// Désinstallation assistée : liste des programmes installés (taille, dernière utilisation connue), sans les mises à jour,
/// pilotes, composants d'exécution, logiciels de sécurité ni PCBoost. La désinstallation passe toujours par le programme
/// officiel de l'éditeur, après confirmation ; elle est définitive et inscrite au journal.
/// </summary>
public interface IProgramInventoryService
{
    Task<ProgramInventory> GetInventoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Lit les dernières exécutions enregistrées par Windows (autorisation administrateur ponctuelle) et les conserve.</summary>
    Task<OperationResult> ReadLastRunAsync(CancellationToken cancellationToken = default);

    /// <summary>Lance le programme de désinstallation officiel puis attend que le programme disparaisse de la liste de Windows.</summary>
    Task<UninstallResult> UninstallAsync(InstalledProgram program, CancellationToken cancellationToken = default);
}
