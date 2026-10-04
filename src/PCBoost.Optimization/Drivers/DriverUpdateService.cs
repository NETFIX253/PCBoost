using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Drivers;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Drivers;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Drivers;

/// <summary>
/// Mises à jour de pilotes sécurisées.
/// <list type="bullet">
/// <item>Recherche : Windows Update uniquement (catalogue Microsoft, pilotes signés WHQL), configuration de Windows
/// respectée (stratégie excluant les pilotes, service désactivé, serveur WSUS), classement par <see cref="DriverUpdatePolicy"/>.</item>
/// <item>Installation : configuration relue et périphériques relus au moment de la demande ; chaque pilote inscrit AVANT
/// installation au journal de restauration avec l'état actuel de ses périphériques ; une autorisation administrateur ;
/// point de restauration Windows neuf obligatoire (créé par l'assistant après sa propre revérification).</item>
/// <item>Résultat inconnu (assistant sans réponse) : la modification reste « en attente » — jamais « échouée » — pour que le
/// retour au pilote précédent reste proposé (il ne touche que les périphériques encore sur la version installée).</item>
/// <item>Installation et point de restauration à la demande ne s'exécutent jamais en même temps.</item>
/// </list>
/// </summary>
public sealed class DriverUpdateService : IDriverUpdateService
{
    public const string OptimizationId = "drivers";

    private readonly IDriverUpdateSource _source;
    private readonly IDeviceDriverProvider _devices;
    private readonly IRegistryProvider _registry;
    private readonly IElevationService _elevation;
    private readonly IRollbackManager _rollback;
    private readonly IActivityJournal _journal;
    private readonly IClock _clock;
    private readonly ILogger<DriverUpdateService> _logger;

    /// <summary>Une seule opération administrateur à la fois (installation, point de restauration à la demande).</summary>
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public DriverUpdateService(IDriverUpdateSource source, IDeviceDriverProvider devices, IRegistryProvider registry, IElevationService elevation,
        IRollbackManager rollback, IActivityJournal journal, IClock clock, ILogger<DriverUpdateService>? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? NullLogger<DriverUpdateService>.Instance;
    }

    public DriverScanResult? Latest { get; private set; }

    public async Task<DriverScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var environment = DriverEnvironmentReader.Read(_registry);
        var protection = environment.Protection;
        var managedServer = environment.ManagedUpdateServer;
        var rebootPending = environment.RebootPending;

        DriverScanResult result;
        if (environment.DriversExcludedByPolicy)
        {
            result = new DriverScanResult(DriverScanState.ExcludedByPolicy, [], now, rebootPending, false, managedServer, protection, null);
        }
        else if (environment.UpdateServiceDisabled)
        {
            var (computer, sources) = await OfficialSourcesAsync(null, cancellationToken).ConfigureAwait(false);
            result = new DriverScanResult(DriverScanState.UpdateServiceDisabled, [], now, rebootPending, false, managedServer, protection, null, computer, sources);
        }
        else
        {
            var devicesTask = Task.Run(_devices.GetInstalledDrivers, cancellationToken);
            var search = await _source.SearchAsync(cancellationToken).ConfigureAwait(false);
            var devices = await devicesTask.ConfigureAwait(false);
            var installed = devices.Success && devices.Value is not null ? devices.Value : [];
            var (computer, sources) = await OfficialSourcesAsync(installed, cancellationToken).ConfigureAwait(false);
            if (!search.Outcome.Success)
            {
                var state = search.Outcome.Message?.Key switch
                {
                    "Drv_Error_ServiceDisabled" => DriverScanState.UpdateServiceDisabled,
                    "Drv_Error_Policy" => DriverScanState.ExcludedByPolicy,
                    _ => DriverScanState.SearchFailed,
                };
                result = new DriverScanResult(state, [], now, rebootPending || search.RebootRequired, search.InstallerBusy, managedServer, protection, search.Outcome,
                    computer, state == DriverScanState.ExcludedByPolicy ? [] : sources);
            }
            else
            {
                var candidates = search.Offers
                    .DistinctBy(o => o.UpdateId, StringComparer.OrdinalIgnoreCase)
                    .Select(o => DriverUpdatePolicy.Evaluate(o, installed, now))
                    .OrderBy(c => c.Tier)
                    .ThenBy(c => c.Device?.DeviceName ?? c.Offer.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                result = new DriverScanResult(DriverScanState.Ready, candidates, now, rebootPending || search.RebootRequired, search.InstallerBusy,
                    managedServer, protection, devices.Success ? null : devices.ToResult(), computer, sources);
                _logger.LogInformation("Pilotes : {Recommended} recommandée(s), {Review} à examiner, {Excluded} exclue(s)",
                    candidates.Count(c => c.Tier == DriverUpdateTier.Recommended), candidates.Count(c => c.Tier == DriverUpdateTier.Review),
                    candidates.Count(c => c.Tier == DriverUpdateTier.Excluded));
            }
        }

        Latest = result;
        return result;
    }

