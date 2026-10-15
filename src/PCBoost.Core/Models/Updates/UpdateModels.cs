using PCBoost.Core.Common;

namespace PCBoost.Core.Models.Updates;

public enum UpdateCheckStatus { UpToDate = 0, UpdateAvailable, NotConfigured, Failed }

/// <summary>
/// Mise à jour proposée, issue d'un manifeste dont la signature a été vérifiée. <see cref="SignedManifest"/> conserve le
/// manifeste signé d'origine : il est vérifié de nouveau (signature puis empreinte) avant chaque installation.
/// </summary>
public sealed record UpdateInfo(
    Version Version,
    string PackageLocation,
    string Sha256,
    string? ReleaseNotes,
    DateTimeOffset? PublishedAt,
    long? Size = null,
    string? ReleaseUrl = null,
    string? SignedManifest = null);

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update, TextRef? Message);

public sealed record UpdateDownloadResult(OperationResult Outcome, string? LocalPath);

/// <summary>Relance de PCBoost par le programme d'installation, une fois la mise à jour installée.</summary>
public enum UpdateRelaunch
{
    /// <summary>Pas de relance (l'utilisateur a quitté PCBoost).</summary>
    None = 0,
    /// <summary>Relance avec la fenêtre (« Installer maintenant »).</summary>
    Window,
    /// <summary>Relance discrète dans la zone de notification (installation à l'ouverture de session).</summary>
    Background,
}

/// <summary>
/// Installation sans question (interface réduite de Windows Installer) : dossier d'installation actuel conservé, journal
/// d'installation, relance éventuelle. Windows demande l'autorisation administrateur (installation pour tous les
/// utilisateurs) ; en cas d'échec ou de refus, la version installée reste intacte.
/// </summary>
public sealed record UpdateInstallOptions(UpdateRelaunch Relaunch, string? InstallFolder, string? LogPath);

/// <summary>
/// Installation préparée : paquet revérifié et verrouillé en lecture (ni modifiable ni supprimable) jusqu'au lancement.
/// <see cref="Start"/> lance le programme d'installation (une seule fois) ; <see cref="IDisposable.Dispose"/> libère le paquet.
/// </summary>
public interface IPreparedUpdateInstall : IDisposable
{
    Version Version { get; }

    OperationResult Start();
}

/// <summary>
/// Demande de fermeture de PCBoost pour installer la mise à jour prête. <see cref="Automatic"/> : installation à l'ouverture
/// de session (l'hôte la reporte si l'utilisateur utilise déjà PCBoost) ; sinon « Installer maintenant ».
/// </summary>
public sealed record UpdateExitRequest(bool Automatic);

/// <summary>Étape de la mise à jour automatique de PCBoost.</summary>
public enum AutoUpdatePhase
{
    /// <summary>Aucune vérification effectuée depuis le démarrage.</summary>
    Idle = 0,
    Checking,
    UpToDate,
    /// <summary>Nouvelle version trouvée, pas encore téléchargée (mises à jour automatiques désactivées, connexion limitée).</summary>
    Available,
    Downloading,
    /// <summary>Paquet téléchargé et vérifié : installé à la fermeture de PCBoost (ou tout de suite à la demande).</summary>
    ReadyToInstall,
    /// <summary>La source n'a pas pu être consultée ou le paquet n'a pas pu être vérifié.</summary>
    Failed,
    /// <summary>Aucune source de mise à jour configurée pour cette copie.</summary>
    NotConfigured,
}

/// <summary>État courant de la mise à jour automatique (affiché dans Paramètres).</summary>
public sealed record AutoUpdateState(
    AutoUpdatePhase Phase,
    UpdateInfo? Update,
    DateTimeOffset? LastCheckedAt,
    TextRef? Message,
    double? DownloadPercent,
    bool IsInstalledCopy,
    bool IsMetered,
    bool InstallBlocked)
{
    public static AutoUpdateState Initial(bool installedCopy) => new(AutoUpdatePhase.Idle, null, null, null, null, installedCopy, false, false);

    /// <summary>La mise à jour peut être installée depuis PCBoost (copie installée par le MSI, paquet prêt).</summary>
    public bool CanInstall => Phase == AutoUpdatePhase.ReadyToInstall && IsInstalledCopy;
}
