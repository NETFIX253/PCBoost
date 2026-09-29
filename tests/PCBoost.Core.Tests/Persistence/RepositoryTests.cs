using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Settings;
using PCBoost.Persistence.Repositories;

namespace PCBoost.Core.Tests.Storage;

public sealed class RepositoryTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private DateTimeOffset Now => _db.Clock.UtcNow;

    [Fact]
    public async Task Gaming_sessions_round_trip_and_update()
    {
        var repository = new SqliteGamingSessionRepository(_db.Database);
        var session = new GamingSession
        {
            Id = Guid.NewGuid(),
            StartedAt = Now,
            Status = GamingSessionStatus.Active,
            GameId = "steam:570",
            GameName = "Dota 2",
            ProcessId = 4242,
            OptimizationSessionId = Guid.NewGuid(),
            Optimizations =
            [
                new ActiveGamingOptimization("power.plan", TextRef.Of("Game_Power"), true, TextRef.Of("Game_Power_Detail", "Performances élevées")),
                new ActiveGamingOptimization("priority", TextRef.Of("Game_Priority"), false, null),
            ],
        };

        await repository.SaveAsync(session);
        var loaded = await repository.GetAsync(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal(session.GameId, loaded.GameId);
        Assert.Equal(session.GameName, loaded.GameName);
        Assert.Equal(4242, loaded.ProcessId);
        Assert.Equal(session.OptimizationSessionId, loaded.OptimizationSessionId);
        Assert.Equal(2, loaded.Optimizations.Count);
        Assert.Equal("Game_Power", loaded.Optimizations[0].Label.Key);
        Assert.Equal("Performances élevées", loaded.Optimizations[0].Detail!.Args[0]);
        Assert.Null(loaded.Optimizations[1].Detail);
        Assert.Null(loaded.FrameStats);

        var frames = new FrameStats(144.2, 98.5, null, 6.93, 12.1, 8650, TimeSpan.FromMinutes(1));
        await repository.SaveAsync(session with { Status = GamingSessionStatus.Restored, EndedAt = Now.AddHours(1), FrameStats = frames });
        var updated = await repository.GetAsync(session.Id);

        Assert.Equal(GamingSessionStatus.Restored, updated!.Status);
        Assert.Equal(Now.AddHours(1), updated.EndedAt);
        Assert.Equal(frames, updated.FrameStats);
        Assert.Equal(1, await _db.ScalarAsync("SELECT COUNT(*) FROM gaming_sessions;"));
    }

    [Fact]
    public async Task Gaming_sessions_are_queried_by_status_and_recency()
    {
        var repository = new SqliteGamingSessionRepository(_db.Database);
        var sessions = Enumerable.Range(0, 4).Select(i => new GamingSession
        {
            Id = Guid.NewGuid(),
            StartedAt = Now.AddHours(-i),
            Status = i % 2 == 0 ? GamingSessionStatus.Active : GamingSessionStatus.Restored,
        }).ToList();
        foreach (var s in sessions) await repository.SaveAsync(s);

        var active = await repository.GetByStatusAsync(GamingSessionStatus.Active);
        var recent = await repository.GetRecentAsync(2);

        Assert.Equal(new[] { sessions[0].Id, sessions[2].Id }, active.Select(s => s.Id));
        Assert.Equal(new[] { sessions[0].Id, sessions[1].Id }, recent.Select(s => s.Id));
        Assert.Null(await repository.GetAsync(Guid.NewGuid()));
        Assert.Empty(await repository.GetRecentAsync(0));
    }

    [Fact]
    public async Task Scans_are_saved_and_returned_newest_first()
    {
        var repository = new SqliteScanHistoryRepository(_db.Database);
        var scans = Enumerable.Range(0, 3).Select(i => new ScanRecord(Guid.NewGuid(), Now.AddDays(-i), 60 + i, i, $"{{\"n\":{i}}}")).ToList();
        foreach (var s in scans) await repository.SaveAsync(s);

        var recent = await repository.GetRecentAsync(2);

        Assert.Equal(new[] { scans[0], scans[1] }, recent);
    }

    [Fact]
    public async Task Snapshots_are_inserted_in_batch_and_queried_by_range()
    {
        var repository = new SqlitePerformanceSnapshotRepository(_db.Database);
        var snapshots = Enumerable.Range(0, 100).Select(i => new PerformanceSnapshot(
            Now.AddMinutes(i), i, 50 + i / 10d,
            i % 2 == 0 ? i / 2d : null, null, i % 3 == 0 ? 55 : null, null, i % 5 == 0 ? 60 : null, null)).ToList();

        await repository.AddRangeAsync(snapshots);
        await repository.AddRangeAsync([]);
        var range = await repository.GetRangeAsync(Now.AddMinutes(10), Now.AddMinutes(19));

        Assert.Equal(100, await _db.ScalarAsync("SELECT COUNT(*) FROM snapshots;"));
        Assert.Equal(snapshots.Skip(10).Take(10), range);
    }

    [Fact]
    public async Task Snapshots_are_purged_before_the_cutoff()
    {
        var repository = new SqlitePerformanceSnapshotRepository(_db.Database);
        await repository.AddRangeAsync(Enumerable.Range(0, 10).Select(i => new PerformanceSnapshot(Now.AddDays(-i), 1, 1, null, null, null, null, null, null)).ToList());

        var removed = await repository.PurgeOlderThanAsync(Now.AddDays(-5).AddSeconds(-1));

        Assert.Equal(4, removed);
        Assert.Equal(6, (await repository.GetRangeAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue)).Count);
    }

    [Fact]
    public async Task Benchmarks_round_trip_with_frame_statistics()
    {
        var repository = new SqliteBenchmarkRepository(_db.Database);
        var before = new BenchmarkRun
        {
            Id = Guid.NewGuid(),
            Timestamp = Now,
            Phase = BenchmarkPhase.Before,
            Duration = TimeSpan.FromSeconds(60),
            Label = "Avant",
            GameName = "Jeu",
            CpuAveragePercent = 45.5,
            CpuMaxPercent = 90,
            GpuAveragePercent = null,
            RamAveragePercent = 62.25,
            DiskActiveAveragePercent = 3.5,
            Frames = new FrameStats(60, 45, 30, 16.7, 25, 3600, TimeSpan.FromSeconds(60)),
            SampleCount = 60,
        };
        var after = before with { Id = Guid.NewGuid(), Timestamp = Now.AddMinutes(10), Phase = BenchmarkPhase.After, PairedRunId = before.Id, Frames = FrameStats.Empty };

        await repository.SaveAsync(before);
        await repository.SaveAsync(after);
        var loaded = await repository.GetAsync(before.Id);
        var recent = await repository.GetRecentAsync(10);

        Assert.Equal(before, loaded);
        Assert.Equal(new[] { after.Id, before.Id }, recent.Select(r => r.Id));
        Assert.Equal(before.Id, recent[0].PairedRunId);
        Assert.False(recent[0].Frames.HasData);
        Assert.Null(await repository.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Key_value_store_round_trips_simple_and_complex_values()
    {
        var store = new SqliteKeyValueStore(_db.Database, _db.Clock, NullLogger<SqliteKeyValueStore>.Instance);
        var settings = new AppSettings { Language = "en", Theme = ThemePreference.Dark, HistoryRetentionDays = 30 };
        settings.Gaming.CustomGames.Add(new CustomGameEntry { Name = "Jeu", ExecutablePath = @"C:\Games\jeu.exe" });

        await store.SetAsync("settings", settings);
        await store.SetAsync("count", 3);
        await store.SetAsync("count", 4);
        var loaded = await store.GetAsync<AppSettings>("settings");

        Assert.Equal("en", loaded!.Language);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
        Assert.Equal(30, loaded.HistoryRetentionDays);
        Assert.Equal(@"C:\Games\jeu.exe", Assert.Single(loaded.Gaming.CustomGames).ExecutablePath);
        Assert.Equal(4, await store.GetAsync<int>("count"));
        Assert.Null(await store.GetAsync<string>("missing"));
        Assert.Equal(0, await store.GetAsync<int>("missing"));

        await store.RemoveAsync("count");
        Assert.Equal(0, await store.GetAsync<int>("count"));
        Assert.Contains("\"Theme\":\"Dark\"", await StringValueAsync("settings"));
    }

    [Fact]
    public async Task Key_value_store_treats_unreadable_values_as_missing()
    {
        var logger = new ListLogger<SqliteKeyValueStore>();
        var store = new SqliteKeyValueStore(_db.Database, _db.Clock, logger);
        await store.SetAsync("settings", new AppSettings());
        await _db.ExecuteAsync("UPDATE kv SET value = '{broken' WHERE key = 'settings';");

        Assert.Null(await store.GetAsync<AppSettings>("settings"));
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync<int>(" "));
    }

    [Fact]
    public async Task Activity_entries_round_trip_newest_first_and_are_purged()
    {
        var repository = new SqliteActivityLogRepository(_db.Database);
        var entries = Enumerable.Range(0, 5).Select(i => new ActivityLogEntry(
            Guid.NewGuid(), Now.AddDays(-i * 30), ActivityKind.Cleanup, TextRef.Of("Opt_Cleanup_Done", 1_500_000_000L, i), i == 0 ? "détail" : null)).ToList();
        foreach (var e in entries) await repository.AddAsync(e);

        var recent = await repository.GetRecentAsync(3);

        Assert.Equal(entries.Take(3).Select(e => e.Id), recent.Select(e => e.Id));
        Assert.Equal(ActivityKind.Cleanup, recent[0].Kind);
        Assert.Equal("détail", recent[0].Detail);
        Assert.Equal(new object[] { 1_500_000_000L, 0 }, recent[0].Message.Args);
        Assert.Equal(entries[0].Timestamp, recent[0].Timestamp);

        var removed = await repository.PurgeOlderThanAsync(Now.AddDays(-65));
        Assert.Equal(2, removed);
        Assert.Equal(3, (await repository.GetRecentAsync(100)).Count);
        Assert.Empty(await repository.GetRecentAsync(0));
    }

    [Fact]
    public async Task Literal_text_round_trips()
    {
        var repository = new SqliteActivityLogRepository(_db.Database);
        var entry = new ActivityLogEntry(Guid.NewGuid(), Now, ActivityKind.Info, TextRef.Literal("setup {0}.exe"), null);

        await repository.AddAsync(entry);
        var loaded = Assert.Single(await repository.GetRecentAsync(1));

        Assert.True(loaded.Message.IsLiteral);
        Assert.Equal("setup {0}.exe", loaded.Message.Args[0]);
    }

    private async Task<string> StringValueAsync(string key)
    {
        await using var connection = await _db.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM kv WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
