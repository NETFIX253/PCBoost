using System.Collections.Concurrent;
using System.Text.Json;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.Optimization;

namespace PCBoost.TestUtilities;

public sealed class InMemoryOptimizationHistoryRepository : IOptimizationHistoryRepository
{
    private readonly ConcurrentDictionary<Guid, OptimizationSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, ChangeRecord> _changes = new();

    public IReadOnlyCollection<ChangeRecord> AllChanges => _changes.Values.ToList();

    /// <summary>Sessions avec leurs modifications, dans l'ordre de début.</summary>
    public IReadOnlyList<OptimizationSession> Sessions => _sessions.Values.OrderBy(s => s.StartedAt).Select(Hydrate).ToList();

    public Task CreateSessionAsync(OptimizationSession session, CancellationToken cancellationToken = default)
    {
        _sessions[session.Id] = session with { Changes = [] };
        return Task.CompletedTask;
    }

    public Task UpdateSessionAsync(OptimizationSession session, CancellationToken cancellationToken = default)
    {
        _sessions[session.Id] = session with { Changes = [] };
        return Task.CompletedTask;
    }

    public Task AddChangeAsync(ChangeRecord change, CancellationToken cancellationToken = default)
    {
        _changes[change.Id] = change;
        return Task.CompletedTask;
    }

    public Task UpdateChangeAsync(ChangeRecord change, CancellationToken cancellationToken = default)
    {
        _changes[change.Id] = change;
        return Task.CompletedTask;
    }

    public Task<OptimizationSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(_sessions.TryGetValue(sessionId, out var s) ? Hydrate(s) : null);

    public Task<IReadOnlyList<OptimizationSession>> GetRecentSessionsAsync(int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OptimizationSession>>(_sessions.Values.OrderByDescending(s => s.StartedAt).Take(limit).Select(Hydrate).ToList());

    public Task<IReadOnlyList<OptimizationSession>> GetSessionsByStatusAsync(IReadOnlyCollection<SessionStatus> statuses, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OptimizationSession>>(_sessions.Values.Where(s => statuses.Contains(s.Status)).Select(Hydrate).ToList());

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var old = _sessions.Values.Where(s => s.StartedAt < cutoff && s.Status != SessionStatus.InProgress).Select(s => s.Id).ToList();
        foreach (var id in old)
        {
            _sessions.TryRemove(id, out _);
            foreach (var c in _changes.Values.Where(c => c.SessionId == id).ToList()) _changes.TryRemove(c.Id, out _);
        }
        return Task.FromResult(old.Count);
    }

    private OptimizationSession Hydrate(OptimizationSession s)
        => s with { Changes = _changes.Values.Where(c => c.SessionId == s.Id).OrderBy(c => c.Sequence).ToList() };
}

public sealed class InMemoryGamingSessionRepository : IGamingSessionRepository
{
    private readonly ConcurrentDictionary<Guid, GamingSession> _items = new();
    public Task SaveAsync(GamingSession session, CancellationToken cancellationToken = default) { _items[session.Id] = session; return Task.CompletedTask; }
    public Task<GamingSession?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_items.GetValueOrDefault(id));
    public Task<IReadOnlyList<GamingSession>> GetByStatusAsync(GamingSessionStatus status, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GamingSession>>(_items.Values.Where(s => s.Status == status).ToList());
    public Task<IReadOnlyList<GamingSession>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GamingSession>>(_items.Values.OrderByDescending(s => s.StartedAt).Take(limit).ToList());
    public Task<IReadOnlyList<GamingSession>> GetByGameAsync(string gameId, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GamingSession>>(_items.Values.Where(s => s.GameId == gameId).OrderByDescending(s => s.StartedAt).Take(limit).ToList());
}

public sealed class InMemoryScanHistoryRepository : IScanHistoryRepository
{
    private readonly List<ScanRecord> _items = [];
    public Task SaveAsync(ScanRecord record, CancellationToken cancellationToken = default) { lock (_items) _items.Add(record); return Task.CompletedTask; }
    public Task<IReadOnlyList<ScanRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        lock (_items) return Task.FromResult<IReadOnlyList<ScanRecord>>(_items.OrderByDescending(r => r.Timestamp).Take(limit).ToList());
    }
}

public sealed class InMemoryPerformanceSnapshotRepository : IPerformanceSnapshotRepository
{
    private readonly List<PerformanceSnapshot> _items = [];
    public Task AddRangeAsync(IReadOnlyCollection<PerformanceSnapshot> snapshots, CancellationToken cancellationToken = default) { lock (_items) _items.AddRange(snapshots); return Task.CompletedTask; }
    public Task<IReadOnlyList<PerformanceSnapshot>> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        lock (_items) return Task.FromResult<IReadOnlyList<PerformanceSnapshot>>(_items.Where(s => s.Timestamp >= from && s.Timestamp <= to).OrderBy(s => s.Timestamp).ToList());
    }
    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        lock (_items) return Task.FromResult(_items.RemoveAll(s => s.Timestamp < cutoff));
    }
}

public sealed class InMemoryBenchmarkRepository : IBenchmarkRepository
{
    private readonly ConcurrentDictionary<Guid, BenchmarkRun> _items = new();
    public Task SaveAsync(BenchmarkRun run, CancellationToken cancellationToken = default) { _items[run.Id] = run; return Task.CompletedTask; }
    public Task<BenchmarkRun?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_items.GetValueOrDefault(id));
    public Task<IReadOnlyList<BenchmarkRun>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BenchmarkRun>>(_items.Values.OrderByDescending(r => r.Timestamp).Take(limit).ToList());
}

public sealed class InMemoryKeyValueStore : IKeyValueStore
{
    private readonly ConcurrentDictionary<string, string> _items = new();
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_items.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default);
    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) { _items[key] = JsonSerializer.Serialize(value); return Task.CompletedTask; }
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) { _items.TryRemove(key, out _); return Task.CompletedTask; }
}

public sealed class InMemoryActivityLogRepository : IActivityLogRepository
{
    private readonly List<ActivityLogEntry> _items = [];
    public IReadOnlyList<ActivityLogEntry> Entries { get { lock (_items) return _items.ToList(); } }
    public Task AddAsync(ActivityLogEntry entry, CancellationToken cancellationToken = default) { lock (_items) _items.Add(entry); return Task.CompletedTask; }
    public Task<IReadOnlyList<ActivityLogEntry>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        lock (_items) return Task.FromResult<IReadOnlyList<ActivityLogEntry>>(_items.OrderByDescending(e => e.Timestamp).Take(limit).ToList());
    }
    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        lock (_items) return Task.FromResult(_items.RemoveAll(e => e.Timestamp < cutoff));
    }
}
