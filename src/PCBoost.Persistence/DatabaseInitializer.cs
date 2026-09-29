using PCBoost.Core.Abstractions.Persistence;

namespace PCBoost.Persistence;

/// <summary>
/// Initialisation du stockage local au démarrage (migrations, récupération d'une base endommagée).
/// Les dépôts initialisent aussi la base à la demande : appeler <see cref="InitializeAsync"/> au démarrage évite seulement
/// de payer ce coût lors de la première lecture.
/// </summary>
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private readonly SqliteDatabase _database;

    public DatabaseInitializer(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task InitializeAsync(CancellationToken cancellationToken = default) => _database.EnsureInitializedAsync(cancellationToken);

    /// <summary>Version du schéma de la base ouverte (0 avant <see cref="InitializeAsync"/>).</summary>
    public int CurrentSchemaVersion => _database.SchemaVersion;

    /// <summary>Résultat de l'initialisation, pour informer l'utilisateur d'une récupération ou d'un mode temporaire.</summary>
    public DatabaseState State => _database.State;

    public string? CorruptBackupPath => _database.CorruptBackupPath;
}
