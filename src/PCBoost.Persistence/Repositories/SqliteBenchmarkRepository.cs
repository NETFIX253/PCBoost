using Microsoft.Data.Sqlite;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Models.Gaming;
using PCBoost.Persistence.Serialization;

namespace PCBoost.Persistence.Repositories;

/// <summary>Benchmarks : colonnes de filtrage (id, date, phase, appariement) + mesures complètes en JSON.</summary>
public sealed class SqliteBenchmarkRepository : IBenchmarkRepository
{
    private readonly SqliteDatabase _database;

    public SqliteBenchmarkRepository(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task SaveAsync(BenchmarkRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        return _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, """
                INSERT INTO benchmarks (id, timestamp, phase, paired_run_id, data_json)
                VALUES ($id, $timestamp, $phase, $paired, $data)
                ON CONFLICT (id) DO UPDATE SET
                    timestamp = excluded.timestamp,
                    phase = excluded.phase,
                    paired_run_id = excluded.paired_run_id,
                    data_json = excluded.data_json;
                """);
            Sql.Add(command, "$id", Sql.ToText(run.Id));
            Sql.Add(command, "$timestamp", Sql.ToTicks(run.Timestamp));
            Sql.Add(command, "$phase", Sql.ToText(run.Phase));
            Sql.Add(command, "$paired", Sql.ToText(run.PairedRunId));
            Sql.Add(command, "$data", PersistenceJson.Serialize(run));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<BenchmarkRun?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, "SELECT data_json FROM benchmarks WHERE id = $id;");
            Sql.Add(command, "$id", Sql.ToText(id));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false) ? PersistenceJson.TryDeserialize<BenchmarkRun>(reader.GetString(0)) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<BenchmarkRun>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return Task.FromResult<IReadOnlyList<BenchmarkRun>>([]);
        return _database.RunAsync<IReadOnlyList<BenchmarkRun>>(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, "SELECT data_json FROM benchmarks ORDER BY timestamp DESC, rowid DESC LIMIT $limit;");
            Sql.Add(command, "$limit", limit);
            var list = new List<BenchmarkRun>();
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                // Une ligne illisible est ignorée plutôt que de bloquer tout l'historique.
                if (PersistenceJson.TryDeserialize<BenchmarkRun>(reader.GetString(0)) is { } run) list.Add(run);
            }
            return list;
        }, cancellationToken);
    }
}
