using Microsoft.Data.Sqlite;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Persistence.Serialization;

namespace PCBoost.Persistence.Repositories;

/// <summary>
/// Journal de restauration (sessions + modifications write-ahead). Les écritures sont des « upserts »
/// (<c>ON CONFLICT DO UPDATE</c>, jamais <c>REPLACE</c> qui supprimerait en cascade les modifications d'une session).
/// <see cref="CreateSessionAsync"/>/<see cref="UpdateSessionAsync"/> n'écrivent que la session : les modifications passent par
/// <see cref="AddChangeAsync"/>/<see cref="UpdateChangeAsync"/>.
/// </summary>
public sealed class SqliteOptimizationHistoryRepository : IOptimizationHistoryRepository
{
    private const string SessionColumns = "id, type, status, started_at, completed_at, title_json, profile_id, bytes_freed, requires_restart";
    private const string ChangeColumns = "id, session_id, sequence, optimization_id, kind, target, description_json, before_state, after_state, reversible, status, recorded_at, rolled_back_at, error_detail";
    private const string ChangeOrder = "sequence, recorded_at, rowid";

    /// <summary>Sessions jamais purgées : une restauration peut encore être nécessaire.</summary>
    private static readonly string[] PurgeProtectedStatuses = [nameof(SessionStatus.InProgress), nameof(SessionStatus.Interrupted)];

    private readonly SqliteDatabase _database;

