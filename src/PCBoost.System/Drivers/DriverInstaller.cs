using Microsoft.Extensions.Logging;
using PCBoost.Core.Drivers;
using PCBoost.Core.Models.Drivers;
using PCBoost.Core.Models.Health;
using PCBoost.Platform.Health;

namespace PCBoost.Platform.Drivers;

/// <summary>Résultat complet d'une installation exécutée par l'assistant administrateur.</summary>
internal sealed record DriverInstallReport(
    DriverInstallStop Stop,
    RestorePointStatus RestorePoint,
    DateTimeOffset? RestorePointAt,
    bool ProtectionEnabled,
    IReadOnlyList<DriverInstallOutcome> Drivers,
    bool RebootRequired);

/// <summary>
/// Installation des mises à jour de pilotes par l'assistant administrateur, dans cet ordre strict :
/// <list type="number">
/// <item>configuration de Windows relue : stratégie excluant les pilotes, service Windows Update désactivé, redémarrage en
/// attente, restauration du système interdite → rien n'est fait ; aucune installation Windows Update en cours ;</item>
/// <item>revérification : état actuel des périphériques (obligatoire — illisible, rien n'est fait), nouvelle recherche
/// Windows Update et nouvelle évaluation de chaque mise à jour demandée par <see cref="DriverUpdatePolicy"/> (jamais de
/// microprogramme, de version plus ancienne ni de périphérique introuvable) ;</item>
/// <item>s'il reste une mise à jour à installer : point de restauration Windows neuf — obligatoire : sans lui, rien n'est
/// installé (la protection du système n'est activée que si l'utilisateur l'a autorisé) ;</item>
/// <item>téléchargement et installation une par une, avec vérification des périphériques après chacune ; si un
/// périphérique signale un nouveau problème (hors « redémarrage nécessaire ») ou ne peut pas être vérifié, les
/// installations suivantes ne sont pas faites.</item>
/// </list>
/// Les conditions de licence ne sont jamais acceptées à la place de l'utilisateur.
/// </summary>
internal sealed class DriverInstaller
{
    private readonly ILogger _logger;
    private readonly Action<string> _log;
    private readonly Action<DriverInstallProgress> _progress;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<DriverEnvironment> _environment;
    private readonly Func<IReadOnlyList<InstalledDriver>?> _readDevices;

    public DriverInstaller(ILogger logger, Action<string> log, Action<DriverInstallProgress> progress, Func<DateTimeOffset>? now = null,
        Func<DriverEnvironment>? environment = null, Func<IReadOnlyList<InstalledDriver>?>? readDevices = null)
    {
        _logger = logger;
        _log = log;
        _progress = progress;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _environment = environment ?? (() => DriverEnvironmentReader.Read(new RegistryProvider()));
        _readDevices = readDevices ?? ReadDevices;
    }

    public DriverInstallReport Install(IReadOnlyList<string> updateIds, bool enableProtection)
    {
        var total = updateIds.Count;

        // 0) Configuration de Windows : stratégies de l'organisation, service, redémarrage, restauration interdite.
        var configurationStop = _environment().InstallStop;
        if (configurationStop != DriverInstallStop.None)
        {
            _log($"Installation refusée par la configuration de Windows : {configurationStop}");
            return Stopped(configurationStop, updateIds);
        }

        using var tracker = new ComTracker();
        object session;
        try
        {
            session = WindowsUpdateAgent.CreateSession(tracker);
            if (WindowsUpdateAgent.IsRebootRequired(tracker)) return Stopped(DriverInstallStop.RebootPending, updateIds);
            if (WindowsUpdateAgent.IsInstallerBusy(session, tracker)) return Stopped(DriverInstallStop.InstallerBusy, updateIds);
        }
        catch (Exception ex) when (WindowsUpdateAgent.IsComFailure(ex))
        {
            _log($"Agent Windows Update indisponible ({WindowsUpdateAgent.Classify(ex).TechnicalDetail})");
            return Stopped(DriverInstallStop.SearchFailed, updateIds);
        }

        // 1) Revérification : périphériques actuels (obligatoire), nouvelle recherche, politique.
        _progress(new DriverInstallProgress(DriverInstallStage.Searching, 0, total));
        if (_readDevices() is not { } installed)
        {
            _log("Liste des périphériques illisible : aucune installation");
            return Stopped(DriverInstallStop.DeviceReadFailed, updateIds);
        }
        var offered = new Dictionary<string, (object Update, DriverUpdateCandidate Candidate)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (code, updates) = WindowsUpdateAgent.SearchDrivers(session, tracker);
            if (code is not (WindowsUpdateAgent.ResultSucceeded or WindowsUpdateAgent.ResultSucceededWithErrors))
            {
                _log($"Recherche Windows Update : code {code}");
                return Stopped(DriverInstallStop.SearchFailed, updateIds);
            }
            foreach (var update in updates)
            {
                if (WindowsUpdateAgent.ReadOffer(update, tracker) is not { } offer || !updateIds.Contains(offer.UpdateId, StringComparer.OrdinalIgnoreCase)) continue;
                offered[offer.UpdateId] = (update, DriverUpdatePolicy.Evaluate(offer, installed, _now()));
            }
        }
        catch (Exception ex) when (WindowsUpdateAgent.IsComFailure(ex))
        {
            _log($"Recherche Windows Update impossible ({WindowsUpdateAgent.Classify(ex).TechnicalDetail})");
            return Stopped(DriverInstallStop.SearchFailed, updateIds);
        }

