using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;

namespace PCBoost.Infrastructure.Updates;

/// <summary>
/// Mise à jour automatique de PCBoost (choix de l'éditeur : installation automatique, désactivable).
/// <list type="number">
/// <item>Vérification 2 minutes après le démarrage puis toutes les 6 heures (manifeste signé, voir <see cref="UpdateService"/>).</item>
/// <item>Nouvelle version : téléchargement et vérification en arrière-plan — jamais sur une connexion limitée, et jamais
/// pour une copie portable (simple notification). Notification « prête, installée à la fermeture ».</item>
/// <item>Installation à la fermeture volontaire de PCBoost ; si PCBoost n'est jamais quitté (zone de notification), à
/// l'ouverture de session suivante, une minute après le démarrage discret, puis relance discrète. Windows demande
/// l'autorisation administrateur (installation pour tous les utilisateurs) ; après deux tentatives sans effet pour une
/// même version (autorisation refusée…), l'installation automatique s'arrête et seule l'action « Installer maintenant »
/// reste proposée.</item>
/// <item>Au démarrage suivant : notification « PCBoost a été mis à jour » et entrée du journal.</item>
/// </list>
/// Désactivation : réglage « Mises à jour automatiques » (aucune connexion automatique) ou stratégie de l'organisation
/// <c>HKLM\SOFTWARE\Policies\PCBoost\DisableAutoUpdate = 1</c>.
/// </summary>
public sealed class AutoUpdateService : IAutoUpdateService, IDisposable
{
    internal const string StateKey = "updates.auto";
    internal const int MaxAutomaticAttempts = 2;
    internal static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    internal static readonly TimeSpan SessionInstallDelay = TimeSpan.FromMinutes(1);

    /// <summary>Dossier d'installation écrit par le MSI (absent pour une copie portable).</summary>
    internal static readonly RegistryLocation InstallKey = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\PCBoost");
    internal const string InstallFolderValue = "InstallFolder";
    internal static readonly RegistryLocation PolicyKey = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\Policies\PCBoost");
    internal const string DisableAutoUpdateValue = "DisableAutoUpdate";

    private readonly IUpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly IKeyValueStore _store;
    private readonly INetworkCostProvider _network;
    private readonly INotificationService _notifications;
    private readonly IActivityJournal _journal;
    private readonly IRegistryProvider _registry;
    private readonly IElevationService _elevation;
    private readonly IAppInfo _appInfo;
    private readonly IClock _clock;
    private readonly ILogger<AutoUpdateService> _logger;
    private readonly string _downloadDirectory;
    private readonly string _baseDirectory;
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private readonly Lock _gate = new();

    private Timer? _checkTimer;
    private Timer? _sessionInstallTimer;
    private bool _started;
    private bool _disposed;
    private AutoUpdateState _state;
    private PersistedUpdateState _persisted = PersistedUpdateState.Empty;
    private string? _pendingPackage;
    private bool _installRequested;
    private UpdateRelaunch _requestedRelaunch;

    public AutoUpdateService(IUpdateService updates, ISettingsService settings, IKeyValueStore store, INetworkCostProvider network,
        INotificationService notifications, IActivityJournal journal, IRegistryProvider registry, IElevationService elevation, IAppInfo appInfo,
        IClock clock, UpdateOptions options, ILogger<AutoUpdateService>? logger = null)
        : this(updates, settings, store, network, notifications, journal, registry, elevation, appInfo, clock, options, logger, AppContext.BaseDirectory)
    {
    }