    public SqliteOptimizationHistoryRepository(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task CreateSessionAsync(OptimizationSession session, CancellationToken cancellationToken = default)
        => UpsertSessionAsync(session, cancellationToken);

    public Task UpdateSessionAsync(OptimizationSession session, CancellationToken cancellationToken = default)
        => UpsertSessionAsync(session, cancellationToken);

    public Task AddChangeAsync(ChangeRecord change, CancellationToken cancellationToken = default)
        => UpsertChangeAsync(change, cancellationToken);

    public Task UpdateChangeAsync(ChangeRecord change, CancellationToken cancellationToken = default)
        => UpsertChangeAsync(change, cancellationToken);

    public Task<OptimizationSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _database.RunAsync(async (connection, token) =>
        {
            OptimizationSession? session = null;
            await using (var command = Sql.Command(connection, $"SELECT {SessionColumns} FROM sessions WHERE id = $id;"))
            {
                Sql.Add(command, "$id", Sql.ToText(sessionId));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false)) session = ReadSession(reader);
            }
            if (session is null) return null;

            var changes = await LoadChangesAsync(connection, [session.Id], token).ConfigureAwait(false);
            return session with { Changes = changes.TryGetValue(session.Id, out var list) ? list : [] };
        }, cancellationToken);

    public Task<IReadOnlyList<OptimizationSession>> GetRecentSessionsAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return Task.FromResult<IReadOnlyList<OptimizationSession>>([]);
        return _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, $"SELECT {SessionColumns} FROM sessions ORDER BY started_at DESC, rowid DESC LIMIT $limit;");
            Sql.Add(command, "$limit", limit);
            return await ReadSessionsWithChangesAsync(connection, command, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<OptimizationSession>> GetSessionsByStatusAsync(IReadOnlyCollection<SessionStatus> statuses, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        if (statuses.Count == 0) return Task.FromResult<IReadOnlyList<OptimizationSession>>([]);
        return _database.RunAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            var inClause = Sql.InClause(command, "s", statuses.Distinct().Select(s => Sql.ToText(s)));
            command.CommandText = $"SELECT {SessionColumns} FROM sessions WHERE status IN {inClause} ORDER BY started_at DESC, rowid DESC;";
            return await ReadSessionsWithChangesAsync(connection, command, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>
    /// Supprime les sessions terminées (date de fin, ou de début à défaut, antérieure à <paramref name="cutoff"/>) et leurs
    /// modifications. Les sessions en cours ou interrompues sont toujours conservées.
    /// </summary>
    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        => _database.RunAsync(async (connection, token) =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);

            await using (var deleteChanges = Sql.Command(connection, string.Empty, transaction))
            {
                var protectedClause = Sql.InClause(deleteChanges, "p", PurgeProtectedStatuses);
                deleteChanges.CommandText = $"""
                    DELETE FROM changes WHERE session_id IN (
                        SELECT id FROM sessions
                        WHERE COALESCE(completed_at, started_at) < $cutoff AND status NOT IN {protectedClause});
                    """;
                Sql.Add(deleteChanges, "$cutoff", Sql.ToTicks(cutoff));
                await deleteChanges.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            int deleted;
            await using (var deleteSessions = Sql.Command(connection, string.Empty, transaction))
            {
                var protectedClause = Sql.InClause(deleteSessions, "p", PurgeProtectedStatuses);
                deleteSessions.CommandText = $"DELETE FROM sessions WHERE COALESCE(completed_at, started_at) < $cutoff AND status NOT IN {protectedClause};";
                Sql.Add(deleteSessions, "$cutoff", Sql.ToTicks(cutoff));
                deleted = await deleteSessions.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return deleted;
        }, cancellationToken);

    private Task UpsertSessionAsync(OptimizationSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, """
                INSERT INTO sessions (id, type, status, started_at, completed_at, title_json, profile_id, bytes_freed, requires_restart)
                VALUES ($id, $type, $status, $started, $completed, $title, $profile, $bytes, $restart)
                ON CONFLICT (id) DO UPDATE SET
                    type = excluded.type,
                    status = excluded.status,
                    started_at = excluded.started_at,
                    completed_at = excluded.completed_at,
                    title_json = excluded.title_json,
                    profile_id = excluded.profile_id,
                    bytes_freed = excluded.bytes_freed,
                    requires_restart = excluded.requires_restart;
                """);
            Sql.Add(command, "$id", Sql.ToText(session.Id));
            Sql.Add(command, "$type", Sql.ToText(session.Type));
            Sql.Add(command, "$status", Sql.ToText(session.Status));
            Sql.Add(command, "$started", Sql.ToTicks(session.StartedAt));
            Sql.Add(command, "$completed", Sql.ToTicks(session.CompletedAt));
            Sql.Add(command, "$title", session.Title is null ? null : PersistenceJson.Serialize(session.Title));
            Sql.Add(command, "$profile", session.ProfileId);
            Sql.Add(command, "$bytes", session.BytesFreed);
            Sql.Add(command, "$restart", session.RequiresRestart ? 1 : 0);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task UpsertChangeAsync(ChangeRecord change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, """
                INSERT INTO changes (id, session_id, sequence, optimization_id, kind, target, description_json, before_state, after_state,
                                     reversible, status, recorded_at, rolled_back_at, error_detail)
                VALUES ($id, $session, $sequence, $optimization, $kind, $target, $description, $before, $after,
                        $reversible, $status, $recorded, $rolledBack, $error)
                ON CONFLICT (id) DO UPDATE SET
                    session_id = excluded.session_id,
                    sequence = excluded.sequence,
                    optimization_id = excluded.optimization_id,
                    kind = excluded.kind,
                    target = excluded.target,
                    description_json = excluded.description_json,
                    before_state = excluded.before_state,
                    after_state = excluded.after_state,
                    reversible = excluded.reversible,
                    status = excluded.status,
                    recorded_at = excluded.recorded_at,
                    rolled_back_at = excluded.rolled_back_at,
                    error_detail = excluded.error_detail;
                """);
            Sql.Add(command, "$id", Sql.ToText(change.Id));
            Sql.Add(command, "$session", Sql.ToText(change.SessionId));
            Sql.Add(command, "$sequence", change.Sequence);
            Sql.Add(command, "$optimization", change.OptimizationId);
            Sql.Add(command, "$kind", change.Kind);
            Sql.Add(command, "$target", change.Target);
            Sql.Add(command, "$description", PersistenceJson.Serialize(change.Description));
            Sql.Add(command, "$before", change.BeforeState);
            Sql.Add(command, "$after", change.AfterState);
            Sql.Add(command, "$reversible", change.Reversible ? 1 : 0);
            Sql.Add(command, "$status", Sql.ToText(change.Status));
            Sql.Add(command, "$recorded", Sql.ToTicks(change.RecordedAt));
            Sql.Add(command, "$rolledBack", Sql.ToTicks(change.RolledBackAt));
            Sql.Add(command, "$error", change.ErrorDetail);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static async Task<IReadOnlyList<OptimizationSession>> ReadSessionsWithChangesAsync(SqliteConnection connection, SqliteCommand sessionQuery, CancellationToken cancellationToken)
    {
        var sessions = new List<OptimizationSession>();
        await using (var reader = await sessionQuery.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                sessions.Add(ReadSession(reader));
        }
        if (sessions.Count == 0) return sessions;

        var changes = await LoadChangesAsync(connection, sessions.Select(s => s.Id).ToList(), cancellationToken).ConfigureAwait(false);
        return sessions.Select(s => s with { Changes = changes.TryGetValue(s.Id, out var list) ? list : [] }).ToList();
    }

    private static async Task<Dictionary<Guid, List<ChangeRecord>>> LoadChangesAsync(SqliteConnection connection, IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<ChangeRecord>>();
        await using var command = connection.CreateCommand();
        var inClause = Sql.InClause(command, "id", sessionIds.Select(id => Sql.ToText(id)));
        command.CommandText = $"SELECT {ChangeColumns} FROM changes WHERE session_id IN {inClause} ORDER BY session_id, {ChangeOrder};";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var change = ReadChange(reader);
            if (!result.TryGetValue(change.SessionId, out var list))
                result[change.SessionId] = list = [];
            list.Add(change);
        }
        return result;
    }

    private static OptimizationSession ReadSession(SqliteDataReader reader) => new()
    {
        Id = Sql.GetGuid(reader, 0),
        Type = Sql.ParseEnum(reader.GetString(1), SessionType.Manual),
        // Statut inconnu (base écrite par une version plus récente) : visible dans l'historique, restauration manuelle possible.
        Status = Sql.ParseEnum(reader.GetString(2), SessionStatus.Completed),
        StartedAt = Sql.GetDate(reader, 3),
        CompletedAt = Sql.GetNullableDate(reader, 4),
        Title = PersistenceJson.TryDeserialize<TextRef>(Sql.GetNullableString(reader, 5)),
        ProfileId = Sql.GetNullableString(reader, 6),
        BytesFreed = reader.GetInt64(7),
        RequiresRestart = reader.GetInt64(8) != 0,
    };

    private static ChangeRecord ReadChange(SqliteDataReader reader)
    {
        var target = reader.GetString(5);
        return new ChangeRecord
        {
            Id = Sql.GetGuid(reader, 0),
            SessionId = Sql.GetGuid(reader, 1),
            Sequence = reader.GetInt32(2),
            OptimizationId = reader.GetString(3),
            Kind = reader.GetString(4),
            Target = target,
            Description = PersistenceJson.TryDeserialize<TextRef>(reader.GetString(6)) ?? TextRef.Literal(target),
            BeforeState = Sql.GetNullableString(reader, 7),
            AfterState = Sql.GetNullableString(reader, 8),
            Reversible = reader.GetInt64(9) != 0,
            Status = Sql.ParseEnum(reader.GetString(10), ChangeStatus.Applied),
            RecordedAt = Sql.GetDate(reader, 11),
            RolledBackAt = Sql.GetNullableDate(reader, 12),
            ErrorDetail = Sql.GetNullableString(reader, 13),
        };
    }
}
