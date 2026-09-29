using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.Optimization;

namespace PCBoost.Core.Abstractions.Persistence;

/// <summary>Journal de restauration : sessions et modifications (write-ahead).</summary>
public interface IOptimizationHistoryRepository
{
    Task CreateSessionAsync(OptimizationSession session, CancellationToken cancellationToken = default);

    Task UpdateSessionAsync(OptimizationSession session, CancellationToken cancellationToken = default);

    Task AddChangeAsync(ChangeRecord change, CancellationToken cancellationToken = default);

    Task UpdateChangeAsync(ChangeRecord change, CancellationToken cancellationToken = default);

    /// <summary>Session avec ses modifications (ordre de séquence).</summary>
    Task<OptimizationSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OptimizationSession>> GetRecentSessionsAsync(int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OptimizationSession>> GetSessionsByStatusAsync(IReadOnlyCollection<SessionStatus> statuses, CancellationToken cancellationToken = default);

    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}

public interface IGamingSessionRepository
{
    Task SaveAsync(GamingSession session, CancellationToken cancellationToken = default);

    Task<GamingSession?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GamingSession>> GetByStatusAsync(GamingSessionStatus status, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GamingSession>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);
}

public interface IScanHistoryRepository
{
    Task SaveAsync(ScanRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScanRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);
}

public interface IPerformanceSnapshotRepository
{
    Task AddRangeAsync(IReadOnlyCollection<PerformanceSnapshot> snapshots, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PerformanceSnapshot>> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}

public interface IBenchmarkRepository
{
    Task SaveAsync(BenchmarkRun run, CancellationToken cancellationToken = default);

    Task<BenchmarkRun?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BenchmarkRun>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>Stockage clé/valeur JSON (préférences, profil actif, profil personnalisé…).</summary>
public interface IKeyValueStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

public interface IActivityLogRepository
{
    Task AddAsync(ActivityLogEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivityLogEntry>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);

    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}

/// <summary>Initialisation et migrations du stockage local.</summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    int CurrentSchemaVersion { get; }
}
