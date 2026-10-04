using System.Globalization;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Drivers;
using PCBoost.Core.Models.Drivers;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;

namespace PCBoost.Optimization.Handlers;

/// <summary>
/// Annule une mise à jour de pilote : retour au pilote précédent (« Restaurer le pilote » de Windows) par l'assistant
/// administrateur (<c>driver.rollback</c>), pour les seuls périphériques notés dans le journal et seulement s'ils sont
/// encore sur la version installée par PCBoost (<see cref="DriverUpdatePolicy.RollbackSkip"/>). Si rien n'est à faire
/// (pilote précédent déjà en place, périphérique absent, pilote modifié depuis), aucune autorisation n'est demandée. Un
/// périphérique absent ou modifié n'est jamais compté comme restauré : le point de restauration Windows créé avant
/// l'installation reste disponible. Tant qu'un assistant administrateur de PCBoost est actif (par exemple une installation
/// dont le résultat n'a pas été reçu), rien n'est fait ni conclu.
/// </summary>
public sealed class DriverUpdateChangeHandler : IChangeHandler
{
    private readonly IDeviceDriverProvider _devices;
    private readonly IElevationService _elevation;

    public DriverUpdateChangeHandler(IDeviceDriverProvider devices, IElevationService elevation)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
    }

    public string Kind => ChangeKinds.DriverUpdate;

    public bool CanUndo(ChangeRecord change)
        => change is { Reversible: true } && Read(change) is { } state && DriverElevatedData.RollbackParameters(state) is not null;

    public async Task<OperationResult> UndoAsync(ChangeRecord change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Read(change) is not { } state || DriverElevatedData.RollbackParameters(state) is not { } parameters)
            return OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Opt_Undo_InvalidState"));

        // Un assistant administrateur encore actif (installation dont l'application a cessé d'attendre la fin, autre
        // opération) pourrait modifier ces pilotes pendant ou après le retour : rien n'est fait, ni conclu, avant sa fin.
        if (_elevation.IsHelperRunning())
            return OperationResult.Fail(OperationErrorKind.InUse, TextRef.Of("Drv_Undo_HelperRunning"));

        // Rien à faire sans autorisation si aucun périphérique n'est encore sur la version installée par PCBoost.
        // Liste illisible : l'assistant relit lui-même et ne tente rien sans état connu.
        var targets = state.RollbackTargets();
        var current = _devices.GetInstalledDrivers();
        if (current.Success && current.Value is { } devices)
        {
            var planned = targets.Select(t => (Target: t, Skip: DriverUpdatePolicy.RollbackSkip(t, Find(devices, t.InstanceId), state.NewVersion!))).ToList();
            if (planned.All(p => p.Skip is not null))
                return Summarize(planned.Select(p => new DriverRollbackOutcome(p.Target.InstanceId, p.Skip!.Value, null, 0)).ToList(), rebootRequired: false);
        }

        var response = await _elevation.RunAsync(new ElevatedRequest(ElevatedDriverOperations.Rollback, parameters), cancellationToken).ConfigureAwait(false);
        var (outcomes, reboot) = DriverElevatedData.DecodeRollback(response.Data);
        if (outcomes.Count == 0)
            return response.Outcome.Success ? OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drv_Rollback_Failed")) : response.Outcome;
        return Summarize(outcomes, reboot);
    }

    /// <summary>
    /// Résultat global : succès seulement si chaque périphérique a son pilote précédent (réinstallé ou déjà en place).
    /// Sinon, par ordre de gravité : échec, pilote modifié depuis (jamais remplacé), périphérique absent.
    /// </summary>
    internal static OperationResult Summarize(IReadOnlyList<DriverRollbackOutcome> outcomes, bool rebootRequired)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count > 0 && outcomes.All(o => o.IsRestored))
        {
            return outcomes.Any(o => o.Status == DriverRollbackStatus.RolledBack)
                ? OperationResult.Ok(TextRef.Of(rebootRequired ? "Drv_Rollback_DoneRestart" : "Drv_Rollback_Done"))
                : OperationResult.Ok(TextRef.Of("Opt_Undo_AlreadyRestored"));
        }

        var detail = string.Join(", ", outcomes.GroupBy(o => o.Status).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key}={g.Count()}")));
        if (outcomes.Count == 0 || outcomes.Any(o => o.Status == DriverRollbackStatus.Failed))
            return OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drv_Rollback_Failed"), detail);
        if (outcomes.Any(o => o.Status == DriverRollbackStatus.Changed))
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drv_Undo_Changed"), detail);
        return OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Drv_Undo_DeviceGone"), detail);
    }

    private static InstalledDriver? Find(IReadOnlyList<InstalledDriver> devices, string instanceId)
        => devices.FirstOrDefault(d => string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

    private static DriverUpdateState? Read(ChangeRecord change)
    {
        var state = ChangeStateSerializer.Deserialize<DriverUpdateState>(change.BeforeState);
        return state is { Devices: not null } && DriverIdentifiers.IsValidUpdateId(state.UpdateId) ? state : null;
    }
}
