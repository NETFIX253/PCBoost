using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Optimization;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Startup;

/// <summary>
/// Active/désactive une entrée de démarrage avec consignation write-ahead : valeur StartupApproved (état exact avant/après)
/// ou état d'une tâche planifiée. Utilisé par la page Démarrage et par le module startup-apps.
/// </summary>
internal sealed class StartupToggler
{
    private readonly IRegistryProvider _registry;
    private readonly RegistryWriter _registryWriter;
    private readonly IScheduledTaskProvider _tasks;
    private readonly ScheduledTaskWriter _taskWriter;
    private readonly IClock _clock;

    public StartupToggler(IRegistryProvider registry, RegistryWriter registryWriter, IScheduledTaskProvider tasks, ScheduledTaskWriter taskWriter, IClock clock)
    {
        _registry = registry;
        _registryWriter = registryWriter;
        _tasks = tasks;
        _taskWriter = taskWriter;
        _clock = clock;
    }

    internal const string AlreadyInStateKey = "Opt_Startup_AlreadyInState";

    /// <summary>Le résultat indique-t-il qu'aucune modification n'était nécessaire ?</summary>
    public static bool IsNoChange(OperationResult outcome) => outcome.Success && outcome.Message?.Key == AlreadyInStateKey;

    /// <summary>Cible textuelle consignée pour une entrée (validée par ForbiddenTargetPolicy).</summary>
    public static string TargetOf(StartupEntry entry)
    {
        if (entry.Location == StartupLocation.ScheduledTaskLogon) return "task:" + entry.SourcePath;
        var approved = StartupApproved.ApprovedLocation(entry.Location);
        return approved is null ? entry.Id : $"{approved} : {entry.ItemName}";
    }

    public async Task<OperationResult> SetEnabledAsync(StartupEntry entry, bool enabled, IChangeRecorder recorder, string optimizationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Les logiciels de sécurité (Sécurité Windows, antivirus) ne sont jamais désactivés par PCBoost.
        if (!enabled && entry.IsSecuritySoftware)
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Opt_Startup_SecurityBlocked"));

        var description = TextRef.Of(enabled ? "Opt_Change_StartupEnable" : "Opt_Change_StartupDisable", entry.Name);

        if (entry.Location == StartupLocation.ScheduledTaskLogon)
        {
            var taskPath = entry.SourcePath;
            if (ScheduledTaskWriter.IsMicrosoftTaskPath(taskPath))
                return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Opt_Safety_MicrosoftTask"));
            var current = _tasks.IsEnabled(taskPath) ?? entry.IsEnabled;
            if (current == enabled) return OperationResult.Ok(TextRef.Of(AlreadyInStateKey));

            var change = new PendingChange(ChangeKinds.ScheduledTask, optimizationId, TargetOf(entry), description,
                ChangeStateSerializer.Serialize(new ScheduledTaskState(taskPath, current)), Reversible: true);
            return await recorder.ApplyWithAfterStateAsync(change, ChangeStateSerializer.Serialize(new ScheduledTaskState(taskPath, enabled)),
                ct => _taskWriter.SetEnabledAsync(taskPath, enabled, ct), cancellationToken).ConfigureAwait(false);
        }

        var approved = StartupApproved.ApprovedLocation(entry.Location);
        if (approved is null) return OperationResult.Fail(OperationErrorKind.NotSupported);

        var currentValue = _registry.GetValue(approved, entry.ItemName);
        if (StartupApproved.IsEnabled(currentValue) == enabled)
            return OperationResult.Ok(TextRef.Of(AlreadyInStateKey));

        var newValue = RegistryValueData.Binary(enabled ? StartupApproved.EnabledValue() : StartupApproved.DisabledValue(_clock.UtcNow));
        var before = RegistryValueState.Capture(approved, entry.ItemName, currentValue);
        var after = RegistryValueState.Capture(approved, entry.ItemName, newValue);
        var registryChange = new PendingChange(ChangeKinds.RegistryValue, optimizationId, TargetOf(entry), description,
            ChangeStateSerializer.Serialize(before), Reversible: true);
        return await recorder.ApplyWithAfterStateAsync(registryChange, ChangeStateSerializer.Serialize(after),
            ct => _registryWriter.WriteAsync(approved, entry.ItemName, newValue, ct), cancellationToken).ConfigureAwait(false);
    }
}