        // Une mise à jour visant un périphérique déjà concerné par une précédente de la liste est refusée (une à la fois :
        // son état « avant » dépendrait du résultat de la première).
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sharedDevice = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in updateIds)
        {
            if (!offered.TryGetValue(id, out var entry) || !entry.Candidate.CanInstall) continue;
            var instances = entry.Candidate.Devices.Select(d => d.InstanceId).ToList();
            if (instances.Any(claimed.Contains)) sharedDevice.Add(id);
            else claimed.UnionWith(instances);
        }
        bool Installable(string id) => offered.TryGetValue(id, out var e) && e.Candidate.CanInstall && !sharedDevice.Contains(id);

        // Rien d'installable après revérification : ni point de restauration, ni modification de la protection du système.
        if (!updateIds.Any(Installable))
        {
            _log("Aucune mise à jour installable après revérification");
            return new DriverInstallReport(DriverInstallStop.None, RestorePointStatus.Failed, null, false,
                updateIds.Select((id, i) => Unavailable(id, i, offered, sharedDevice)).ToList(), false);
        }

        // 2) Point de restauration neuf : condition absolue.
        _progress(new DriverInstallProgress(DriverInstallStage.RestorePoint, 0, total));
        var protectionEnabled = false;
        var (status, createdAt) = ElevatedHealthReaders.CreateDriverRestorePoint(_now(), _logger);
        if (status == RestorePointStatus.Disabled && enableProtection)
        {
            protectionEnabled = ElevatedHealthReaders.EnableSystemProtection(_logger);
            _log($"Protection du système : {(protectionEnabled ? "activée" : "non activée")}");
            if (protectionEnabled) (status, createdAt) = ElevatedHealthReaders.CreateDriverRestorePoint(_now(), _logger);
        }
        _log($"Point de restauration avant pilotes : {status}");
        if (status != RestorePointStatus.Created)
        {
            var stop = status == RestorePointStatus.Disabled && !enableProtection ? DriverInstallStop.ProtectionDisabled : DriverInstallStop.RestorePointUnavailable;
            return new DriverInstallReport(stop, status, createdAt, protectionEnabled, NotRun(updateIds), false);
        }

        // 3) Installation une par une.
        var outcomes = new List<DriverInstallOutcome>();
        var stopReason = DriverInstallStop.None;
        var rebootRequired = false;
        for (var i = 0; i < updateIds.Count; i++)
        {
            var id = updateIds[i];
            if (stopReason != DriverInstallStop.None)
            {
                outcomes.Add(new DriverInstallOutcome(id, DriverInstallStatus.NotRun, 0, false, null, null));
                continue;
            }
            if (!Installable(id))
            {
                outcomes.Add(Unavailable(id, i, offered, sharedDevice));
                continue;
            }
            var entry = offered[id];

            var (outcome, verified) = InstallOne(session, entry.Update, entry.Candidate, i, total, tracker);
            outcomes.Add(outcome);
            rebootRequired |= outcome.RebootRequired;
            if (outcome.Status != DriverInstallStatus.Installed) continue;
            if (!verified)
            {
                _log($"Mise à jour {i + 1}/{total} : vérification impossible ; arrêt des installations suivantes");
                stopReason = DriverInstallStop.DeviceReadFailed;
            }
            else if (outcome.ProblemCodeAfter is { } problem)
            {
                _log($"Mise à jour {i + 1}/{total} : un périphérique signale le code {problem} ; arrêt des installations suivantes");
                stopReason = DriverInstallStop.DeviceProblem;
            }
        }

        return new DriverInstallReport(stopReason, status, createdAt, protectionEnabled, outcomes, rebootRequired);
    }

    /// <summary>Mise à jour non installée après revérification : plus proposée, refusée par la politique, ou même périphérique.</summary>
    private DriverInstallOutcome Unavailable(string id, int index, Dictionary<string, (object Update, DriverUpdateCandidate Candidate)> offered,
        HashSet<string> sharedDevice)
    {
        if (!offered.TryGetValue(id, out var entry))
        {
            _log($"Mise à jour {index + 1} : plus proposée par Windows Update");
            return new DriverInstallOutcome(id, DriverInstallStatus.NoLongerOffered, 0, false, null, null);
        }
        _log(sharedDevice.Contains(id)
            ? $"Mise à jour {index + 1} : refusée (même périphérique qu'une mise à jour précédente de la liste)"
            : $"Mise à jour {index + 1} : refusée ({string.Join(", ", entry.Candidate.Reasons)})");
        return new DriverInstallOutcome(id, DriverInstallStatus.Refused, 0, false, null, null);
    }

    /// <summary>Installe une mise à jour ; <c>Verified</c> = périphériques relus après l'installation (sans objet en cas d'échec).</summary>
    private (DriverInstallOutcome Outcome, bool Verified) InstallOne(object sessionObject, object updateObject, DriverUpdateCandidate candidate, int index, int total, ComTracker tracker)
    {
        dynamic session = sessionObject;
        dynamic update = updateObject;
        var id = candidate.Offer.UpdateId;
        try
        {
            dynamic collection = tracker.Track(WindowsUpdateAgent.CreateObject("Microsoft.Update.UpdateColl"));
            collection.Add(update);

            _progress(new DriverInstallProgress(DriverInstallStage.Downloading, index + 1, total));
            dynamic downloader = tracker.Track(session.CreateUpdateDownloader());
            downloader.Updates = collection;
            dynamic download = tracker.Track(downloader.Download());
            int downloadCode = download.ResultCode;
            bool downloaded = WindowsUpdateAgent.Read(() => (bool)update.IsDownloaded) ?? false;
            if (downloadCode is not (WindowsUpdateAgent.ResultSucceeded or WindowsUpdateAgent.ResultSucceededWithErrors) || !downloaded)
            {
                int hr = WindowsUpdateAgent.Read(() => (int)download.HResult) ?? 0;
                _log($"Mise à jour {index + 1}/{total} : téléchargement en échec (code {downloadCode}, 0x{hr:X8})");
                return (new DriverInstallOutcome(id, DriverInstallStatus.Failed, hr, false, null, null), true);
            }

            _progress(new DriverInstallProgress(DriverInstallStage.Installing, index + 1, total));
            dynamic installer = tracker.Track(session.CreateUpdateInstaller());
            installer.Updates = collection;
            installer.AllowSourcePrompts = false;
            try
            {
                installer.ForceQuiet = true;
            }
            catch (Exception ex) when (WindowsUpdateAgent.IsComFailure(ex))
            {
                // IUpdateInstaller2 absent : l'installation reste silencieuse pour un pilote sans interaction.
            }
            dynamic result = tracker.Track(installer.Install());
            dynamic updateResult = tracker.Track(result.GetUpdateResult(0));
            int code = updateResult.ResultCode;
            int hresult = WindowsUpdateAgent.Read(() => (int)updateResult.HResult) ?? 0;
            bool reboot = WindowsUpdateAgent.Read(() => (bool)updateResult.RebootRequired) ?? false;
            var success = code is WindowsUpdateAgent.ResultSucceeded or WindowsUpdateAgent.ResultSucceededWithErrors;
            _log($"Mise à jour {index + 1}/{total} : installation {(success ? "réussie" : "en échec")} (code {code}, 0x{hresult:X8}, redémarrage {(reboot ? "requis" : "non")})");
            if (!success) return (new DriverInstallOutcome(id, DriverInstallStatus.Failed, hresult, reboot, null, null), true);

            _progress(new DriverInstallProgress(DriverInstallStage.Verifying, index + 1, total));
            var (version, problem, verified) = Verify(candidate);
            return (new DriverInstallOutcome(id, DriverInstallStatus.Installed, hresult, reboot, version, problem), verified);
        }
        catch (Exception ex) when (WindowsUpdateAgent.IsComFailure(ex))
        {
            var failure = WindowsUpdateAgent.Classify(ex);
            _log($"Mise à jour {index + 1}/{total} : erreur ({failure.TechnicalDetail})");
            return (new DriverInstallOutcome(id, DriverInstallStatus.Failed, ex.HResult, false, null, null), true);
        }
    }

    /// <summary>
    /// Après l'installation : version du périphérique principal et premier nouveau problème signalé par l'un des
    /// périphériques concernés (<see cref="DriverUpdatePolicy.NewProblemCode"/> : le code 14 « redémarrage nécessaire »
    /// n'en est pas un). <c>Readable</c> = false si la liste des périphériques n'a pas pu être relue.
    /// </summary>
    private (string? Version, int? Problem, bool Readable) Verify(DriverUpdateCandidate candidate)
    {
        if (_readDevices() is not { } devices) return (null, null, false);
        string? version = null;
        int? problem = null;
        foreach (var before in candidate.Devices)
        {
            var current = devices.FirstOrDefault(d => string.Equals(d.InstanceId, before.InstanceId, StringComparison.OrdinalIgnoreCase));
            if (current is null) continue;
            if (ReferenceEquals(before, candidate.Device)) version = current.Version;
            problem ??= DriverUpdatePolicy.NewProblemCode(before.ProblemCode, current.ProblemCode);
        }
        return (version, problem, true);
    }

    private static IReadOnlyList<InstalledDriver>? ReadDevices()
    {
        var result = new DeviceDriverProvider().GetInstalledDrivers();
        return result.Success ? result.Value : null;
    }

    private static DriverInstallReport Stopped(DriverInstallStop stop, IReadOnlyList<string> updateIds)
        => new(stop, RestorePointStatus.Failed, null, false, NotRun(updateIds), false);

    private static List<DriverInstallOutcome> NotRun(IReadOnlyList<string> updateIds)
        => updateIds.Select(id => new DriverInstallOutcome(id, DriverInstallStatus.NotRun, 0, false, null, null)).ToList();
}
