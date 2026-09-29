using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PCBoost.Persistence.Schema;

/// <summary>Applique les migrations manquantes (idempotent) en s'appuyant sur <c>PRAGMA user_version</c>.</summary>
internal static class SchemaMigrator
{
    public static async Task<int> GetUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Applique chaque migration de version supérieure à la version courante, chacune dans sa transaction.</summary>
    /// <returns>Version du schéma après migration.</returns>
    public static async Task<int> MigrateAsync(
        SqliteConnection connection,
        IReadOnlyList<SchemaMigration> migrations,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ValidateOrder(migrations);
        var current = await GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        var latest = migrations.Count == 0 ? 0 : migrations[^1].Version;

        if (current > latest)
        {
            // Base créée par une version plus récente (retour à une version antérieure) : on ne touche à rien.
            logger.LogWarning(
                "Le schéma de la base locale (version {DatabaseVersion}) est plus récent que celui de cette version de l'application ({KnownVersion}). Aucune migration n'est appliquée.",
                current, latest);
            return current;
        }

        foreach (var migration in migrations)
        {
            if (migration.Version <= current) continue;
            cancellationToken.ThrowIfCancellationRequested();

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA user_version = " + migration.Version.ToString(CultureInfo.InvariantCulture) + ";";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            current = migration.Version;
            logger.LogInformation("Migration de la base locale appliquée : version {Version} ({Name}).", migration.Version, migration.Name);
        }

        return current;
    }

    private static void ValidateOrder(IReadOnlyList<SchemaMigration> migrations)
    {
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
                throw new InvalidOperationException($"Migrations mal ordonnées : la migration n°{i + 1} porte la version {migrations[i].Version}.");
        }
    }
}