    internal AutoUpdateService(IUpdateService updates, ISettingsService settings, IKeyValueStore store, INetworkCostProvider network,
        INotificationService notifications, IActivityJournal journal, IRegistryProvider registry, IElevationService elevation, IAppInfo appInfo,
        IClock clock, UpdateOptions options, ILogger<AutoUpdateService>? logger, string baseDirectory)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        _appInfo = appInfo ?? throw new ArgumentNullException(nameof(appInfo));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? NullLogger<AutoUpdateService>.Instance;
        _downloadDirectory = options.ResolveDownloadDirectory(appInfo.DataDirectory);
        _baseDirectory = baseDirectory;
        _state = AutoUpdateState.Initial(IsInstalledCopy());
    }

    public AutoUpdateState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public event EventHandler<AutoUpdateState>? StateChanged;

    public event EventHandler<UpdateExitRequest>? ExitForInstallRequested;

    public bool IsDisabledByPolicy
        => _registry.GetValue(PolicyKey, DisableAutoUpdateValue) is { Type: RegistryValueType.DWord, Value: int value } && value == 1;

    public bool IsAutomatic => _settings.Current.AutoUpdate && !IsDisabledByPolicy;

    // ---- Démarrage et arrêt -----------------------------------------------------------------------------------------

    public async Task StartAsync(bool sessionStart, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
        }

        try
        {
            _persisted = await _store.GetAsync<PersistedUpdateState>(StateKey, cancellationToken).ConfigureAwait(false) ?? PersistedUpdateState.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "État des mises à jour illisible : repris à zéro.");
            _persisted = PersistedUpdateState.Empty;
        }

        await AnnounceCompletedUpdateAsync().ConfigureAwait(false);
        await RestorePendingAsync(cancellationToken).ConfigureAwait(false);
        Update(s => s with { LastCheckedAt = _persisted.LastCheckedAt });

        _settings.SettingsChanged += OnSettingsChanged;
        if (IsAutomatic) StartTimer();

        // PCBoost démarré à l'ouverture de session avec une mise à jour prête d'une session précédente (jamais quitté) :
        // installation discrète une minute plus tard, puis relance dans la zone de notification.
        if (sessionStart && IsAutomatic && !_elevation.IsElevated && State is { CanInstall: true, InstallBlocked: false })
        {
            lock (_gate)
            {
                if (!_disposed) _sessionInstallTimer = new Timer(_ => OnSessionInstallDue(), null, SessionInstallDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _checkTimer?.Dispose();
            _checkTimer = null;
            _sessionInstallTimer?.Dispose();
            _sessionInstallTimer = null;
        }
        _settings.SettingsChanged -= OnSettingsChanged;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        _work.Dispose();
        _persistLock.Dispose();
    }

    private void StartTimer()
    {
        lock (_gate)
        {
            if (_disposed || _checkTimer is not null) return;
            _checkTimer = new Timer(_ => _ = RunScheduledCheckAsync(), null, FirstCheckDelay, CheckInterval);
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (IsAutomatic)
        {
            StartTimer();
            return;
        }
        lock (_gate)
        {
            _checkTimer?.Dispose();
            _checkTimer = null;
            _sessionInstallTimer?.Dispose();
            _sessionInstallTimer = null;
        }
    }

    // ---- Vérification et téléchargement -----------------------------------------------------------------------------

    private async Task RunScheduledCheckAsync()
    {
        if (!IsAutomatic) return;
        try
        {
            await CheckCoreAsync(automatic: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Vérification automatique des mises à jour interrompue.");
        }
    }

    public Task CheckNowAsync(CancellationToken cancellationToken = default) => CheckCoreAsync(automatic: false, cancellationToken);

    internal async Task CheckCoreAsync(bool automatic, CancellationToken cancellationToken)
    {
        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false)) return;
        var previous = State;
        try
        {
            if (previous.Phase is not AutoUpdatePhase.ReadyToInstall) Update(s => s with { Phase = AutoUpdatePhase.Checking, Message = null });

            var result = await _updates.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock.UtcNow;
            await UpdatePersistedAsync(p => p with { LastCheckedAt = now }).ConfigureAwait(false);
            var metered = _network.IsMeteredConnection() == true;

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable when result.Update is { } update:
                    var canDownload = State.IsInstalledCopy && IsAutomatic && !metered && !_elevation.IsElevated;
                    if (previous.Phase == AutoUpdatePhase.ReadyToInstall && previous.Update is { } ready)
                    {
                        // Une mise à jour prête le reste tant qu'une version plus récente n'a pas été téléchargée et vérifiée.
                        Update(s => s with { LastCheckedAt = now, IsMetered = metered });
                        if (UpdateVersions.IsNewer(update.Version, ready.Version) && canDownload)
                            await DownloadCoreAsync(update, cancellationToken, fallback: previous with { LastCheckedAt = now, IsMetered = metered }).ConfigureAwait(false);
                        break;
                    }
                    Update(s => s with { Phase = AutoUpdatePhase.Available, Update = update, LastCheckedAt = now, Message = result.Message, IsMetered = metered });
                    if (canDownload)
                    {
                        await DownloadCoreAsync(update, cancellationToken).ConfigureAwait(false);
                    }
                    else if (automatic)
                    {
                        await NotifyAvailableOnceAsync(update).ConfigureAwait(false);
                    }
                    break;
                case UpdateCheckStatus.UpToDate:
                    if (previous.Phase != AutoUpdatePhase.ReadyToInstall)
                        Update(s => s with { Phase = AutoUpdatePhase.UpToDate, Update = null, LastCheckedAt = now, Message = result.Message, IsMetered = metered });
                    break;
                case UpdateCheckStatus.NotConfigured:
                    if (previous.Phase != AutoUpdatePhase.ReadyToInstall)
                        Update(s => s with { Phase = AutoUpdatePhase.NotConfigured, Update = null, LastCheckedAt = now, Message = result.Message });
                    break;
                default:
                    // Échec passager (hors ligne…) : une mise à jour déjà prête le reste.
                    if (previous.Phase != AutoUpdatePhase.ReadyToInstall)
                        Update(s => s with { Phase = AutoUpdatePhase.Failed, LastCheckedAt = now, Message = result.Message ?? TextRef.Of("Infra_Update_Offline") });
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Vérification interrompue (page quittée, fermeture) : l'état d'avant est rétabli, rien n'est perdu.
            RestoreState(previous);
            throw;
        }
        finally
        {
            _work.Release();
        }
    }

    private void RestoreState(AutoUpdateState previous)
        => Update(s => previous.Phase is AutoUpdatePhase.Checking or AutoUpdatePhase.Downloading
            ? previous with { Phase = previous.Update is null ? AutoUpdatePhase.Idle : AutoUpdatePhase.Available, DownloadPercent = null }
            : previous with { LastCheckedAt = s.LastCheckedAt });

    public async Task DownloadNowAsync(CancellationToken cancellationToken = default)
    {
        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false)) return;
        var previous = State;
        try
        {
            if (previous is { Phase: AutoUpdatePhase.Available or AutoUpdatePhase.Failed, Update: { } update, IsInstalledCopy: true })
            {
                // Instance administrateur : refus expliqué (l'installation ne pourrait de toute façon pas en partir).
                if (_elevation.IsElevated) Update(s => s with { Phase = AutoUpdatePhase.Failed, Message = TextRef.Of("Infra_AutoUpdate_Elevated") });
                else await DownloadCoreAsync(update, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            RestoreState(previous);
            throw;
        }
        finally
        {
            _work.Release();
        }
    }

    /// <param name="fallback">État rétabli en cas d'échec (mise à jour déjà prête, conservée) ; sinon « échec ».</param>
    private async Task DownloadCoreAsync(UpdateInfo update, CancellationToken cancellationToken, AutoUpdateState? fallback = null)
    {
        Update(s => s with { Phase = AutoUpdatePhase.Downloading, Update = update, DownloadPercent = 0, Message = null });
        var progress = new InlineProgress(p => Update(s => s.Phase == AutoUpdatePhase.Downloading ? s with { DownloadPercent = Math.Clamp(p, 0, 100) } : s));
        UpdateDownloadResult result;
        try
        {
            result = await _updates.DownloadAndVerifyAsync(update, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (fallback is not null)
        {
            Update(_ => fallback);
            throw;
        }
        if (!result.Outcome.Success || result.LocalPath is not { } path)
        {
            _logger.LogWarning("Mise à jour {Version} : téléchargement ou vérification en échec ({Error}).", UpdateVersions.Display(update.Version), result.Outcome.Error);
            if (fallback is not null) Update(_ => fallback);
            else Update(s => s with { Phase = AutoUpdatePhase.Failed, DownloadPercent = null, Message = result.Outcome.Message ?? TextRef.Of("Infra_Update_DownloadFailed") });
            return;
        }

        _pendingPackage = path;
        await UpdatePersistedAsync(p => p with { Pending = PendingUpdate.From(update, path) }).ConfigureAwait(false);
        Update(s => s with { Phase = AutoUpdatePhase.ReadyToInstall, DownloadPercent = null, Message = null, InstallBlocked = false });
        var version = UpdateVersions.Display(update.Version);
        await LogAsync(ActivityKind.Info, TextRef.Of("Infra_AutoUpdate_Journal_Ready", version)).ConfigureAwait(false);
        _notifications.Show(new NotificationRequest(
            TextRef.Of("Infra_AutoUpdate_Notify_Ready_Title", version),
            TextRef.Of("Infra_AutoUpdate_Notify_Ready_Body"),
            ActionId: InstallActionId,
            ActionLabel: TextRef.Of("Infra_AutoUpdate_Action_InstallNow"),
            Tag: "update-ready"));
    }

    /// <summary>Action de notification « Installer maintenant » (traitée par l'hôte).</summary>
    public const string InstallActionId = "update.install";

    private async Task NotifyAvailableOnceAsync(UpdateInfo update)
    {
        var version = UpdateVersions.Display(update.Version);
        if (string.Equals(_persisted.NotifiedVersion, version, StringComparison.Ordinal)) return;
        await UpdatePersistedAsync(p => p with { NotifiedVersion = version }).ConfigureAwait(false);
        var body = !State.IsInstalledCopy ? "Infra_AutoUpdate_Notify_Available_Portable"
            : State.IsMetered ? "Infra_AutoUpdate_Notify_Available_Metered"
            : "Infra_AutoUpdate_Notify_Available_Body";
        _notifications.Show(new NotificationRequest(
            TextRef.Of("Infra_AutoUpdate_Notify_Available_Title", version),
            TextRef.Of(body),
            ActionId: "navigate:settings",
            ActionLabel: TextRef.Of("Infra_AutoUpdate_Action_Open"),
            Tag: "update-available"));
    }

    // ---- Installation -----------------------------------------------------------------------------------------------

    public OperationResult RequestInstallNow()
    {
        var state = State;
        if (!state.IsInstalledCopy) return OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Infra_AutoUpdate_PortableCopy"));
        if (!state.CanInstall) return OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Infra_AutoUpdate_NothingToInstall"));
        if (_elevation.IsElevated) return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_AutoUpdate_Elevated"));
        lock (_gate)
        {
            _installRequested = true;
            _requestedRelaunch = UpdateRelaunch.Window;
        }
        ExitForInstallRequested?.Invoke(this, new UpdateExitRequest(Automatic: false));
        return OperationResult.Ok();
    }

    public void CancelInstallRequest()
    {
        lock (_gate)
        {
            _installRequested = false;
            _requestedRelaunch = UpdateRelaunch.None;
        }
    }

    internal void OnSessionInstallDue()
    {
        if (!IsAutomatic || _elevation.IsElevated || State is not { CanInstall: true, InstallBlocked: false }) return;
        lock (_gate)
        {
            _installRequested = true;
            _requestedRelaunch = UpdateRelaunch.Background;
        }
        var version = State.Update is { } update ? UpdateVersions.Display(update.Version) : string.Empty;
        _notifications.Show(new NotificationRequest(
            TextRef.Of("Infra_AutoUpdate_Notify_Installing_Title", version),
            TextRef.Of("Infra_AutoUpdate_Notify_Installing_Body"),
            Tag: "update-installing"));
        ExitForInstallRequested?.Invoke(this, new UpdateExitRequest(Automatic: true));
    }

    public async Task<IPreparedUpdateInstall?> PrepareInstallOnExitAsync(CancellationToken cancellationToken = default)
    {
        var state = State;
        bool requested;
        UpdateRelaunch relaunch;
        lock (_gate)
        {
            requested = _installRequested;
            relaunch = requested ? _requestedRelaunch : UpdateRelaunch.None;
        }
        if (!state.CanInstall || state.Update is not { } update || _pendingPackage is not { } package) return null;
        // PCBoost lancé en administrateur : msiexec hériterait de ce jeton sans demander d'autorisation, avec un paquet
        // situé dans un dossier modifiable par l'utilisateur → jamais d'installation depuis une instance élevée.
        if (_elevation.IsElevated)
        {
            _logger.LogInformation("Mise à jour non installée : PCBoost s'exécute en administrateur.");
            return null;
        }
        // Seul « Installer maintenant » (relance avec la fenêtre) passe outre les règles de l'installation automatique
        // (réglage, stratégie, tentatives restantes) ; une demande automatique (ouverture de session) les respecte.
        var explicitRequest = requested && relaunch == UpdateRelaunch.Window;
        if (!explicitRequest && (!IsAutomatic || state.InstallBlocked)) return null;

        var version = UpdateVersions.Display(update.Version);
        var saved = await UpdatePersistedAsync(p => p with
        {
            AttemptVersion = version,
            Attempts = string.Equals(p.AttemptVersion, version, StringComparison.Ordinal) ? p.Attempts + 1 : 1,
        }).ConfigureAwait(false);
        var attempts = saved.Attempts;

        var options = new UpdateInstallOptions(relaunch, InstalledFolder(), Path.Combine(_downloadDirectory, $"install-{version}.log"));
        var prepared = _updates.PrepareInstall(package, options);
        if (!prepared.Success || prepared.Value is null)
        {
            _logger.LogWarning("Installation de la mise à jour {Version} non préparée ({Error}).", version, prepared.Error);
            return null;
        }
        _logger.LogInformation("Installation de la mise à jour {Version} à la fermeture (tentative {Attempt}, relance : {Relaunch}).", version, attempts, relaunch);
        await LogAsync(ActivityKind.Info, TextRef.Of("Infra_AutoUpdate_Journal_Installing", version)).ConfigureAwait(false);
        return prepared.Value;
    }

    // ---- Démarrage : mise à jour terminée, paquet en attente ----------------------------------------------------------

    private async Task AnnounceCompletedUpdateAsync()
    {
        var current = _appInfo.Version;
        var display = UpdateVersions.Display(current);
        var previous = SignedUpdateManifest.TryParseVersion(_persisted.LastRunVersion, out var parsed) ? parsed : null;
        var updated = previous is not null && UpdateVersions.IsNewer(current, previous);
        if (!string.Equals(_persisted.LastRunVersion, display, StringComparison.Ordinal))
        {
            var installedNotes = _persisted.Pending is { } p && SameVersion(p.GetVersion(), current) ? p.Notes : _persisted.InstalledNotes;
            await UpdatePersistedAsync(p => p with
            {
                LastRunVersion = display,
                InstalledNotes = updated ? installedNotes : p.InstalledNotes,
                InstalledNotesVersion = updated ? display : p.InstalledNotesVersion,
            }).ConfigureAwait(false);
        }
        if (!updated) return;

        _logger.LogInformation("PCBoost mis à jour : {Previous} → {Current}.", UpdateVersions.Display(previous!), display);
        await LogAsync(ActivityKind.Info, TextRef.Of("Infra_AutoUpdate_Journal_Updated", UpdateVersions.Display(previous!), display)).ConfigureAwait(false);
        _notifications.Show(new NotificationRequest(
            TextRef.Of("Infra_AutoUpdate_Notify_Updated_Title", display),
            TextRef.Of("Infra_AutoUpdate_Notify_Updated_Body"),
            ActionId: "navigate:settings",
            ActionLabel: TextRef.Of("Infra_AutoUpdate_Action_WhatsNew"),
            Tag: "update-done"));
    }

    /// <summary>
    /// Paquet téléchargé lors d'une session précédente : revérifié (signature du manifeste puis empreinte) ; obsolète ou
    /// invalide, il est supprimé. Les paquets et journaux d'installation des versions déjà installées sont supprimés.
    /// </summary>
    private async Task RestorePendingAsync(CancellationToken cancellationToken)
    {
        var current = _appInfo.Version;
        var pending = _persisted.Pending;
        if (pending is not null && (pending.GetVersion() is not { } version || !UpdateVersions.IsNewer(version, current)))
        {
            await UpdatePersistedAsync(p => p with { Pending = null, AttemptVersion = null, Attempts = 0 }).ConfigureAwait(false);
            pending = null;
        }
        CleanDownloadDirectory(keep: pending?.PackagePath);
        if (pending?.ToUpdateInfo() is not { } update) return;

        var verified = await _updates.VerifyDownloadedAsync(update, pending.PackagePath, cancellationToken).ConfigureAwait(false);
        if (!verified.Outcome.Success || verified.LocalPath is not { } path)
        {
            _logger.LogWarning("Paquet de mise à jour en attente invalide ou absent : abandonné ({Error}).", verified.Outcome.Error);
            await UpdatePersistedAsync(p => p with { Pending = null, AttemptVersion = null, Attempts = 0 }).ConfigureAwait(false);
            return;
        }
        _pendingPackage = path;
        var blocked = string.Equals(_persisted.AttemptVersion, UpdateVersions.Display(update.Version), StringComparison.Ordinal)
                      && _persisted.Attempts >= MaxAutomaticAttempts;
        Update(s => s with
        {
            Phase = AutoUpdatePhase.ReadyToInstall,
            Update = update,
            InstallBlocked = blocked,
            Message = blocked ? TextRef.Of("Infra_AutoUpdate_InstallBlocked") : null,
        });
    }

    private void CleanDownloadDirectory(string? keep)
    {
        try
        {
            if (!Directory.Exists(_downloadDirectory)) return;
            foreach (var file in Directory.EnumerateFiles(_downloadDirectory))
            {
                var name = Path.GetFileName(file);
                // Téléchargements partiels récents conservés (reprise) ; journaux d'installation gardés 30 jours.
                var obsolete = name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                               || (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))
                               || (name.StartsWith("install-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                                   && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-30));
                if (!obsolete || (keep is not null && PathContainment.PathEquals(file, keep))) continue;
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Nettoyage du dossier des mises à jour incomplet.");
        }
    }

    // ---- Divers -------------------------------------------------------------------------------------------------------

    /// <summary>Copie installée par le MSI : dossier noté par l'installateur = dossier de l'application.</summary>
    internal bool IsInstalledCopy() => InstalledFolder() is not null;

    private string? InstalledFolder()
    {
        if (_registry.GetValue(InstallKey, InstallFolderValue) is not { Type: RegistryValueType.String, Value: string folder } || string.IsNullOrWhiteSpace(folder))
            return null;
        try
        {
            var installed = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetFullPath(_baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(installed, current, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? installed : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private async Task<bool> TryEnterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _work.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void Update(Func<AutoUpdateState, AutoUpdateState> change)
    {
        AutoUpdateState next;
        lock (_gate)
        {
            next = change(_state) with { IsInstalledCopy = _state.IsInstalledCopy };
            if (next == _state) return;
            _state = next;
        }
        StateChanged?.Invoke(this, next);
    }

    /// <summary>Modification atomique de l'état conservé (aucune mise à jour perdue entre minuterie, actions et fermeture).</summary>
    private async Task<PersistedUpdateState> UpdatePersistedAsync(Func<PersistedUpdateState, PersistedUpdateState> change)
    {
        await _persistLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = change(_persisted);
            _persisted = state;
            try
            {
                await _store.SetAsync(StateKey, state, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "État des mises à jour non enregistré.");
            }
            return state;
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private async Task LogAsync(ActivityKind kind, TextRef message)
    {
        try
        {
            await _journal.LogAsync(kind, message, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Journal d'activité indisponible.");
        }
    }

    /// <summary>Notes de la version installée par la dernière mise à jour (affichées dans Paramètres).</summary>
    public string? InstalledReleaseNotes
        => string.Equals(_persisted.InstalledNotesVersion, UpdateVersions.Display(_appInfo.Version), StringComparison.Ordinal) ? _persisted.InstalledNotes : null;

    private static bool SameVersion(Version? a, Version? b)
        => a is not null && b is not null && UpdateVersions.Normalize(a) == UpdateVersions.Normalize(b);

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

/// <summary>État conservé entre les sessions (base locale, clé <c>updates.auto</c>).</summary>
internal sealed record PersistedUpdateState(
    string? LastRunVersion,
    DateTimeOffset? LastCheckedAt,
    PendingUpdate? Pending,
    string? AttemptVersion,
    int Attempts,
    string? NotifiedVersion,
    string? InstalledNotesVersion,
    string? InstalledNotes)
{
    public static PersistedUpdateState Empty { get; } = new(null, null, null, null, 0, null, null, null);
}

/// <summary>Paquet téléchargé et vérifié, avec le manifeste signé qui le décrit (revérifié à chaque démarrage).</summary>
internal sealed record PendingUpdate(
    string Version,
    string PackagePath,
    string PackageLocation,
    string Sha256,
    long? Size,
    string? Notes,
    DateTimeOffset? PublishedAt,
    string? ReleaseUrl,
    string SignedManifest)
{
    public Version? GetVersion() => SignedUpdateManifest.TryParseVersion(Version, out var v) ? v : null;

    public static PendingUpdate From(UpdateInfo update, string packagePath)
        => new(UpdateVersions.Display(update.Version), packagePath, update.PackageLocation, update.Sha256, update.Size, update.ReleaseNotes,
            update.PublishedAt, update.ReleaseUrl, update.SignedManifest ?? string.Empty);

    public UpdateInfo? ToUpdateInfo()
        => GetVersion() is { } version && !string.IsNullOrEmpty(SignedManifest)
            ? new UpdateInfo(version, PackageLocation, Sha256, Notes, PublishedAt, Size, ReleaseUrl, SignedManifest)
            : null;
}
