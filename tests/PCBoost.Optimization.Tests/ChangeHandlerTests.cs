using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Handlers;
using PCBoost.TestUtilities;

namespace PCBoost.Optimization.Tests;

public sealed class ChangeHandlerTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private IChangeHandler Handler(string kind) => _h.Get<IEnumerable<IChangeHandler>>().First(x => x.Kind == kind);

    private static ChangeRecord Record(string kind, string? before, bool reversible = true) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        OptimizationId = "test",
        Kind = kind,
        Target = "t",
        Description = TextRef.Literal("t"),
        BeforeState = before,
        Reversible = reversible,
        Status = ChangeStatus.Applied,
        RecordedAt = DateTimeOffset.UtcNow,
    };

    private const string MachineApprovedRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    [Fact]
    public async Task Registry_HklmNotWritable_SendsElevatedRegistrySetRequest()
    {
        _h.Registry.LocalMachineWritable = false;
        var original = new byte[] { 0x06, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        var location = new RegistryLocation(RegistryHiveKind.LocalMachine, MachineApprovedRun, RegistryViewKind.Registry64);
        var state = RegistryValueState.Capture(location, "Vendor", RegistryValueData.Binary(original));

        var result = await Handler(ChangeKinds.RegistryValue).UndoAsync(Record(ChangeKinds.RegistryValue, ChangeStateSerializer.Serialize(state)));

        Assert.True(result.Success);
        var request = Assert.Single(_h.Elevation.Requests);
        Assert.Equal(ElevatedOperations.RegistrySetValue, request.Operation);
        Assert.Equal(MachineApprovedRun, request.Parameters["path"]);
        Assert.Equal("Vendor", request.Parameters["name"]);
        Assert.Equal("Registry64", request.Parameters["view"]);
        Assert.Equal(Convert.ToBase64String(original), request.Parameters["valueBase64"]);
        Assert.Equal(4, request.Parameters.Count);
    }

    [Fact]
    public async Task Registry_HklmNotWritable_ValueDidNotExist_SendsElevatedDelete()
    {
        _h.Registry.LocalMachineWritable = false;
        var location = new RegistryLocation(RegistryHiveKind.LocalMachine, MachineApprovedRun, RegistryViewKind.Registry64);
        var state = RegistryValueState.Capture(location, "Vendor", null);

        var result = await Handler(ChangeKinds.RegistryValue).UndoAsync(Record(ChangeKinds.RegistryValue, ChangeStateSerializer.Serialize(state)));

        Assert.True(result.Success);
        var request = Assert.Single(_h.Elevation.Requests);
        Assert.Equal(ElevatedOperations.RegistryDeleteValue, request.Operation);
        Assert.False(request.Parameters.ContainsKey("valueBase64"));
    }

    [Fact]
    public async Task Registry_UacRefused_ReturnsElevationCancelled_AndSessionShowsRollbackFailed()
    {
        _h.Registry.LocalMachineWritable = false;
        _h.Elevation.UserCancels = true;
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Startup, TextRef.Literal("t"));
        var location = new RegistryLocation(RegistryHiveKind.LocalMachine, MachineApprovedRun, RegistryViewKind.Registry64);
        var state = RegistryValueState.Capture(location, "Vendor", null);
        await recorder.ApplyAsync(new PendingChange(ChangeKinds.RegistryValue, "test", $"{location} : Vendor", TextRef.Literal("x"),
            ChangeStateSerializer.Serialize(state), true), _ => Task.FromResult(OperationResult.Ok()));
        await rollback.CompleteSessionAsync(recorder.SessionId);

        var result = await rollback.RestoreSessionAsync(recorder.SessionId);

        Assert.Equal(1, result.Failed);
        Assert.Equal(OperationErrorKind.ElevationCancelled, result.Errors.Single().Error);
        var session = await rollback.GetSessionAsync(recorder.SessionId);
        Assert.Equal(ChangeStatus.RollbackFailed, session!.Changes.Single().Status);
        Assert.True(rollback.CanRollback(session));
    }

    [Fact]
    public async Task Registry_ElevationRefusedForNonAllowListedKeyOrNonBinaryValue()
    {
        _h.Registry.LocalMachineWritable = false;
        var handler = Handler(ChangeKinds.RegistryValue);
        var otherKey = RegistryValueState.Capture(new RegistryLocation(RegistryHiveKind.LocalMachine, @"SOFTWARE\Vendor\Settings"), "v", RegistryValueData.Binary([1]));
        var stringValue = RegistryValueState.Capture(new RegistryLocation(RegistryHiveKind.LocalMachine, MachineApprovedRun), "v", RegistryValueData.String("x"));

        var r1 = await handler.UndoAsync(Record(ChangeKinds.RegistryValue, ChangeStateSerializer.Serialize(otherKey)));
        var r2 = await handler.UndoAsync(Record(ChangeKinds.RegistryValue, ChangeStateSerializer.Serialize(stringValue)));

        Assert.False(r1.Success);
        Assert.Equal(OperationErrorKind.Blocked, r1.Error);
        Assert.False(r2.Success);
        Assert.Empty(_h.Elevation.Requests);
    }

    [Fact]
    public async Task Registry_CurrentUser_RestoresExactValue_Idempotently()
    {
        var location = new RegistryLocation(RegistryHiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        var original = new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var state = RegistryValueState.Capture(location, "App", RegistryValueData.Binary(original));
        _h.Registry.Set(RegistryHiveKind.CurrentUser, location.KeyPath, "App", RegistryValueData.Binary([0x03, 0, 0, 0, 9, 9, 9, 9, 9, 9, 9, 9]));
        var handler = Handler(ChangeKinds.RegistryValue);
        var record = Record(ChangeKinds.RegistryValue, ChangeStateSerializer.Serialize(state));

        Assert.True((await handler.UndoAsync(record)).Success);
        Assert.True((await handler.UndoAsync(record)).Success);

        Assert.Equal(original, (byte[])_h.Registry.GetValue(location, "App")!.Value);
        Assert.Empty(_h.Elevation.Requests);
    }

    [Fact]
    public async Task Power_RestoresPreviousScheme_AndFailsHonestlyIfSchemeDisappeared()
    {
        var handler = Handler(ChangeKinds.PowerScheme);
        _h.Power.ActiveId = PowerScheme.HighPerformance;

        var ok = await handler.UndoAsync(Record(ChangeKinds.PowerScheme, ChangeStateSerializer.Serialize(new PowerSchemeState(PowerScheme.PowerSaver, "Économie d'énergie"))));
        var missing = await handler.UndoAsync(Record(ChangeKinds.PowerScheme, ChangeStateSerializer.Serialize(new PowerSchemeState(Guid.NewGuid(), "OEM"))));

        Assert.True(ok.Success);
        Assert.Equal(PowerScheme.PowerSaver, _h.Power.ActiveId);
        Assert.False(missing.Success);
        Assert.Equal(OperationErrorKind.NotFound, missing.Error);
    }

    [Fact]
    public async Task ProcessPriority_OnlyRestoredIfSameProcessStillRuns()
    {
        var start = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
        _h.Processes.Add(100, "app.exe", start: start, priority: ProcessPriority.BelowNormal);
        var handler = Handler(ChangeKinds.ProcessPriority);

        var restored = await handler.UndoAsync(Record(ChangeKinds.ProcessPriority, ChangeStateSerializer.Serialize(new ProcessPriorityState(100, "app.exe", start, ProcessPriority.Normal))));
        Assert.True(restored.Success);
        Assert.Equal(ProcessPriority.Normal, _h.Processes.GetProcess(100)!.Priority);

        // PID réutilisé par un autre processus (heure de démarrage différente) : rien n'est touché.
        _h.Processes.Add(200, "other.exe", start: start.AddHours(1), priority: ProcessPriority.BelowNormal);
        var reused = await handler.UndoAsync(Record(ChangeKinds.ProcessPriority, ChangeStateSerializer.Serialize(new ProcessPriorityState(200, "old.exe", start, ProcessPriority.Normal))));
        Assert.True(reused.Success);
        Assert.Equal("Opt_Undo_ProcessGone", reused.Message!.Key);
        Assert.Equal(ProcessPriority.BelowNormal, _h.Processes.GetProcess(200)!.Priority);

        var gone = await handler.UndoAsync(Record(ChangeKinds.ProcessPriority, ChangeStateSerializer.Serialize(new ProcessPriorityState(999, "gone.exe", start, ProcessPriority.Normal))));
        Assert.True(gone.Success);
    }

    [Fact]
    public async Task ProcessEfficiency_RestoresPreviousMode()
    {
        var start = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
        _h.Processes.Add(100, "app.exe", start: start);
        _h.Processes.SetEfficiencyMode(100, true);

        var result = await Handler(ChangeKinds.ProcessEfficiency).UndoAsync(
            Record(ChangeKinds.ProcessEfficiency, ChangeStateSerializer.Serialize(new ProcessEfficiencyState(100, "app.exe", start, false))));

        Assert.True(result.Success);
        Assert.False(_h.Processes.GetEfficiencyMode(100).Value);
    }

    [Fact]
    public async Task VisualEffects_RestoresCompleteState()
    {
        var original = new VisualEffectsState(true, false, true, true, false, true, true, false, true);
        _h.Visual.State = new VisualEffectsState(false, false, false, false, false, false, false, false, false);

        var result = await Handler(ChangeKinds.VisualEffects).UndoAsync(Record(ChangeKinds.VisualEffects, ChangeStateSerializer.Serialize(original)));

        Assert.True(result.Success);
        Assert.Equal(original, _h.Visual.State);
    }

    [Fact]
    public async Task ScheduledTask_AccessDenied_UsesElevatedTaskRequest()
    {
        var tasks = new ProtectedScheduledTaskProvider();
        tasks.Tasks.Add(new ScheduledTaskInfo(@"\Vendor\Updater", "Updater", "Vendor", @"C:\Program Files\Vendor\up.exe", null, false, false));
        _h.Tasks = tasks;

        var result = await Handler(ChangeKinds.ScheduledTask).UndoAsync(
            Record(ChangeKinds.ScheduledTask, ChangeStateSerializer.Serialize(new ScheduledTaskState(@"\Vendor\Updater", true))));

        Assert.True(result.Success);
        var request = Assert.Single(_h.Elevation.Requests);
        Assert.Equal(ElevatedOperations.ScheduledTaskSetEnabled, request.Operation);
        Assert.Equal(@"\Vendor\Updater", request.Parameters["path"]);
        Assert.Equal("true", request.Parameters["enabled"]);
    }

    [Fact]
    public async Task ScheduledTask_MicrosoftTaskIsNeverTouched()
    {
        var handler = Handler(ChangeKinds.ScheduledTask);
        var record = Record(ChangeKinds.ScheduledTask, ChangeStateSerializer.Serialize(new ScheduledTaskState(@"\Microsoft\Windows\Defrag\ScheduledDefrag", true)));

        Assert.False(handler.CanUndo(record));
        var result = await handler.UndoAsync(record);
        Assert.Equal(OperationErrorKind.Blocked, result.Error);
    }

    [Fact]
    public void Irreversible_Kinds_CannotBeUndone()
    {
        Assert.False(Handler(ChangeKinds.FileDeletion).CanUndo(Record(ChangeKinds.FileDeletion, null, false)));
        Assert.False(Handler(ChangeKinds.RecycleBin).CanUndo(Record(ChangeKinds.RecycleBin, null, false)));
    }

    [Fact]
    public void EveryReversibleChangeKind_HasAHandler()
    {
        var kinds = _h.Get<IEnumerable<IChangeHandler>>().Select(x => x.Kind).ToHashSet();
        Assert.Contains(ChangeKinds.RegistryValue, kinds);
        Assert.Contains(ChangeKinds.PowerScheme, kinds);
        Assert.Contains(ChangeKinds.ProcessPriority, kinds);
        Assert.Contains(ChangeKinds.ProcessEfficiency, kinds);
        Assert.Contains(ChangeKinds.VisualEffects, kinds);
        Assert.Contains(ChangeKinds.ScheduledTask, kinds);
        Assert.Contains(ChangeKinds.FileDeletion, kinds);
        Assert.Contains(ChangeKinds.RecycleBin, kinds);
    }
}
