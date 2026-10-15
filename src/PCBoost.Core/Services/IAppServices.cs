using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Settings;

namespace PCBoost.Core.Services;

public interface ISettingsService
{
    AppSettings Current { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    event EventHandler<AppSettings>? SettingsChanged;
}

public sealed record NotificationRequest(TextRef Title, TextRef Body, string? ActionId = null, TextRef? ActionLabel = null, string? Tag = null);

/// <summary>Notifications Windows (§28). Respecte le réglage utilisateur.</summary>
public interface INotificationService
{
    void Show(NotificationRequest request);

    event EventHandler<string>? ActionInvoked;
}

public interface IActivityJournal
{
    Task LogAsync(ActivityKind kind, TextRef message, string? detail = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivityLogEntry>> GetRecentAsync(int limit = 200, CancellationToken cancellationToken = default);

    event EventHandler<ActivityLogEntry>? EntryAdded;
}

public interface IAppInfo
{
    string ProductName { get; }

    Version Version { get; }

    string DotNetVersion { get; }

    string WindowsAppSdkVersion { get; }

    string DataDirectory { get; }

    string LogDirectory { get; }
}

public interface IUpdateProvider
{
    string Name { get; }

    Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default);

    Task<UpdateDownloadResult> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Mises à jour de PCBoost (§51) : manifeste signé (clés publiques intégrées à l'application) puis empreinte SHA-256 du
/// paquet, vérifiés avant toute installation.
/// </summary>
public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    Task<UpdateDownloadResult> DownloadAndVerifyAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Paquet déjà téléchargé (session précédente) : signature du manifeste puis empreinte vérifiées de nouveau ; en cas
    /// de réussite, le paquet peut être installé par cette instance.
    /// </summary>
    Task<UpdateDownloadResult> VerifyDownloadedAsync(UpdateInfo update, string packagePath, CancellationToken cancellationToken = default);

    /// <summary>Installation interactive (assistant Windows Installer) d'un paquet vérifié par cette instance.</summary>
    OperationResult LaunchInstaller(string verifiedPackagePath);

    /// <summary>
    /// Prépare l'installation sans question d'un paquet vérifié par cette instance : empreinte recontrôlée, paquet
    /// verrouillé jusqu'au lancement (<see cref="IPreparedUpdateInstall.Start"/>).
    /// </summary>
    OperationResult<IPreparedUpdateInstall> PrepareInstall(string verifiedPackagePath, UpdateInstallOptions options);
}

/// <summary>
/// Mise à jour automatique de PCBoost : vérification au démarrage puis périodique, téléchargement et vérification en
/// arrière-plan (jamais sur une connexion limitée), installation à la fermeture de PCBoost — ou à l'ouverture de session
/// suivante si PCBoost n'a pas été quitté —, notification avant et après. Désactivable dans les Paramètres (ou par la
/// stratégie de l'organisation).
/// </summary>
public interface IAutoUpdateService
{
    AutoUpdateState State { get; }

    event EventHandler<AutoUpdateState>? StateChanged;

    /// <summary>
    /// Demande de fermeture de PCBoost pour installer la mise à jour prête (« Installer maintenant », ou installation à
    /// l'ouverture de session) : l'hôte ferme proprement l'application, qui lance alors l'installation.
    /// </summary>
    event EventHandler<UpdateExitRequest>? ExitForInstallRequested;

    /// <summary>L'hôte reporte la fermeture demandée (session Gaming, fenêtre déjà utilisée) : la demande est oubliée.</summary>
    void CancelInstallRequest();

    /// <summary>Mises à jour automatiques actives (réglage de l'utilisateur, sauf stratégie de l'organisation).</summary>
    bool IsAutomatic { get; }

    /// <summary>Une stratégie de l'organisation désactive les mises à jour automatiques.</summary>
    bool IsDisabledByPolicy { get; }

    /// <summary>Nouveautés de la version actuelle si elle a été installée par une mise à jour automatique, sinon null.</summary>
    string? InstalledReleaseNotes { get; }

    /// <param name="sessionStart">PCBoost démarré à l'ouverture de session (« --background »).</param>
    Task StartAsync(bool sessionStart, CancellationToken cancellationToken = default);

    void Stop();

    /// <summary>Vérification demandée par l'utilisateur (télécharge ensuite si les mises à jour automatiques sont actives).</summary>
    Task CheckNowAsync(CancellationToken cancellationToken = default);

    /// <summary>Téléchargement et vérification demandés par l'utilisateur (mise à jour disponible non téléchargée).</summary>
    Task DownloadNowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// « Installer maintenant » : demande la fermeture de PCBoost, l'installation se lance à la fermeture. Refusé si PCBoost
    /// s'exécute en administrateur (l'installation se ferait sans autorisation, avec un paquet modifiable par l'utilisateur).
    /// </summary>
    OperationResult RequestInstallNow();

    /// <summary>
    /// Appelé par l'hôte juste avant la fermeture volontaire de PCBoost : prépare l'installation de la mise à jour prête si
    /// elle est demandée (« Installer maintenant ») ou automatique. Null si rien n'est à installer.
    /// </summary>
    Task<IPreparedUpdateInstall?> PrepareInstallOnExitAsync(CancellationToken cancellationToken = default);
}
