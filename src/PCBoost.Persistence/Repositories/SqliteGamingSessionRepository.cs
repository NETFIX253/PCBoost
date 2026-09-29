using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Models.Gaming;
using PCBoost.Persistence.Serialization;

namespace PCBoost.Persistence.Repositories;

public sealed class SqliteGamingSessionRepository : IGamingSessionRepository
{
    private const string Columns = "id, status, started_at, ended_at, game_id, game_name, process_id, optimization_session_id, optimizations_json, frame_stats_json";

    private readonly SqliteDatabase _database;

    public SqliteGamingSessionRepository(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task SaveAsync(GamingSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, """
                INSERT INTO gaming_sessions (id, status, started_at, ended_at, game_id, game_name, process_id, optimization_session_id, optimizations_json, frame_stats_json)
                VALUES ($id, $status, $started, $ended, $gameId, $gameName, $pid, $optSession, $optimizations, $frames)
                ON CONFLICT (id) DO UPDATE SET
                    status = excluded.status,
                    started_at = excluded.started_at,
                    ended_at = excluded.ended_at,
                    game_id = excluded.game_id,
                    game_name = excluded.game_name,
                    process_id = excluded.process_id,
                    optimization_session_id = excluded.optimization_session_id,
                    optimizations_json = excluded.optimizations_json,
                    frame_stats_json = excluded.frame_stats_json;
                """);
            Sql.Add(command, "$id", Sql.ToText(session.Id));
            Sql.Add(command, "$status", Sql.ToText(session.Status));
            Sql.Add(command, "$started", Sql.ToTicks(session.StartedAt));
            Sql.Add(command, "$ended", Sql.ToTicks(session.EndedAt));
            Sql.Add(command, "$gameId", session.GameId);
            Sql.Add(command, "$gameName", session.GameName);
            Sql.Add(command, "$pid", session.ProcessId);
            Sql.Add(command, "$optSession", Sql.ToText(session.OptimizationSessionId));
            Sql.Add(command, "$optimizations", PersistenceJson.Serialize(session.Optimizations ?? []));
            Sql.Add(command, "$frames", session.FrameStats is null ? null : PersistenceJson.Serialize(session.FrameStats));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<GamingSession?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, $"SELECT {Columns} FROM gaming_sessions WHERE id = $id;");
            Sql.Add(command, "$id", Sql.ToText(id));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false) ? Read(reader) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<GamingSession>> GetByStatusAsync(GamingSessionStatus status, CancellationToken cancellationToken = default)
        => QueryAsync($"SELECT {Columns} FROM gaming_sessions WHERE status = $status ORDER BY started_at DESC, rowid DESC;",
            command => Sql.Add(command, "$status", Sql.ToText(status)), cancellationToken);

    public Task<IReadOnlyList<GamingSession>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return Task.FromResult<IReadOnlyList<GamingSession>>([]);
        return QueryAsync($"SELECT {Columns} FROM gaming_sessions ORDER BY started_at DESC, rowid DESC LIMIT $limit;",
            command => Sql.Add(command, "$limit", limit), cancellationToken);
    }

    private Task<IReadOnlyList<GamingSession>> QueryAsync(string sql, Action<Microsoft.Data.Sqlite.SqliteCommand> bind, CancellationToken cancellationToken)
        => _database.RunAsync<IReadOnlyList<GamingSession>>(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, sql);
            bind(command);
            var list = new List<GamingSession>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) list.Add(Read(reader));
            return list;
        }, cancellationToken);

    private static GamingSession Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        Id = Sql.GetGuid(reader, 0),
        Status = Sql.ParseEnum(reader.GetString(1), GamingSessionStatus.Interrupted),
        StartedAt = Sql.GetDate(reader, 2),
        EndedAt = Sql.GetNullableDate(reader, 3),
        GameId = Sql.GetNullableString(reader, 4),
        GameName = Sql.GetNullableString(reader, 5),
        ProcessId = Sql.GetNullableInt32(reader, 6),
        OptimizationSessionId = Sql.GetNullableGuid(reader, 7),
        Optimizations = PersistenceJson.TryDeserialize<List<ActiveGamingOptimization>>(reader.GetString(8)) ?? [],
        FrameStats = PersistenceJson.TryDeserialize<FrameStats>(Sql.GetNullableString(reader, 9)),
    };
}
