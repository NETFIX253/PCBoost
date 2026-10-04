using System.Runtime.InteropServices;
using PCBoost.Core.Drivers;
using PCBoost.Core.Models.Drivers;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform.Drivers;

/// <summary>
/// Retour au pilote précédent par l'assistant administrateur, périphérique par périphérique, avec « Restaurer le pilote »
/// de Windows (DiRollbackDriver), qui réinstalle le pilote précédent conservé dans le magasin de pilotes.
/// <list type="bullet">
/// <item>Liste des périphériques illisible : rien n'est tenté (échec, jamais « périphérique absent »).</item>
/// <item>Un périphérique n'est ramené que s'il est encore sur la version installée par PCBoost
/// (<see cref="DriverUpdatePolicy.RollbackSkip"/>) : un pilote modifié depuis n'est jamais remplacé.</item>
/// <item>Aucune réinstallation forcée d'un fichier INF : si Windows ne peut pas restaurer le pilote, le point de
/// restauration créé avant l'installation reste la solution.</item>
/// <item>Le résultat est vérifié en relisant la version du pilote de chaque périphérique.</item>
/// </list>
/// </summary>
internal sealed class DriverRollback
{
    private readonly Action<string> _log;
    private readonly Func<IReadOnlyList<InstalledDriver>?> _readDevices;
    private readonly Func<string, (bool Ok, int Error, bool NeedReboot)> _rollbackDevice;

    /// <param name="readDevices">Périphériques présents, ou null si la liste n'a pas pu être lue.</param>
    /// <param name="rollbackDevice">« Restaurer le pilote » d'un périphérique (identifiant d'instance).</param>
    public DriverRollback(Action<string> log, Func<IReadOnlyList<InstalledDriver>?>? readDevices = null,
        Func<string, (bool Ok, int Error, bool NeedReboot)>? rollbackDevice = null)
    {
        _log = log;
        _readDevices = readDevices ?? ReadDevices;
        _rollbackDevice = rollbackDevice ?? RollbackDevice;
    }

    public (IReadOnlyList<DriverRollbackOutcome> Outcomes, bool RebootRequired) Run(IReadOnlyList<DriverRollbackTarget> targets, string installedVersion)
    {
        var devices = _readDevices();
        if (devices is null)
        {
            _log("Retour au pilote précédent : liste des périphériques illisible, rien n'est tenté");
            return (targets.Select(t => new DriverRollbackOutcome(t.InstanceId, DriverRollbackStatus.Failed, null, 0)).ToList(), false);
        }

        var outcomes = new List<DriverRollbackOutcome>();
        var needsRestart = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            var device = Find(devices, target.InstanceId);
            if (DriverUpdatePolicy.RollbackSkip(target, device, installedVersion) is { } skip)
            {
                _log($"Retour au pilote précédent : périphérique ignoré ({skip})");
                outcomes.Add(new DriverRollbackOutcome(target.InstanceId, skip, device?.Version, 0));
                continue;
            }

            var (ok, error, reboot) = _rollbackDevice(target.InstanceId);
            if (ok && reboot) needsRestart.Add(target.InstanceId);
            _log($"Restaurer le pilote : {(ok ? "réussi" : $"échec (erreur {error})")}{(reboot ? ", redémarrage requis" : string.Empty)}");
            outcomes.Add(new DriverRollbackOutcome(target.InstanceId, ok ? DriverRollbackStatus.RolledBack : DriverRollbackStatus.Failed, null, ok ? 0 : error));
        }

        // Vérification : version du pilote de chaque périphérique ramené (sauf redémarrage nécessaire pour l'appliquer).
        if (outcomes.Any(o => o.Status == DriverRollbackStatus.RolledBack))
        {
            var after = _readDevices();
            for (var i = 0; i < outcomes.Count; i++)
            {
                if (outcomes[i].Status != DriverRollbackStatus.RolledBack) continue;
                if (after is null)
                {
                    // Windows a confirmé l'opération ; la version n'a simplement pas pu être relue.
                    _log("Vérification impossible : liste des périphériques illisible");
                    continue;
                }
                var version = Find(after, outcomes[i].InstanceId)?.Version;
                var restored = needsRestart.Contains(outcomes[i].InstanceId) || DriverUpdatePolicy.SameVersion(version, targets[i].PreviousVersion);
                outcomes[i] = outcomes[i] with { VersionAfter = version, Status = restored ? DriverRollbackStatus.RolledBack : DriverRollbackStatus.Failed };
                if (!restored) _log("Vérification : la version précédente n'est pas en place");
            }
        }
        return (outcomes, needsRestart.Count > 0);
    }

    private static (bool Ok, int Error, bool NeedReboot) RollbackDevice(string instanceId)
    {
        var set = DeviceInstall.SetupDiCreateDeviceInfoList(0, 0);
        if (set == -1 || set == 0) return (false, Marshal.GetLastPInvokeError(), false);
        try
        {
            var data = new DeviceInstall.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<DeviceInstall.SP_DEVINFO_DATA>() };
            if (!DeviceInstall.SetupDiOpenDeviceInfoW(set, instanceId, 0, 0, ref data))
                return (false, Marshal.GetLastPInvokeError(), false);
            var ok = DeviceInstall.DiRollbackDriver(set, ref data, 0, DeviceInstall.ROLLBACK_FLAG_NO_UI, out var needReboot);
            return (ok, ok ? 0 : Marshal.GetLastPInvokeError(), ok && needReboot);
        }
        finally
        {
            DeviceInstall.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static InstalledDriver? Find(IReadOnlyList<InstalledDriver> devices, string instanceId)
        => devices.FirstOrDefault(d => string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<InstalledDriver>? ReadDevices()
    {
        var result = new DeviceDriverProvider().GetInstalledDrivers();
        return result.Success ? result.Value : null;
    }
}
