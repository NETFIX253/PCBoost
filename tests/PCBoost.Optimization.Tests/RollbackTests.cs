using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Rollback;

namespace PCBoost.Optimization.Tests;

public sealed class RollbackTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private const string TestKind = "test.kind";

    private RecordingChangeHandler UseRecordingHandler()
    {
        var handler = new RecordingChangeHandler(TestKind);
        _h.ExtraHandlers.Add(handler);
        return handler;
    }

    private static PendingChange Change(string target, string kind = TestKind, bool reversible = true, string? before = "{}")
        => new(kind, "test", target, TextRef.Literal(target), before, reversible);

    [Fact]
    public async Task ApplyAsync_PersistsPendingRecordBeforeExecutingChange()
    {
        UseRecordingHandler();
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));
        ChangeStatus? statusSeenDuringApply = null;
        string? beforeSeenDuringApply = null;

        var result = await recorder.ApplyAsync(Change("target-1", before: "{\"x\":1}"), _ =>
        {
            var record = _h.History.AllChanges.Single();
            statusSeenDuringApply = record.Status;
            beforeSeenDuringApply = record.BeforeState;
            return Task.FromResult(OperationResult.Ok());
        });

        Assert.True(result.Success);
        Assert.Equal(ChangeStatus.Pending, statusSeenDuringApply);
        Assert.Equal("{\"x\":1}", beforeSeenDuringApply);
        Assert.Equal(ChangeStatus.Applied, _h.History.AllChanges.Single().Status);
        Assert.Equal(1, _h.History.AllChanges.Single().Sequence);
    }

    [Fact]
    public async Task ApplyAsync_FailedExecution_IsMarkedFailedWithDetail()
    {
        UseRecordingHandler();
        var recorder = await _h.Get<IRollbackManager>().BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));

        var result = await recorder.ApplyAsync(Change("t"), _ => throw new UnauthorizedAccessException("denied"));

        Assert.False(result.Success);
        Assert.Equal(OperationErrorKind.AccessDenied, result.Error);
        var record = _h.History.AllChanges.Single();
        Assert.Equal(ChangeStatus.Failed, record.Status);
        Assert.Contains("AccessDenied", record.ErrorDetail);
    }

    [Theory]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender")]
    [InlineData(@"C:\Windows\System32\drivers\x.sys")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU")]
    [InlineData("service:wuauserv")]
    public async Task ApplyAsync_ForbiddenTarget_IsBlockedWithoutExecution(string target)
    {
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));
        var executed = false;

        var result = await recorder.ApplyAsync(Change(target, ChangeKinds.RegistryValue,
                before: ChangeStateSerializer.Serialize(new RegistryValueState(RegistryHiveKind.CurrentUser, RegistryViewKind.Default, @"Software\Test", "v", false))),
            _ =>
            {
                executed = true;
                return Task.FromResult(OperationResult.Ok());
            });

        Assert.False(executed);
        Assert.False(result.Success);
        Assert.Equal(OperationErrorKind.Blocked, result.Error);
        Assert.All(_h.History.AllChanges, c => Assert.NotEqual(ChangeStatus.Applied, c.Status));
    }

    [Fact]
    public async Task ApplyAsync_RegistryStateTargetingForbiddenKey_IsBlockedEvenWithHarmlessLabel()
    {
        var recorder = await _h.Get<IRollbackManager>().BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));
        var executed = false;
        var state = new RegistryValueState(RegistryHiveKind.LocalMachine, RegistryViewKind.Default, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", false);

        var result = await recorder.ApplyAsync(Change("harmless label", ChangeKinds.RegistryValue, before: ChangeStateSerializer.Serialize(state)),
            _ => { executed = true; return Task.FromResult(OperationResult.Ok()); });

        Assert.False(executed);
        Assert.Equal(OperationErrorKind.Blocked, result.Error);
    }

    [Fact]
    public async Task ApplyAsync_UnknownKind_IsBlockedWithoutExecution()
    {
        var recorder = await _h.Get<IRollbackManager>().BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));
        var executed = false;

        var result = await recorder.ApplyAsync(Change("x", kind: "bcd.edit"), _ => { executed = true; return Task.FromResult(OperationResult.Ok()); });

        Assert.False(executed);
        Assert.Equal(OperationErrorKind.Blocked, result.Error);
    }

    [Fact]
    public async Task RestoreSession_UndoesInReverseOrder()
    {
        var handler = UseRecordingHandler();
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));
        foreach (var target in new[] { "first", "second", "third" })
            await recorder.ApplyAsync(Change(target), _ => Task.FromResult(OperationResult.Ok()));
        await rollback.CompleteSessionAsync(recorder.SessionId);

        var result = await rollback.RestoreSessionAsync(recorder.SessionId);

        Assert.Equal(["third", "second", "first"], handler.UndoneTargets);
        Assert.Equal(3, result.Restored);
        Assert.True(result.Success);
        var session = await rollback.GetSessionAsync(recorder.SessionId);
        Assert.Equal(SessionStatus.RolledBack, session!.Status);
        Assert.All(session.Changes, c => Assert.Equal(ChangeStatus.RolledBack, c.Status));
    }

    [Fact]
    public async Task RestoreSession_UndoesPendingChange_WhoseOutcomeIsUnknown()
    {
        var handler = UseRecordingHandler();
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));
        using var cts = new CancellationTokenSource();

        // Annulation pendant l'application : l'état réel est incertain, la modification reste Pending.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.ApplyAsync(Change("maybe-applied"), ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(OperationResult.Ok());
        }, cts.Token));
        Assert.Equal(ChangeStatus.Pending, _h.History.AllChanges.Single().Status);

        var result = await rollback.RestoreSessionAsync(recorder.SessionId);

        Assert.Equal(["maybe-applied"], handler.UndoneTargets);
        Assert.Equal(1, result.Restored);
    }

    [Fact]
    public async Task RestoreSession_IsIdempotent()
    {
        var handler = UseRecordingHandler();
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));
        await recorder.ApplyAsync(Change("a"), _ => Task.FromResult(OperationResult.Ok()));
        await rollback.CompleteSessionAsync(recorder.SessionId);

        var first = await rollback.RestoreSessionAsync(recorder.SessionId);
        var second = await rollback.RestoreSessionAsync(recorder.SessionId);

        Assert.Equal(1, first.Restored);
        Assert.Equal(0, second.Restored);
        Assert.Equal(0, second.Failed);
        Assert.Single(handler.UndoneTargets);
        Assert.False(rollback.CanRollback((await rollback.GetSessionAsync(recorder.SessionId))!));
    }

    [Fact]
    public async Task RestoreSession_PartialFailure_GivesPartiallyRolledBack_AndCanBeRetried()
    {
        var handler = UseRecordingHandler();
        handler.FailingTargets.Add("b");
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));
        await recorder.ApplyAsync(Change("a"), _ => Task.FromResult(OperationResult.Ok()));
        await recorder.ApplyAsync(Change("b"), _ => Task.FromResult(OperationResult.Ok()));
        await rollback.CompleteSessionAsync(recorder.SessionId);

        var result = await rollback.RestoreSessionAsync(recorder.SessionId);

        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.Failed);
        Assert.False(result.Success);
        var session = await rollback.GetSessionAsync(recorder.SessionId);
        Assert.Equal(SessionStatus.PartiallyRolledBack, session!.Status);
        Assert.Equal(ChangeStatus.RollbackFailed, session.Changes.Single(c => c.Target == "b").Status);
        Assert.True(rollback.CanRollback(session));

        handler.FailingTargets.Clear();
        var retry = await rollback.RestoreSessionAsync(recorder.SessionId);
        Assert.Equal(1, retry.Restored);
        Assert.Equal(SessionStatus.RolledBack, (await rollback.GetSessionAsync(recorder.SessionId))!.Status);
    }

    [Fact]
    public async Task RestoreSession_IrreversibleChanges_AreCountedButNeverUndone()
    {
        var handler = UseRecordingHandler();
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Cleanup, TextRef.Literal("t"));
        await recorder.ApplyAsync(Change("reversible"), _ => Task.FromResult(OperationResult.Ok()));
        await recorder.RecordIrreversibleAsync(new PendingChange(ChangeKinds.FileDeletion, "temp-files", "cleanup:user-temp", TextRef.Literal("x"), null, false),
            OperationResult.Ok(), 1234);
        await rollback.CompleteSessionAsync(recorder.SessionId, 1234);

        var result = await rollback.RestoreSessionAsync(recorder.SessionId);

        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.Irreversible);
        Assert.Equal(["reversible"], handler.UndoneTargets);
        var session = await rollback.GetSessionAsync(recorder.SessionId);
        var irreversible = session!.Changes.Single(c => c.Kind == ChangeKinds.FileDeletion);
        Assert.Equal(ChangeStatus.Irreversible, irreversible.Status);
        Assert.Equal(1234, ChangeStateSerializer.Deserialize<IrreversibleOutcomeState>(irreversible.AfterState)!.BytesFreed);
    }

    [Fact]
    public async Task CompleteSession_ComputesStatusFromChanges()
    {
        UseRecordingHandler();
        var rollback = _h.Get<IRollbackManager>();

        async Task<SessionStatus> Run(params bool[] outcomes)
        {
            var recorder = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));
            var i = 0;
            foreach (var ok in outcomes)
                await recorder.ApplyAsync(Change("c" + i++), _ => Task.FromResult(ok ? OperationResult.Ok() : OperationResult.Fail(OperationErrorKind.Failed)));
            await rollback.CompleteSessionAsync(recorder.SessionId, 10, true);
            return (await rollback.GetSessionAsync(recorder.SessionId))!.Status;
        }

        Assert.Equal(SessionStatus.Completed, await Run(true, true));
        Assert.Equal(SessionStatus.PartiallyCompleted, await Run(true, false));
        Assert.Equal(SessionStatus.Failed, await Run(false, false));
        Assert.Equal(SessionStatus.Completed, await Run());
    }

    [Fact]
    public async Task UndoChange_RestoresSingleChange_AndUpdatesSessionStatus()
    {
        var handler = UseRecordingHandler();
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));
        await recorder.ApplyAsync(Change("a"), _ => Task.FromResult(OperationResult.Ok()));
        await recorder.ApplyAsync(Change("b"), _ => Task.FromResult(OperationResult.Ok()));
        await rollback.CompleteSessionAsync(recorder.SessionId);
        var changeB = _h.History.AllChanges.Single(c => c.Target == "b");

        var result = await rollback.UndoChangeAsync(changeB.Id);

        Assert.True(result.Success);
        Assert.Equal(["b"], handler.UndoneTargets);
        Assert.Equal(SessionStatus.PartiallyRolledBack, (await rollback.GetSessionAsync(recorder.SessionId))!.Status);
        Assert.Equal(OperationErrorKind.NotFound, (await rollback.UndoChangeAsync(Guid.NewGuid())).Error);
    }

    [Fact]
    public async Task SessionChanged_IsRaised_AndJournalIsWritten()
    {
        UseRecordingHandler();
        var rollback = _h.Get<IRollbackManager>();
        var raised = new List<Guid>();
        rollback.SessionChanged += (_, id) => raised.Add(id);

        var recorder = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));
        await recorder.ApplyAsync(Change("a"), _ => Task.FromResult(OperationResult.Ok()));
        await rollback.CompleteSessionAsync(recorder.SessionId);
        await rollback.RestoreSessionAsync(recorder.SessionId);

        Assert.Contains(recorder.SessionId, raised);
        Assert.Contains(_h.Journal.Entries, e => e.Message.Key == "Opt_Journal_SessionCompleted");
        Assert.Contains(_h.Journal.Entries, e => e.Message.Key == "Opt_Journal_SessionRestored");
    }

    [Fact]
    public async Task GetHistory_PurgesSessionsOlderThanRetention()
    {
        UseRecordingHandler();
        _h.Settings.Current.HistoryRetentionDays = 30;
        var rollback = _h.Get<IRollbackManager>();
        var old = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("old"));
        await rollback.CompleteSessionAsync(old.SessionId);
        _h.Clock.Advance(TimeSpan.FromDays(45));
        var recent = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("recent"));
        await rollback.CompleteSessionAsync(recent.SessionId);

        var history = await rollback.GetHistoryAsync();

        Assert.Single(history);
        Assert.Equal(recent.SessionId, history[0].Id);
    }

    [Fact]
    public async Task ModuleRollback_RestoresOnlyThatModulesChanges()
    {
        var rollback = _h.Get<IRollbackManager>();
        var power = _h.Get<IEnumerable<IOptimization>>().Single(o => o.Id == OptimizationIds.PowerPlan);
        _h.Power.ActiveId = Core.Models.SystemInfo.PowerScheme.PowerSaver;
        var recorder = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));
        var context = new OptimizationContext(null);
        await power.ApplyAsync(context, recorder);
        await rollback.CompleteSessionAsync(recorder.SessionId);
        Assert.Equal(Core.Models.SystemInfo.PowerScheme.Balanced, _h.Power.ActiveId);

        var result = await power.RollbackAsync(recorder.SessionId);

        Assert.Equal(1, result.Restored);
        Assert.Equal(Core.Models.SystemInfo.PowerScheme.PowerSaver, _h.Power.ActiveId);
    }

    [Fact]
    public async Task CompleteSession_AfterRestore_KeepsRolledBackStatus()
    {
        UseRecordingHandler();
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Gaming, TextRef.Literal("t"));
        await recorder.ApplyAsync(Change("a"), _ => Task.FromResult(OperationResult.Ok()));

        await rollback.RestoreSessionAsync(recorder.SessionId);
        await rollback.CompleteSessionAsync(recorder.SessionId);

        Assert.Equal(SessionStatus.RolledBack, (await rollback.GetSessionAsync(recorder.SessionId))!.Status);
    }
}
