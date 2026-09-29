using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Security;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Handlers;

/// <summary>
/// Restaure une valeur de registre à son état exact d'origine (ou la supprime si elle n'existait pas).
/// HKLM non inscriptible → PCBoost.Elevator (<c>registry.set</c>/<c>registry.delete</c>, valeurs binaires de la liste blanche uniquement).
/// </summary>
public sealed class RegistryValueChangeHandler : IChangeHandler
{
    private readonly RegistryWriter _writer;

    internal RegistryValueChangeHandler(RegistryWriter writer) => _writer = writer;

    public RegistryValueChangeHandler(IRegistryProvider registry, IElevationService elevation)
        : this(new RegistryWriter(registry, elevation))
    {
    }

    public string Kind => ChangeKinds.RegistryValue;

    public bool CanUndo(ChangeRecord change)
    {
        var state = ChangeStateSerializer.Deserialize<RegistryValueState>(change.BeforeState);
        return change.Reversible && state is not null && !ForbiddenTargetPolicy.IsForbiddenRegistryPath(state.KeyPath);
    }

    public Task<OperationResult> UndoAsync(ChangeRecord change, CancellationToken cancellationToken = default)
    {
        var state = ChangeStateSerializer.Deserialize<RegistryValueState>(change.BeforeState);
        if (state is null)
            return Task.FromResult(OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Opt_Undo_InvalidState")));
        if (ForbiddenTargetPolicy.IsForbiddenRegistryPath(state.KeyPath))
            return Task.FromResult(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Opt_Safety_ForbiddenTarget")));

        RegistryValueData? data;
        try
        {
            data = state.ToData();
        }
        catch (FormatException)
        {
            return Task.FromResult(OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Opt_Undo_InvalidState")));
        }
        return _writer.WriteAsync(state.Location, state.ValueName, data, cancellationToken);
    }
}

/// <summary>Réactive le plan d'alimentation d'origine (idempotent).</summary>
public sealed class PowerSchemeChangeHandler : IChangeHandler
{
    private readonly IPowerProvider _power;

    public PowerSchemeChangeHandler(IPowerProvider power) => _power = power;

    public string Kind => ChangeKinds.PowerScheme;

    public bool CanUndo(ChangeRecord change)
        => change.Reversible && ChangeStateSerializer.Deserialize<PowerSchemeState>(change.BeforeState) is { SchemeId: var id } && id != Guid.Empty;

    public Task<OperationResult> UndoAsync(ChangeRecord change, CancellationToken cancellationToken = default)
    {
        var state = ChangeStateSerializer.Deserialize<PowerSchemeState>(change.BeforeState);
        if (state is null || state.SchemeId == Guid.Empty)
            return Task.FromResult(OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Opt_Undo_InvalidState")));

        if (_power.GetActiveScheme()?.Id == state.SchemeId)
            return Task.FromResult(OperationResult.Ok(TextRef.Of("Opt_Undo_AlreadyRestored")));
        if (_power.GetSchemes().All(s => s.Id != state.SchemeId))
            return Task.FromResult(OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Opt_Undo_PowerSchemeMissing", state.SchemeName ?? state.SchemeId.ToString())));
        return Task.FromResult(_power.SetActiveScheme(state.SchemeId));
    }
}

/// <summary>
/// Rétablit la priorité d'origine, uniquement si le même processus (PID + heure de démarrage) tourne encore ;
/// sinon l'effet a disparu avec le processus : « rien à restaurer ».
/// </summary>
public sealed class ProcessPriorityChangeHandler : IChangeHandler
{
    private readonly IProcessProvider _processes;
    private readonly IProcessControl _control;

    public ProcessPriorityChangeHandler(IProcessProvider processes, IProcessControl control)
    {
        _processes = processes;
        _control = control;
    }

    public string Kind => ChangeKinds.ProcessPriority;

    public bool CanUndo(ChangeRecord change)
        => change.Reversible && ChangeStateSerializer.Deserialize<ProcessPriorityState>(change.BeforeState) is { } s
           && s.Priority is not (ProcessPriority.Unknown or ProcessPriority.RealTime);

    public Task<OperationResult> UndoAsync(ChangeRecord change, CancellationToken cancellationToken = default)
    {
        var state = ChangeStateSerializer.Deserialize<ProcessPriorityState>(change.BeforeState);
        if (state is null || state.Priority is ProcessPriority.Unknown or ProcessPriority.RealTime)
            return Task.FromResult(OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Opt_Undo_InvalidState")));

        if (!_processes.IsRunning(state.ProcessId, state.StartTime))
            return Task.FromResult(OperationResult.Ok(TextRef.Of("Opt_Undo_ProcessGone")));

        var current = _control.GetPriority(state.ProcessId);
        if (current.Success && current.Value == state.Priority)
            return Task.FromResult(OperationResult.Ok(TextRef.Of("Opt_Undo_AlreadyRestored")));

        var result = _control.SetPriority(state.ProcessId, state.Priority);
        // Le processus s'est terminé entre-temps : plus rien à restaurer.
        return Task.FromResult(!result.Success && result.Error == OperationErrorKind.NotFound
            ? OperationResult.Ok(TextRef.Of("Opt_Undo_ProcessGone"))
            : result);
    }
}

/// <summary>Rétablit l'état du mode efficacité (EcoQoS), uniquement pour le même processus (PID + heure de démarrage).</summary>
public sealed class ProcessEfficiencyChangeHandler : IChangeHandler
{
    private readonly IProcessProvider _processes;
    private readonly IProcessControl _control;

    public ProcessEfficiencyChangeHandler(IProcessProvider processes, IProcessControl control)
    {
        _processes = processes;
        _control = control;
    }

    public string Kind => ChangeKinds.ProcessEfficiency;

    public bool CanUndo(ChangeRecord change)
        => change.Reversible && ChangeStateSerializer.Deserialize<ProcessEfficiencyState>(change.BeforeState) is not null;

    public Task<OperationResult> UndoAsync(ChangeRecord change, CancellationToken cancellationToken = default)
    {
        var state = ChangeStateSerializer.Deserialize<ProcessEfficiencyState>(change.BeforeState);
        if (state is null)
            return Task.FromResult(OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Opt_Undo_InvalidState")));

        if (!_processes.IsRunning(state.ProcessId, state.StartTime))
            return Task.FromResult(OperationResult.Ok(TextRef.Of("Opt_Undo_ProcessGone")));

        var current = _control.GetEfficiencyMode(state.ProcessId);
        if (current.Success && current.Value == state.Enabled)
            return Task.FromResult(OperationResult.Ok(TextRef.Of("Opt_Undo_AlreadyRestored")));

        var result = _control.SetEfficiencyMode(state.ProcessId, state.Enabled);
        return Task.FromResult(!result.Success && result.Error == OperationErrorKind.NotFound
            ? OperationResult.Ok(TextRef.Of("Opt_Undo_ProcessGone"))
            : result);
    }
}

/// <summary>Rétablit l'état complet des effets visuels (<see cref="VisualEffectsState"/>).</summary>
public sealed class VisualEffectsChangeHandler : IChangeHandler
{
    private readonly IVisualEffectsProvider _visualEffects;

    public VisualEffectsChangeHandler(IVisualEffectsProvider visualEffects) => _visualEffects = visualEffects;

    public string Kind => ChangeKinds.VisualEffects;

    public bool CanUndo(ChangeRecord change)
        => change.Reversible && ChangeStateSerializer.Deserialize<VisualEffectsState>(change.BeforeState) is not null;

    public Task<OperationResult> UndoAsync(ChangeRecord change, CancellationToken cancellationToken = default)
    {
        var state = ChangeStateSerializer.Deserialize<VisualEffectsState>(change.BeforeState);
        if (state is null)
            return Task.FromResult(OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Opt_Undo_InvalidState")));
        if (_visualEffects.GetState() == state)
            return Task.FromResult(OperationResult.Ok(TextRef.Of("Opt_Undo_AlreadyRestored")));
        return Task.FromResult(_visualEffects.SetState(state));
    }
}

/// <summary>Rétablit l'état activé/désactivé d'une tâche planifiée ; accès refusé → PCBoost.Elevator (<c>task.setenabled</c>).</summary>
public sealed class ScheduledTaskChangeHandler : IChangeHandler
{
    private readonly IScheduledTaskProvider _tasks;
    private readonly ScheduledTaskWriter _writer;

    internal ScheduledTaskChangeHandler(IScheduledTaskProvider tasks, ScheduledTaskWriter writer)
    {
        _tasks = tasks;
        _writer = writer;
    }

    public ScheduledTaskChangeHandler(IScheduledTaskProvider tasks, IElevationService elevation)
        : this(tasks, new ScheduledTaskWriter(tasks, elevation))
    {
    }

    public string Kind => ChangeKinds.ScheduledTask;

    public bool CanUndo(ChangeRecord change)
        => change.Reversible && ChangeStateSerializer.Deserialize<ScheduledTaskState>(change.BeforeState) is { } s
           && !ScheduledTaskWriter.IsMicrosoftTaskPath(s.TaskPath);

    public async Task<OperationResult> UndoAsync(ChangeRecord change, CancellationToken cancellationToken = default)
    {
        var state = ChangeStateSerializer.Deserialize<ScheduledTaskState>(change.BeforeState);
        if (state is null || string.IsNullOrWhiteSpace(state.TaskPath))
            return OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Opt_Undo_InvalidState"));

        var current = _tasks.IsEnabled(state.TaskPath);
        if (current == state.Enabled)
            return OperationResult.Ok(TextRef.Of("Opt_Undo_AlreadyRestored"));

        var result = await _writer.SetEnabledAsync(state.TaskPath, state.Enabled, cancellationToken).ConfigureAwait(false);
        // Tâche supprimée entre-temps (désinstallation) : plus rien à restaurer.
        return !result.Success && result.Error == OperationErrorKind.NotFound
            ? OperationResult.Ok(TextRef.Of("Opt_Undo_TaskGone"))
            : result;
    }
}

/// <summary>Actions irréversibles par nature (suppression de fichiers, vidage de la corbeille) : consignées, jamais annulables.</summary>
public sealed class IrreversibleChangeHandler : IChangeHandler
{
    public IrreversibleChangeHandler(string kind) => Kind = kind;

    public string Kind { get; }

    public bool CanUndo(ChangeRecord change) => false;

    public Task<OperationResult> UndoAsync(ChangeRecord change, CancellationToken cancellationToken = default)
        => Task.FromResult(OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Opt_Undo_Irreversible")));
}
