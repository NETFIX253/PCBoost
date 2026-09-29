using PCBoost.Core.Common;
using PCBoost.Core.Models.Programs;

namespace PCBoost.Core.Abstractions.Platform;

/// <summary>
/// Lance le programme de désinstallation officiel de l'éditeur, visible (jamais en mode silencieux), et attend sa
/// fermeture. Windows affiche l'invite d'autorisation si le programme la demande.
/// </summary>
public interface IUninstallerLauncher
{
    /// <summary>Code de sortie du programme de désinstallation ; échec si le lancement est refusé ou impossible.</summary>
    Task<OperationResult<int>> RunAsync(UninstallCommand command, CancellationToken cancellationToken = default);
}
