using System.Text.Json;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Common;
using PCBoost.Persistence.Serialization;

namespace PCBoost.Persistence.Repositories;

/// <summary>Stockage clé/valeur JSON (préférences, profil actif…). Une valeur illisible est traitée comme absente.</summary>
public sealed class SqliteKeyValueStore : IKeyValueStore
{
    private readonly SqliteDatabase _database;
    private readonly IClock _clock;
    private readonly ILogger<SqliteKeyValueStore> _logger;

    public SqliteKeyValueStore(SqliteDatabase database, IClock clock, ILogger<SqliteKeyValueStore> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var json = await _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, "SELECT value FROM kv WHERE key = $key;");
            Sql.Add(command, "$key", key);
            var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return value as string;
        }, cancellationToken).ConfigureAwait(false);

        if (json is null) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, PersistenceJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "La valeur enregistrée pour la clé « {Key} » est illisible ; la valeur par défaut est utilisée.", key);
            return default;
        }
    }

    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var json = JsonSerializer.Serialize(value, PersistenceJson.Options);
        var now = Sql.ToTicks(_clock.UtcNow);
        return _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, """
                INSERT INTO kv (key, value, updated_at) VALUES ($key, $value, $updated)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                """);
            Sql.Add(command, "$key", key);
            Sql.Add(command, "$value", json);
            Sql.Add(command, "$updated", now);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        return _database.RunAsync(async (connection, token) =>
        {
            await using var command = Sql.Command(connection, "DELETE FROM kv WHERE key = $key;");
            Sql.Add(command, "$key", key);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("La clé ne peut pas être vide.", nameof(key));
    }
}
