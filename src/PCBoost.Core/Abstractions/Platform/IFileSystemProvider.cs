using PCBoost.Core.Common;

namespace PCBoost.Core.Abstractions.Platform;

public enum KnownFolder
{
    UserProfile = 0,
    LocalAppData,
    RoamingAppData,
    ProgramData,
    UserTemp,
    WindowsDirectory,
    WindowsTemp,
    ProgramFiles,
    ProgramFilesX86,
    Documents,
    Downloads,
    Desktop,
    Pictures,
    Videos,
    Music,
    StartupUser,
    StartupCommon,
    StartMenuPrograms,
}

/// <param name="IsOffline">Contenu non présent sur le disque (fichier en ligne, rappel à l'accès) : ne jamais le lire.</param>
public sealed record FileEntry(string Path, long Size, DateTimeOffset LastWriteUtc, bool IsReadOnly, bool IsSystem, bool IsHidden, bool IsOffline = false);

public sealed record DirectorySizeResult(long Bytes, int FileCount, int InaccessibleEntries);

/// <summary>Accès au système de fichiers. N'emprunte jamais les liens symboliques ni les jonctions lors des parcours.</summary>
public interface IFileSystemProvider
{
    string? GetKnownFolder(KnownFolder folder);

    string? GetEnvironmentVariable(string name);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>Parcourt les fichiers sans suivre les points d'analyse (reparse points). Les erreurs d'accès sont ignorées et comptées.</summary>
    IEnumerable<FileEntry> EnumerateFiles(string directory, bool recursive, CancellationToken cancellationToken = default);

    IEnumerable<string> EnumerateDirectories(string directory);

    DirectorySizeResult GetDirectorySize(string directory, CancellationToken cancellationToken = default);

    OperationResult DeleteFile(string path);

    /// <summary>Supprime les sous-dossiers vides sous <paramref name="root"/> (jamais la racine).</summary>
    int DeleteEmptySubdirectories(string root);

    string? ReadAllText(string path);

    OperationResult WriteAllText(string path, string content);

    OperationResult CreateDirectory(string path);

    IReadOnlyList<string> GetFixedDriveRoots();

    string GetFullPath(string path);

    /// <summary>État actuel d'un fichier (taille, date, attributs) ; null s'il est absent, inaccessible ou s'il s'agit d'un point d'analyse.</summary>
    FileEntry? GetFileInfo(string path);

    /// <summary>Date du dernier accès au fichier (NTFS) ; null si indisponible.</summary>
    DateTimeOffset? GetLastAccessTimeUtc(string path);

    /// <summary>Ouvre un fichier en lecture partagée (comparaison de contenu) ; null si impossible. N'ouvre jamais un point d'analyse.</summary>
    Stream? OpenRead(string path);

    /// <summary>
    /// Envoie un fichier à la Corbeille (restaurable depuis la Corbeille). Si Windows ne peut pas le recycler (fichier trop
    /// volumineux, lecteur sans Corbeille), il demande confirmation avant toute suppression définitive ; un refus annule.
    /// </summary>
    OperationResult MoveToRecycleBin(string path);
}

/// <summary>Métadonnées de version et signature Authenticode.</summary>
public interface IFileMetadataProvider
{
    Models.Processes.FileVersionMetadata? GetVersionInfo(string path);
}

public interface ISignatureVerifier
{
    /// <summary>Vérifie la signature Authenticode (WinVerifyTrust) sans accès réseau (pas de vérification de révocation en ligne).</summary>
    Models.Processes.SignatureInfo Verify(string path);
}

public sealed record ShortcutTarget(string TargetPath, string? Arguments, string? WorkingDirectory);

public interface IShortcutResolver
{
    ShortcutTarget? Resolve(string shortcutPath);
}

public interface IRecycleBinProvider
{
    /// <summary>Taille et nombre d'éléments de la corbeille (tous lecteurs).</summary>
    OperationResult<(long Bytes, long Items)> Query();

    OperationResult Empty();
}