    public async Task<DriverInstallResult> InstallAsync(IReadOnlyList<DriverUpdateCandidate> updates, bool enableSystemProtection,
        IProgress<DriverInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var selected = updates.DistinctBy(u => u.Offer.UpdateId, StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Count == 0)
            return DriverInstallResult.NotStarted(OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Drv_Install_NothingSelected")));
        if (selected.Count > DriverElevatedData.MaxUpdates)
            return DriverInstallResult.NotStarted(OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Drv_Install_TooMany", DriverElevatedData.MaxUpdates)));
        // Une mise à jour exclue par la politique n'est jamais installée, même si elle est demandée.
        if (selected.Any(u => !u.CanInstall || !DriverIdentifiers.IsValidUpdateId(u.Offer.UpdateId)))
            return DriverInstallResult.NotStarted(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drv_Install_Excluded")));

        // Configuration de Windows relue au moment de la demande (elle a pu changer depuis la recherche).
        var environment = DriverEnvironmentReader.Read(_registry);
        if (environment.Protection == SystemProtectionState.DisabledByPolicy)
            return DriverInstallResult.NotStarted(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drv_Stop_ProtectionPolicy")), DriverInstallStop.RestorePointUnavailable);
        if (environment.InstallStop is var stop and not DriverInstallStop.None)
            return DriverInstallResult.NotStarted(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drv_Stop_" + stop)), stop);

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InstallCoreAsync(selected, enableSystemProtection, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<DriverInstallResult> InstallCoreAsync(IReadOnlyList<DriverUpdateCandidate> requested, bool enableSystemProtection,
        IProgress<DriverInstallProgress>? progress, CancellationToken cancellationToken)
    {
        // État « avant » relu maintenant (la recherche a pu être faite bien avant) et nouvelle évaluation par la politique.
        var devices = await Task.Run(_devices.GetInstalledDrivers, cancellationToken).ConfigureAwait(false);
        if (!devices.Success || devices.Value is null)
            return DriverInstallResult.NotStarted(OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drv_Stop_DeviceReadFailed")), DriverInstallStop.DeviceReadFailed);
        var now = _clock.UtcNow;
        var selected = requested.Select(c => DriverUpdatePolicy.Evaluate(c.Offer, devices.Value, now)).ToList();
        if (selected.Any(c => !c.CanInstall))
            return DriverInstallResult.NotStarted(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drv_Install_Changed")));
        // Deux mises à jour pour un même périphérique : l'état « avant » de la seconde serait faux → une à la fois.
        if (DriverUpdatePolicy.SharesDevice(selected))
            return DriverInstallResult.NotStarted(OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Drv_Install_SameDevice")));

        var recorder = await _rollback.BeginSessionAsync(SessionType.DriverUpdate, TextRef.Of("Drv_Session_Title", selected.Count), null, cancellationToken)
            .ConfigureAwait(false);
        var ids = selected.Select(c => c.Offer.UpdateId.ToLowerInvariant()).ToList();
        var batch = new WriteAheadBatch<DriverInstallOutcome>(ids);
        var applies = new List<Task>();
        var uncertainty = new List<CancellationTokenSource>();
        var elevationStarted = false;
        DriverInstallResult? result = null;
        try
        {
            try
            {
                // 1) Write-ahead : chaque pilote est inscrit au journal (état « avant ») avant toute installation. Un résultat
                // inconnu annule le jeton de SA modification : le journal la laisse alors « en attente » (état réel incertain).
                foreach (var candidate in selected)
                {
                    var id = candidate.Offer.UpdateId.ToLowerInvariant();
                    var uncertain = new CancellationTokenSource();
                    uncertainty.Add(uncertain);
                    var apply = recorder.ApplyAsync(PendingFor(candidate), async _ =>
                    {
                        var outcome = await batch.ArriveAsync(id, CancellationToken.None).ConfigureAwait(false);
                        if (outcome?.Status == DriverInstallStatus.Unknown)
                        {
                            await uncertain.CancelAsync().ConfigureAwait(false);
                            uncertain.Token.ThrowIfCancellationRequested();
                        }
                        return ToResult(outcome);
                    }, uncertain.Token);
                    batch.Observe(id, apply);
                    applies.Add(apply);
                }
                var ready = await batch.Ready.ConfigureAwait(false);

                // 2) Une seule autorisation : revérification, point de restauration neuf, puis installations (par l'Elevator).
                if (ready.Count == 0)
                {
                    result = DriverInstallResult.NotStarted(OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Opt_Error_JournalUnavailable")));
                }
                else if (cancellationToken.IsCancellationRequested)
                {
                    result = DriverInstallResult.NotStarted(OperationResult.Fail(OperationErrorKind.Cancelled, TextRef.Of("Drv_Install_Cancelled")));
                }
                else
                {
                    elevationStarted = true;
                    result = await RunElevatedInstallAsync(ready, enableSystemProtection, progress, recorder.SessionId).ConfigureAwait(false);
                }
            }
            finally
            {
                // Toujours terminé : aucune consignation ne reste en attente d'un résultat. Sans résultat constaté, une
                // modification reste « en attente » si l'assistant a pu être lancé, « en échec » sinon (rien n'a été fait).
                batch.Complete(result is not null
                    ? result.Drivers.DistinctBy(d => d.UpdateId, StringComparer.OrdinalIgnoreCase).ToDictionary(d => d.UpdateId, StringComparer.OrdinalIgnoreCase)
                    : ids.ToDictionary(id => id, id => elevationStarted ? Unknown(id) : NotRun(id), StringComparer.OrdinalIgnoreCase));
            }

            foreach (var apply in applies) await SettleAsync(apply).ConfigureAwait(false);
            foreach (var source in uncertainty) source.Dispose();
            if (result.ProtectionEnabled) await RecordProtectionEnabledAsync(recorder).ConfigureAwait(false);
        }
        finally
        {
            // La session est toujours terminée (sinon elle resterait « en cours » et ses modifications non annulables).
            await _rollback.CompleteSessionAsync(recorder.SessionId, 0, result?.RebootRequired ?? false, CancellationToken.None).ConfigureAwait(false);
        }
        await LogAsync(result).ConfigureAwait(false);
        return result with { SessionId = recorder.SessionId };
    }

    /// <summary>
    /// Demande <c>drivers.install</c> à l'assistant administrateur. L'installation n'est plus interrompue une fois lancée
    /// (l'état des pilotes doit rester connu) ; une exception inattendue donne un résultat inconnu, jamais « non installé ».
    /// </summary>
    private async Task<DriverInstallResult> RunElevatedInstallAsync(IReadOnlyList<string> ready, bool enableSystemProtection,
        IProgress<DriverInstallProgress>? progress, Guid sessionId)
    {
        // Relais synchrone (Progress<T> différerait les rapports sur le pool de threads) : l'appelant choisit son fil.
        var lines = progress is null ? null : new RelayProgress(line =>
        {
            if (DriverElevatedData.ParseProgress(line) is { } p) progress.Report(p);
        });
        ElevatedResponse response;
        try
        {
            response = await _elevation.RunWithProgressAsync(
                new ElevatedRequest(ElevatedDriverOperations.Install, DriverElevatedData.InstallParameters(ready, enableSystemProtection)), lines, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Installation de pilotes : assistant administrateur sans résultat");
            response = new ElevatedResponse(OperationResult.FromException(ex) with { Message = TextRef.Of("Sys_ElevationNoResult", -1) }, new Dictionary<string, string>());
        }
        return Interpret(response, ready, sessionId);
    }

    /// <summary>Fin d'une consignation ; « en attente » volontaire (résultat inconnu) ou refus déjà constatés par le lot.</summary>
    private async Task SettleAsync(Task apply)
    {
        try
        {
            await apply.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Résultat inconnu : modification laissée « en attente » dans le journal.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Consignation d'une mise à jour de pilote");
        }
    }

    private static DriverInstallOutcome Unknown(string id) => new(id, DriverInstallStatus.Unknown, 0, false, null, null);

    private static DriverInstallOutcome NotRun(string id) => new(id, DriverInstallStatus.NotRun, 0, false, null, null);

    /// <summary>
    /// Activation de la protection du système (autorisée par l'utilisateur) inscrite à l'Historique. Elle n'est pas annulée
    /// automatiquement : la désactiver supprimerait les points de restauration, dont celui créé avant l'installation.
    /// </summary>
    private async Task RecordProtectionEnabledAsync(IChangeRecorder recorder)
    {
        var change = new PendingChange(ChangeKinds.SystemProtection, OptimizationId, "SystemRestore",
            TextRef.Of("Drv_Change_ProtectionEnabled"), ChangeStateSerializer.Serialize(new SystemProtectionChangeState(SystemProtectionState.Disabled)), false);
        await recorder.RecordIrreversibleAsync(change, OperationResult.Ok(), 0, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Retour au pilote précédent (journal de restauration), jamais en même temps qu'une installation.</summary>
    public async Task<OperationResult> RollbackAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _rollback.UndoChangeAsync(changeId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<DriverRestorePointResult> CreateRestorePointAsync(bool enableSystemProtection, CancellationToken cancellationToken = default)
    {
        if (ReadProtectionState() == SystemProtectionState.DisabledByPolicy)
            return new DriverRestorePointResult(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drv_Stop_ProtectionPolicy")), RestorePointStatus.Disabled, null, false);

        ElevatedResponse response;
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            response = await _elevation.RunAsync(
                new ElevatedRequest(ElevatedDriverOperations.RestorePoint, DriverElevatedData.RestorePointParameters(enableSystemProtection)), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
        if (!response.Outcome.Success || response.Data.Count == 0)
        {
            var failure = response.Outcome.Success ? OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Opt_RestorePoint_Failed")) : response.Outcome;
            return new DriverRestorePointResult(failure, RestorePointStatus.Failed, null, false);
        }

        var (status, createdAt, enabled) = DriverElevatedData.DecodeRestorePoint(response.Data);
        if (enabled)
        {
            await _journal.TryLogAsync(ActivityKind.Optimization, TextRef.Of("Drv_Journal_ProtectionEnabled"), null, _logger).ConfigureAwait(false);
            await RecordProtectionSessionAsync().ConfigureAwait(false);
        }
        var outcome = status switch
        {
            RestorePointStatus.Created => OperationResult.Ok(TextRef.Of("Drv_RestorePoint_Created")),
            RestorePointStatus.Disabled => OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drv_Stop_ProtectionDisabled")),
            _ => OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Opt_RestorePoint_Failed")),
        };
        await _journal.TryLogAsync(status == RestorePointStatus.Created ? ActivityKind.Optimization : ActivityKind.Warning,
            TextRef.Of(status == RestorePointStatus.Created ? "Drv_Journal_RestorePointManual" : "Opt_RestorePoint_Failed"), null, _logger).ConfigureAwait(false);
        return new DriverRestorePointResult(outcome, status, createdAt, enabled);
    }

    /// <summary>Activation de la protection lors d'un point de restauration à la demande : inscrite à l'Historique elle aussi.</summary>
    private async Task RecordProtectionSessionAsync()
    {
        try
        {
            var recorder = await _rollback.BeginSessionAsync(SessionType.DriverUpdate, TextRef.Of("Drv_Session_RestorePoint"), null, CancellationToken.None)
                .ConfigureAwait(false);
            try
            {
                await RecordProtectionEnabledAsync(recorder).ConfigureAwait(false);
            }
            finally
            {
                await _rollback.CompleteSessionAsync(recorder.SessionId, 0, false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Le point de restauration est créé ; seule l'inscription à l'Historique a échoué (le journal d'activité la garde).
            _logger.LogError(ex, "Activation de la protection du système non inscrite à l'Historique");
        }
    }

    /// <summary>Fabricant du PC et sources officielles correspondantes (lecture seule, sans autorisation).</summary>
    private async Task<(ComputerIdentity? Computer, IReadOnlyList<OfficialDriverSource> Sources)> OfficialSourcesAsync(
        IReadOnlyList<InstalledDriver>? installed, CancellationToken cancellationToken)
    {
        var computer = await Task.Run(_devices.GetComputerIdentity, cancellationToken).ConfigureAwait(false);
        if (installed is null)
        {
            var devices = await Task.Run(_devices.GetInstalledDrivers, cancellationToken).ConfigureAwait(false);
            installed = devices.Success && devices.Value is not null ? devices.Value : [];
        }
        return (computer, OfficialDriverSources.For(computer, installed));
    }

    /// <summary>Résultat de l'assistant administrateur → résultat de l'installation (rien n'est supposé en l'absence de données).</summary>
    internal static DriverInstallResult Interpret(ElevatedResponse response, IReadOnlyList<string> ids, Guid sessionId)
    {
        if (response.Data.Count == 0)
        {
            // Aucun résultat : soit l'assistant n'a jamais démarré (UAC refusée, demande refusée) → rien n'est installé ;
            // soit il a démarré sans rendre de résultat (délai dépassé, arrêt inattendu) → état inconnu.
            var uncertain = IsUncertain(response.Outcome);
            var status = uncertain ? DriverInstallStatus.Unknown : DriverInstallStatus.NotRun;
            var none = ids.Select(id => new DriverInstallOutcome(id, status, 0, false, null, null)).ToList();
            var outcome = uncertain
                ? OperationResult.Fail(response.Outcome.Error is OperationErrorKind.None ? OperationErrorKind.Failed : response.Outcome.Error,
                    TextRef.Of("Drv_Install_Unknown"), response.Outcome.TechnicalDetail)
                : response.Outcome.Success ? OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Sys_ElevationFailed")) : response.Outcome;
            return new DriverInstallResult(outcome, DriverInstallStop.None, RestorePointStatus.Failed, null, false, none, false, sessionId);
        }

        var (stop, restorePoint, restorePointAt, protectionEnabled, drivers, reboot) = DriverElevatedData.DecodeInstall(response.Data);
        var byId = drivers.DistinctBy(d => d.UpdateId, StringComparer.OrdinalIgnoreCase).ToDictionary(d => d.UpdateId, StringComparer.OrdinalIgnoreCase);
        var outcomes = ids.Select(id => byId.TryGetValue(id, out var d) ? d : new DriverInstallOutcome(id, DriverInstallStatus.NotRun, 0, false, null, null)).ToList();
        var final = response.Outcome.Success
            ? outcomes.All(o => o.Status == DriverInstallStatus.Installed)
                ? OperationResult.Ok()
                : OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drv_Install_Partial"))
            : stop == DriverInstallStop.None ? response.Outcome : response.Outcome with { Message = TextRef.Of("Drv_Stop_" + stop) };
        return new DriverInstallResult(final, stop, restorePoint, restorePointAt, protectionEnabled, outcomes, reboot || outcomes.Any(o => o.RebootRequired), sessionId);
    }

    /// <summary>
    /// L'assistant a démarré mais n'a rendu aucun résultat : délai dépassé, attente interrompue, arrêt sans fichier de
    /// résultat, ou exception inattendue. Un refus UAC ou une demande refusée ne l'est pas (rien n'a été lancé).
    /// </summary>
    internal static bool IsUncertain(OperationResult outcome)
        => !outcome.Success
           && (outcome.Error is OperationErrorKind.Timeout or OperationErrorKind.Cancelled
               || string.Equals(outcome.Message?.Key, "Sys_ElevationNoResult", StringComparison.Ordinal));

    internal static OperationResult ToResult(DriverInstallOutcome? outcome) => outcome?.Status switch
    {
        DriverInstallStatus.Installed => OperationResult.Ok(),
        DriverInstallStatus.Failed => OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drv_Outcome_Failed"),
            string.Create(CultureInfo.InvariantCulture, $"0x{outcome.HResult:X8}")),
        DriverInstallStatus.NoLongerOffered => OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Drv_Outcome_NoLongerOffered")),
        DriverInstallStatus.Refused => OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drv_Outcome_Refused")),
        DriverInstallStatus.Unknown => OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drv_Outcome_Unknown")),
        _ => OperationResult.Fail(OperationErrorKind.Cancelled, TextRef.Of("Drv_Outcome_NotRun")),
    };

    /// <summary>
    /// Modification inscrite au journal : version installée et, pour chaque périphérique concerné, le pilote en place
    /// (version, date, éditeur, INF). Retour ciblé seulement si <see cref="DriverUpdatePolicy.SupportsTargetedRollback"/> ;
    /// sinon (extension, composant logiciel, version inconnue, aucun pilote précédent), le point de restauration Windows
    /// créé avant l'installation reste le moyen de revenir en arrière — la description le dit.
    /// </summary>
    internal static PendingChange PendingFor(DriverUpdateCandidate candidate)
    {
        var device = candidate.Device;
        var name = device?.DeviceName ?? candidate.Offer.Model ?? candidate.Offer.Title;
        var state = new DriverUpdateState(
            candidate.Offer.UpdateId.ToLowerInvariant(),
            candidate.Offer.Title,
            name,
            candidate.Offer.DriverClass,
            DriverIdentifiers.IsValidVersion(candidate.Offer.Version) ? candidate.Offer.Version : null,
            candidate.Devices
                .Where(d => DriverIdentifiers.IsValidInstanceId(d.InstanceId))
                .Select(d => new DriverDeviceState(d.InstanceId, d.HasNoDriver ? null : d.Version, d.Date, d.Provider, d.InfName))
                .ToList());
        var reversible = DriverUpdatePolicy.SupportsTargetedRollback(candidate) && DriverElevatedData.RollbackParameters(state) is not null;
        var previous = device is { HasNoDriver: false } ? device.Version ?? "—" : "—";
        return new PendingChange(
            ChangeKinds.DriverUpdate,
            OptimizationId,
            "driver:" + name,
            TextRef.Of(reversible ? "Drv_Change_Description" : "Drv_Change_DescriptionRestorePoint", name, previous, state.NewVersion ?? "—"),
            ChangeStateSerializer.Serialize(state),
            reversible);
    }

    private async Task LogAsync(DriverInstallResult result)
    {
        if (result.ProtectionEnabled)
            await _journal.TryLogAsync(ActivityKind.Optimization, TextRef.Of("Drv_Journal_ProtectionEnabled"), null, _logger).ConfigureAwait(false);
        if (result.RestorePoint == RestorePointStatus.Created)
            await _journal.TryLogAsync(ActivityKind.Optimization, TextRef.Of("Drv_Journal_RestorePoint"), null, _logger).ConfigureAwait(false);
        if (result.Drivers.Any(d => d.Status == DriverInstallStatus.Unknown))
        {
            await _journal.TryLogAsync(ActivityKind.Warning, TextRef.Of("Drv_Journal_Unknown"), result.Outcome.TechnicalDetail, _logger).ConfigureAwait(false);
        }
        else if (result.Drivers.Count > 0)
        {
            await _journal.TryLogAsync(result.Outcome.Success ? ActivityKind.Optimization : ActivityKind.Warning,
                TextRef.Of("Drv_Journal_Installed", result.InstalledCount, result.Drivers.Count),
                $"stop={result.Stop} reboot={result.RebootRequired}", _logger).ConfigureAwait(false);
        }
    }

    internal SystemProtectionState ReadProtectionState() => DriverEnvironmentReader.ReadProtection(_registry);

    private sealed class RelayProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
