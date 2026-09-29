using PCBoost.Core.Models.Gaming;

namespace PCBoost.Gaming.Detection;

/// <summary>
/// Source de jeux installés (bibliothèque d'un lanceur, registre, manifestes). Chaque scanner est isolé :
/// une erreur n'empêche pas les autres de fonctionner. Aucun chemin fixe n'est supposé.
/// </summary>
public interface IGameLibraryScanner
{
    GameSource Source { get; }

    Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken);
}
