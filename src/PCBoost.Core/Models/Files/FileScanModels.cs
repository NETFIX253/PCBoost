using PCBoost.Core.Common;

namespace PCBoost.Core.Models.Files;

/// <summary>Fichier volumineux trouvé dans un dossier personnel.</summary>
public sealed record LargeFile(string Path, long Size, DateTimeOffset LastWriteUtc);

/// <summary>Copie d'un fichier en double (contenu identique, vérifié par empreinte SHA-256).</summary>
public sealed record DuplicateFile(string Path, DateTimeOffset LastWriteUtc);

/// <summary>Groupe de fichiers au contenu identique ; au moins une copie est toujours conservée.</summary>
public sealed record DuplicateGroup(string Hash, long Size, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>Espace récupérable en ne gardant qu'une copie.</summary>
    public long RecoverableBytes => Size * Math.Max(0, Files.Count - 1);
}

public enum FileScanStage { Listing = 0, Comparing = 1, Done = 2 }

public sealed record FileScanProgress(FileScanStage Stage, int FilesListed, int FilesCompared, int FilesToCompare);

/// <summary>Résultat de l'analyse des dossiers personnels (Documents, Téléchargements, Bureau, Images, Vidéos, Musique).</summary>
public sealed record FileScanResult(
    DateTimeOffset ScannedAt,
    IReadOnlyList<string> Roots,
    IReadOnlyList<LargeFile> LargeFiles,
    IReadOnlyList<DuplicateGroup> DuplicateGroups,
    int FilesScanned,
    int InaccessibleEntries,
    bool ComparisonLimited);

/// <summary>Bilan d'un envoi à la Corbeille.</summary>
public sealed record RecycleReport(int Moved, long MovedBytes, IReadOnlyList<(string Path, OperationResult Error)> Failures, bool Cancelled);
