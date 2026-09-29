using Microsoft.Data.Sqlite;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Models.Monitoring;

namespace PCBoost.Persistence.Repositories;

public sealed class SqlitePerformanceSnapshotRepository : IPerformanceSnapshotRepository
{
    private readonly SqliteDatabase _database;

    public SqlitePerformanceSnapshotRepository(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>Insertion par lot dans une seule transaction, avec une commande préparée réutilisée.</summary>
    public Task AddRangeAsync(IReadOnlyCollection<PerformanceSnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0) return Task.CompletedTask;
        return _database.RunAsync(async (connection, token) =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            await using var command = Sql.Command(connection, """
                INSERT INTO snapshots (timestamp, cpu_percent, memory_percent, disk_percent, gpu_percent, cpu_temp_c, gpu_temp_c, fps, frame_time_ms)
                VALUES ($timestamp, $cpu, $memory, $disk, $gpu, $cpuTemp, $gpuTemp, $fps, $frameTime);
                """, transaction);
            var timestamp = command.Parameters.Add("$timestamp", SqliteType.Integer);
            var cpu = command.Parameters.Add("$cpu", SqliteType.Real);
            var memory = command.Parameters.Add("$memory", SqliteType.Real);
            var disk = command.Parameters.Add("$disk", SqliteType.Real);
            var gpu = command.Parameters.Add("$gpu", SqliteType.Real);
            var cpuTemp = command.Parameters.Add("$cpuTemp", SqliteType.Real);
            var gpuTemp = command.Parameters.Add("$gpuTemp", SqliteType.Real);
            var fps = command.Parameters.Add("$fps", SqliteType.Real);
            var frameTime = command.Parameters.Add("$frameTime", SqliteType.Real);
            command.Prepare();

            foreach (var snapshot in snapshots)
            {
                token.ThrowIfCancellationRequested();
                timestamp.Value = Sql.ToTicks(snapshot.Timestamp);
                cpu.Value = snapshot.CpuPercent;
                memory.Value = snapshot.MemoryPercent;
                disk.Value = (object?)snapshot.DiskActivePercent ?? DBNull.Value;
                gpu.Value = (object?)snapshot.GpuPercent ?? DBNull.Value;
                cpuTemp.Value = (object?)snapshot.CpuTemperatureC ?? DBNull.Value;
                gpuTemp.Value = (object?)snapshot.GpuTemperatureC ?? DBNull.Value;
                fps.Value = (object?)snapshot.Fps ?? DBNull.Value;
                frameTime.Value = (object?)snapshot.FrameTimeMs ?? DBNull.Value;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>Instantanés entre <paramref name="from"/> et <paramref name="to"/> inclus, par ordre chronologique.</summary>
    public Task<IReadOnlyList<PerformanceSnapshot>> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        => _database.RunAsync<IReadOnlyList<PerformanceSnapshot>>(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, """
                SELECT timestamp, cpu_percent, memory_percent, disk_percent, gpu_percent, cpu_temp_c, gpu_temp_c, fps, frame_time_ms
                FROM snapshots WHERE timestamp >= $from AND timestamp <= $to ORDER BY timestamp, id;
                """);
            Sql.Add(command, "$from", Sql.ToTicks(from));
            Sql.Add(command, "$to", Sql.ToTicks(to));
            var list = new List<PerformanceSnapshot>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new PerformanceSnapshot(
                    Sql.GetDate(reader, 0),
                    reader.GetDouble(1),
                    reader.GetDouble(2),
                    Sql.GetNullableDouble(reader, 3),
                    Sql.GetNullableDouble(reader, 4),
                    Sql.GetNullableDouble(reader, 5),
                    Sql.GetNullableDouble(reader, 6),
                    Sql.GetNullableDouble(reader, 7),
                    Sql.GetNullableDouble(reader, 8)));
            }
            return list;
        }, cancellationToken);

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        => _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, "DELETE FROM snapshots WHERE timestamp < $cutoff;");
            Sql.Add(command, "$cutoff", Sql.ToTicks(cutoff));
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
}
