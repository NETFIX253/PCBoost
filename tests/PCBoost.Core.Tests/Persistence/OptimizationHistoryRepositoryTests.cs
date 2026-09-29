using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Persistence.Repositories;

namespace PCBoost.Core.Tests.Storage;

public sealed class OptimizationHistoryRepositoryTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly SqliteOptimizationHistoryRepository _repository;

    public OptimizationHistoryRepositoryTests() => _repository = new SqliteOptimizationHistoryRepository(_db.Database);

    public void Dispose() => _db.Dispose();

    private DateTimeOffset Now => _db.Clock.UtcNow;

    private OptimizationSession Session(SessionStatus status = SessionStatus.InProgress, DateTimeOffset? startedAt = null, DateTimeOffset? completedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = SessionType.OneClick,
        StartedAt = startedAt ?? Now,
        CompletedAt = completedAt,
        Status = status,
        Title = TextRef.Of("Opt_Session_OneClick"),
        ProfileId = PerformanceProfile.GamingId,
    };

    private ChangeRecord Change(Guid sessionId, int sequence, ChangeStatus status = ChangeStatus.Pending) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        OptimizationId = "startup.disable",
        Kind = "registry.value",
        Target = $@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run : App{sequence}",
        Description = TextRef.Of("Opt_Startup_Disable", $"App{sequence}", sequence),
        BeforeState = "{\"Existed\":true}",
        Reversible = true,
        Status = status,
        RecordedAt = Now.AddSeconds(sequence),
        Sequence = sequence,
    };

    [Fact]
    public async Task Session_round_trips_all_fields()
    {
        var session = Session() with { BytesFreed = 123_456_789_012, RequiresRestart = true, StartedAt = new DateTimeOffset(2026, 9, 28, 14, 30, 0, TimeSpan.FromHours(3)) };

        await _repository.CreateSessionAsync(session);
        var loaded = await _repository.GetSessionAsync(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal(session.Id, loaded.Id);
        Assert.Equal(session.Type, loaded.Type);
        Assert.Equal(session.Status, loaded.Status);
        Assert.Equal(session.StartedAt, loaded.StartedAt);
        Assert.Equal(TimeSpan.Zero, loaded.StartedAt.Offset);
        Assert.Null(loaded.CompletedAt);
        Assert.Equal("Opt_Session_OneClick", loaded.Title?.Key);
        Assert.Equal(PerformanceProfile.GamingId, loaded.ProfileId);
        Assert.Equal(123_456_789_012, loaded.BytesFreed);
        Assert.True(loaded.RequiresRestart);
        Assert.Empty(loaded.Changes);
    }

    [Fact]
    public async Task Unknown_session_returns_null()
        => Assert.Null(await _repository.GetSessionAsync(Guid.NewGuid()));

    [Fact]
    public async Task Changes_are_hydrated_in_sequence_order()
    {
        var session = Session();
        await _repository.CreateSessionAsync(session);
        await _repository.AddChangeAsync(Change(session.Id, 3));
        await _repository.AddChangeAsync(Change(session.Id, 1));
        await _repository.AddChangeAsync(Change(session.Id, 2));

        var loaded = await _repository.GetSessionAsync(session.Id);

        Assert.Equal(new[] { 1, 2, 3 }, loaded!.Changes.Select(c => c.Sequence));
    }

    [Fact]
    public async Task Pending_change_is_updated_to_applied_without_duplicating_it()
    {
        var session = Session();
        await _repository.CreateSessionAsync(session);
        var pending = Change(session.Id, 1);
        await _repository.AddChangeAsync(pending);

        var applied = pending with { Status = ChangeStatus.Applied, AfterState = "{\"Value\":0}" };
        await _repository.UpdateChangeAsync(applied);
        var loaded = Assert.Single((await _repository.GetSessionAsync(session.Id))!.Changes);

        Assert.Equal(ChangeStatus.Applied, loaded.Status);
        Assert.Equal("{\"Value\":0}", loaded.AfterState);
        Assert.Equal(pending.BeforeState, loaded.BeforeState);
        Assert.Equal(1, loaded.Sequence);
        Assert.Equal(1, await _db.ScalarAsync("SELECT COUNT(*) FROM changes;"));
    }

    [Fact]
    public async Task Change_round_trips_all_fields()
    {
        var session = Session();
        await _repository.CreateSessionAsync(session);
        var change = Change(session.Id, 7, ChangeStatus.RolledBack) with
        {
            AfterState = "after",
            RolledBackAt = Now.AddMinutes(5),
            ErrorDetail = "Win32Exception: 5",
            Reversible = false,
        };

        await _repository.AddChangeAsync(change);
        var loaded = Assert.Single((await _repository.GetSessionAsync(session.Id))!.Changes);

        Assert.Equal(change.Id, loaded.Id);
        Assert.Equal(change.SessionId, loaded.SessionId);
        Assert.Equal(change.OptimizationId, loaded.OptimizationId);
        Assert.Equal(change.Kind, loaded.Kind);
        Assert.Equal(change.Target, loaded.Target);
        Assert.Equal(change.BeforeState, loaded.BeforeState);
        Assert.Equal(change.AfterState, loaded.AfterState);
        Assert.False(loaded.Reversible);
        Assert.Equal(ChangeStatus.RolledBack, loaded.Status);
        Assert.Equal(change.RecordedAt, loaded.RecordedAt);
        Assert.Equal(change.RolledBackAt, loaded.RolledBackAt);
        Assert.Equal(change.ErrorDetail, loaded.ErrorDetail);
        Assert.Equal("Opt_Startup_Disable", loaded.Description.Key);
        Assert.Equal(new object[] { "App7", 7 }, loaded.Description.Args);
    }

    [Fact]
    public async Task Change_descriptions_keep_numeric_argument_types()
    {
        var session = Session();
        await _repository.CreateSessionAsync(session);
        var description = TextRef.Of("Opt_Freed", 42, 5_000_000_000L, 3.5, 2.0, 7L, "texte", true, 1.5f, 12.34m,
            TimeSpan.FromMinutes(90), TextRef.Of("Nested", 1));
        var change = Change(session.Id, 1) with { Description = description };

        await _repository.AddChangeAsync(change);
        var args = Assert.Single((await _repository.GetSessionAsync(session.Id))!.Changes).Description.Args;

        Assert.IsType<int>(args[0]);
        Assert.Equal(42, args[0]);
        Assert.IsType<long>(args[1]);
        Assert.Equal(5_000_000_000L, args[1]);
        Assert.IsType<double>(args[2]);
        Assert.Equal(3.5, args[2]);
        Assert.IsType<double>(args[3]);
        Assert.Equal(2.0, args[3]);
        Assert.IsType<long>(args[4]);
        Assert.Equal(7L, args[4]);
        Assert.Equal("texte", args[5]);
        Assert.Equal(true, args[6]);
        Assert.Equal(1.5f, args[7]);
        Assert.Equal(12.34m, args[8]);
        Assert.Equal(TimeSpan.FromMinutes(90), args[9]);
        var nested = Assert.IsType<TextRef>(args[10]);
        Assert.Equal("Nested", nested.Key);
        Assert.Equal(1, Assert.Single(nested.Args));
    }

    [Fact]
    public async Task Session_update_changes_status_and_completion_but_keeps_changes()
    {
        var session = Session();
        await _repository.CreateSessionAsync(session);
        await _repository.AddChangeAsync(Change(session.Id, 1, ChangeStatus.Applied));

        await _repository.UpdateSessionAsync(session with
        {
            Status = SessionStatus.Completed,
            CompletedAt = Now.AddMinutes(1),
            BytesFreed = 1024,
            Title = null,
        });
        var loaded = await _repository.GetSessionAsync(session.Id);

        Assert.Equal(SessionStatus.Completed, loaded!.Status);
        Assert.Equal(Now.AddMinutes(1), loaded.CompletedAt);
        Assert.Equal(1024, loaded.BytesFreed);
        Assert.Null(loaded.Title);
        Assert.Single(loaded.Changes);
        Assert.Equal(1, loaded.ReversibleChangeCount);
    }

    [Fact]
    public async Task Recent_sessions_are_newest_first_limited_and_hydrated()
    {
        var sessions = Enumerable.Range(0, 5).Select(i => Session(SessionStatus.Completed, Now.AddHours(-i))).ToList();
        foreach (var s in sessions)
        {
            await _repository.CreateSessionAsync(s);
            await _repository.AddChangeAsync(Change(s.Id, 2));
            await _repository.AddChangeAsync(Change(s.Id, 1));
        }

        var recent = await _repository.GetRecentSessionsAsync(3);

        Assert.Equal(sessions.Take(3).Select(s => s.Id), recent.Select(s => s.Id));
        Assert.All(recent, s => Assert.Equal(new[] { 1, 2 }, s.Changes.Select(c => c.Sequence)));
        Assert.All(recent, s => Assert.All(s.Changes, c => Assert.Equal(s.Id, c.SessionId)));
        Assert.Empty(await _repository.GetRecentSessionsAsync(0));
    }

    [Fact]
    public async Task Sessions_are_filtered_by_status()
    {
        var inProgress = Session(SessionStatus.InProgress);
        var interrupted = Session(SessionStatus.Interrupted);
        var completed = Session(SessionStatus.Completed);
        foreach (var s in new[] { inProgress, interrupted, completed }) await _repository.CreateSessionAsync(s);
        await _repository.AddChangeAsync(Change(interrupted.Id, 1));

        var found = await _repository.GetSessionsByStatusAsync([SessionStatus.InProgress, SessionStatus.Interrupted]);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, s => s.Id == inProgress.Id);
        Assert.Single(found.Single(s => s.Id == interrupted.Id).Changes);
        Assert.DoesNotContain(found, s => s.Id == completed.Id);
        Assert.Empty(await _repository.GetSessionsByStatusAsync([]));
    }

    [Fact]
    public async Task Purge_removes_old_finished_sessions_but_keeps_in_progress_and_interrupted()
    {
        var old = Now.AddDays(-120);
        var oldCompleted = Session(SessionStatus.Completed, old, old.AddMinutes(2));
        var oldRolledBack = Session(SessionStatus.RolledBack, old);
        var oldInProgress = Session(SessionStatus.InProgress, old);
        var oldInterrupted = Session(SessionStatus.Interrupted, old);
        var recentCompleted = Session(SessionStatus.Completed, Now.AddDays(-1), Now.AddDays(-1));
        var startedOldFinishedRecently = Session(SessionStatus.Completed, old, Now.AddDays(-2));
        var all = new[] { oldCompleted, oldRolledBack, oldInProgress, oldInterrupted, recentCompleted, startedOldFinishedRecently };
        foreach (var s in all)
        {
            await _repository.CreateSessionAsync(s);
            await _repository.AddChangeAsync(Change(s.Id, 1));
        }

        var removed = await _repository.PurgeOlderThanAsync(Now.AddDays(-90));

        Assert.Equal(2, removed);
        Assert.Null(await _repository.GetSessionAsync(oldCompleted.Id));
        Assert.Null(await _repository.GetSessionAsync(oldRolledBack.Id));
        Assert.Single((await _repository.GetSessionAsync(oldInProgress.Id))!.Changes);
        Assert.Single((await _repository.GetSessionAsync(oldInterrupted.Id))!.Changes);
        Assert.NotNull(await _repository.GetSessionAsync(recentCompleted.Id));
        Assert.NotNull(await _repository.GetSessionAsync(startedOldFinishedRecently.Id));
        Assert.Equal(4, await _db.ScalarAsync("SELECT COUNT(*) FROM changes;"));
    }

    [Fact]
    public async Task Change_for_an_unknown_session_is_rejected_by_the_foreign_key()
        => await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _repository.AddChangeAsync(Change(Guid.NewGuid(), 1)));

    [Fact]
    public async Task Unreadable_description_falls_back_to_the_target_text()
    {
        var session = Session();
        await _repository.CreateSessionAsync(session);
        var change = Change(session.Id, 1);
        await _repository.AddChangeAsync(change);
        await _db.ExecuteAsync("UPDATE changes SET description_json = '{broken';");

        var loaded = Assert.Single((await _repository.GetSessionAsync(session.Id))!.Changes);

        Assert.True(loaded.Description.IsLiteral);
        Assert.Equal(change.Target, loaded.Description.Args[0]);
    }
}
