using PCBoost.Core.Models.Files;

namespace PCBoost.Core.Services;

/// <summary>
/// Gros fichiers et doublons dans les dossiers personnels. Lecture seule pendant l'analyse ; les fichiers choisis par
/// l'utilisateur vont uniquement à la Corbeille (restaurables). Une copie de chaque groupe de doublons est toujours
/// conservée, et chaque fichier est revérifié (présence, taille, date) juste avant son envoi.
/// </summary>
public interface IFileCleanupService
{
    Task<FileScanResult> ScanAsync(IProgress<FileScanProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<RecycleReport> MoveToRecycleBinAsync(FileScanResult scan, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);
}
