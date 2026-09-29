namespace PCBoost.Core.Services;

/// <summary>
/// Dernières lignes des journaux techniques de PCBoost pour le rapport de diagnostic. Les données personnelles
/// (profil, nom d'utilisateur, nom du PC) sont masquées une seconde fois à la lecture.
/// </summary>
public interface IDiagnosticLogSource
{
    Task<IReadOnlyList<string>> ReadRecentLinesAsync(int maxLines, CancellationToken cancellationToken = default);
}
