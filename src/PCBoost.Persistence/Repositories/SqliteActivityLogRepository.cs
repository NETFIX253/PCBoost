using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Persistence.Serialization;

namespace PCBoost.Persistence.Repositories;

public sealed class SqliteActivityLogRepository : IActivityLogRepository
{
    private readonly SqliteDatabase _database;

    public SqliteActivityLogRepository(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task AddAsync(ActivityLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, """
                INSERT INTO activity (id, timestamp, kind, message_json, detail)
                VALUES ($id, $timestamp, $kind, $message, $detail)
                ON CONFLICT (id) DO UPDATE SET
                    timestamp = excluded.timestamp,
                    kind = excluded.kind,
                    message_json = excluded.message_json,
                    detail = excluded.detail;
                """);
            Sql.Add(command, "$id", Sql.ToText(entry.Id));
            Sql.Add(command, "$timestamp", Sql.ToTicks(entry.Timestamp));
            Sql.Add(command, "$kind", Sql.ToText(entry.Kind));
            Sql.Add(command, "$message", PersistenceJson.Serialize(entry.Message));
            Sql.Add(command, "$detail", entry.Detail);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>Entrées les plus récentes en premier.</summary>
    public Task<IReadOnlyList<ActivityLogEntry>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return Task.FromResult<IReadOnlyList<ActivityLogEntry>>([]);
        return _database.RunAsync<IReadOnlyList<ActivityLogEntry>>(async (connection, token) =>
        {
            await using var command = Sql.Command(connection,
                "SELECT id, timestamp, kind, message_json, detail FROM activity ORDER BY timestamp DESC, rowid DESC LIMIT $limit;");
            Sql.Add(command, "$limit", limit);
            var list = new List<ActivityLogEntry>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var detail = Sql.GetNullableString(reader, 4);
                list.Add(new ActivityLogEntry(
                    Sql.GetGuid(reader, 0),
                    Sql.GetDate(reader, 1),
                    Sql.ParseEnum(reader.GetString(2), ActivityKind.Info),
                    PersistenceJson.TryDeserialize<TextRef>(reader.GetString(3)) ?? TextRef.Literal(detail ?? string.Empty),
                    detail));
            }
            return list;
        }, cancellationToken);
    }

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        => _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, "DELETE FROM activity WHERE timestamp < $cutoff;");
            Sql.Add(command, "$cutoff", Sql.ToTicks(cutoff));
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
}
