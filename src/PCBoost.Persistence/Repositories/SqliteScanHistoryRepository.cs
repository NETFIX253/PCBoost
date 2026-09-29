using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Models.Analysis;

namespace PCBoost.Persistence.Repositories;

public sealed class SqliteScanHistoryRepository : IScanHistoryRepository
{
    private readonly SqliteDatabase _database;

    public SqliteScanHistoryRepository(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task SaveAsync(ScanRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, """
                INSERT INTO scans (id, timestamp, score, finding_count, summary_json)
                VALUES ($id, $timestamp, $score, $findings, $summary)
                ON CONFLICT (id) DO UPDATE SET
                    timestamp = excluded.timestamp,
                    score = excluded.score,
                    finding_count = excluded.finding_count,
                    summary_json = excluded.summary_json;
                """);
            Sql.Add(command, "$id", Sql.ToText(record.Id));
            Sql.Add(command, "$timestamp", Sql.ToTicks(record.Timestamp));
            Sql.Add(command, "$score", record.Score);
            Sql.Add(command, "$findings", record.FindingCount);
            Sql.Add(command, "$summary", record.SummaryJson ?? string.Empty);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ScanRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return Task.FromResult<IReadOnlyList<ScanRecord>>([]);
        return _database.RunAsync<IReadOnlyList<ScanRecord>>(async (connection, token) =>
        {
            await using var command = Sql.Command(connection,
                "SELECT id, timestamp, score, finding_count, summary_json FROM scans ORDER BY timestamp DESC, rowid DESC LIMIT $limit;");
            Sql.Add(command, "$limit", limit);
            var list = new List<ScanRecord>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new ScanRecord(
                    Sql.GetGuid(reader, 0),
                    Sql.GetDate(reader, 1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetString(4)));
            }
            return list;
        }, cancellationToken);
    }
}
