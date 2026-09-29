using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Common;
using PCBoost.Persistence.Schema;

namespace PCBoost.Persistence;

/// <summary>État du stockage local après initialisation.</summary>
public enum DatabaseState
{
    NotInitialized = 0,
    /// <summary>Fichier ouvert (ou créé) et migré normalement.</summary>
    Ready,
    /// <summary>Fichier endommagé mis de côté (<c>.corrupt-&lt;date&gt;</c>) puis nouvelle base créée.</summary>
    RecoveredFromCorruption,
    /// <summary>
    /// Fichier inutilisable (disque en lecture seule, droits, fichier endommagé impossible à déplacer) :
    /// base temporaire en mémoire pour ne pas bloquer l'application. Rien n'est conservé à la fermeture.
    /// </summary>
    TemporaryInMemory,
}

/// <summary>
/// Fabrique de connexions SQLite (pool activé) et initialisation unique du schéma.
/// Chaque connexion applique <c>busy_timeout=5000</c> et <c>foreign_keys=ON</c> ; le mode WAL est fixé dans le fichier.
/// <c>synchronous</c> reste à FULL (valeur par défaut) : le journal de restauration est écrit AVANT chaque modification
/// système et doit survivre à une coupure de courant.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    private const int BusyTimeoutMilliseconds = 5000;

    private readonly IClock _clock;
    private readonly ILogger<SqliteDatabase> _logger;
    private readonly string _fileConnectionString;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile string _activeConnectionString;
    private volatile bool _initialized;
    private SqliteConnection? _memoryKeepAlive;
    private bool _disposed;

    public SqliteDatabase(PersistenceOptions options, IClock clock, ILogger<SqliteDatabase> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
            throw new ArgumentException("Le chemin de la base de données locale est vide.", nameof(options));

        _clock = clock;
        _logger = logger;
        DatabasePath = Path.GetFullPath(options.DatabasePath);
        _fileConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        _activeConnectionString = _fileConnectionString;
    }

    /// <summary>Chemin complet du fichier de base de données.</summary>
    public string DatabasePath { get; }

    public DatabaseState State { get; private set; }

    /// <summary>Copie mise de côté d'une base endommagée (null si aucune récupération n'a eu lieu).</summary>
    public string? CorruptBackupPath { get; private set; }

    /// <summary>Version du schéma de la base ouverte (0 avant l'initialisation).</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>
    /// Ouvre la base, applique les migrations manquantes et gère une base endommagée. Idempotent et sûr en concurrence.
    /// Ne lève pas d'exception pour un fichier endommagé ou inaccessible : voir <see cref="State"/>.
    /// </summary>
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() => InitializeCoreAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    /// <summary>Ouvre une connexion configurée (à libérer par l'appelant). Initialise la base au premier appel.</summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await OpenConnectionCoreAsync(_activeConnectionString, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Exécute un travail sur une connexion dédiée, hors du thread appelant : Microsoft.Data.Sqlite est synchrone en interne
    /// et une attente de verrou ne doit jamais figer l'interface.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var connectionString = _activeConnectionString;
        return await Task.Run(async () =>
        {
            await using var connection = await OpenConnectionCoreAsync(connectionString, cancellationToken).ConfigureAwait(false);
            return await work(connection, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task RunAsync(Func<SqliteConnection, CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync<bool>(async (connection, token) =>
        {
            await work(connection, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearPool(_activeConnectionString);
        _memoryKeepAlive?.Dispose();
        _memoryKeepAlive = null;
        _initializationLock.Dispose();
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(DatabasePath);
        try
        {
            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            SchemaVersion = await OpenCheckAndMigrateAsync(_fileConnectionString, isFile: true, cancellationToken).ConfigureAwait(false);
            State = DatabaseState.Ready;
            return;
        }
        catch (Exception ex) when (IsCorruption(ex))
        {
            _logger.LogError(ex,
                "La base de données locale « {FileName} » est endommagée. Elle est mise de côté et une nouvelle base est créée ; l'historique précédent n'est plus disponible dans l'application.",
                fileName);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogCritical(ex,
                "Impossible d'ouvrir la base de données locale « {FileName} ». Une base temporaire en mémoire est utilisée : l'historique ne sera pas conservé à la fermeture.",
                fileName);
            await SwitchToMemoryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var backup = TryQuarantineCorruptFile();
        if (backup is not null)
        {
            try
            {
                SchemaVersion = await OpenCheckAndMigrateAsync(_fileConnectionString, isFile: true, cancellationToken).ConfigureAwait(false);
                CorruptBackupPath = backup;
                State = DatabaseState.RecoveredFromCorruption;
                _logger.LogWarning("Nouvelle base de données locale créée. La copie endommagée est conservée sous « {BackupFileName} ».", Path.GetFileName(backup));
                return;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or DatabaseCorruptedException)
            {
                _logger.LogCritical(ex, "Impossible de recréer la base de données locale « {FileName} ».", fileName);
            }
        }

        _logger.LogCritical("Une base temporaire en mémoire est utilisée : l'historique ne sera pas conservé à la fermeture.");
        await SwitchToMemoryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> OpenCheckAndMigrateAsync(string connectionString, bool isFile, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionCoreAsync(connectionString, cancellationToken).ConfigureAwait(false);
        if (isFile)
        {
            await using (var check = connection.CreateCommand())
            {
                // Vérification rapide de la structure (quelques millisecondes pour une base de quelques Mo).
                check.CommandText = "PRAGMA quick_check;";
                var result = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new DatabaseCorruptedException(result ?? "quick_check sans résultat");
            }
            await using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode = WAL;";
                var mode = Convert.ToString(await wal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                    _logger.LogWarning("Le mode de journalisation WAL n'a pas pu être activé (mode actuel : {JournalMode}).", mode);
            }
        }
        return await SchemaMigrator.MigrateAsync(connection, SchemaMigrations.All, _logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SqliteConnection> OpenConnectionCoreAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = " + BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture) + "; PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Renomme le fichier endommagé (et ses fichiers WAL/SHM) en « .corrupt-&lt;date&gt; ». Null si impossible.</summary>
    private string? TryQuarantineCorruptFile()
    {
        ClearPool(_fileConnectionString);
        var stamp = _clock.UtcNow.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = DatabasePath + ".corrupt-" + stamp;
        for (var i = 1; File.Exists(backup); i++)
            backup = DatabasePath + ".corrupt-" + stamp + "-" + i.ToString(CultureInfo.InvariantCulture);

        try
        {
            if (File.Exists(DatabasePath)) File.Move(DatabasePath, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogCritical(ex, "Impossible de mettre de côté la base de données endommagée « {FileName} ».", Path.GetFileName(DatabasePath));
            return null;
        }

        // Un ancien journal WAL laissé à côté d'une nouvelle base pourrait y être rejoué : il doit disparaître.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = DatabasePath + suffix;
            if (!File.Exists(sidecar)) continue;
            try
            {
                File.Move(sidecar, backup + suffix);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(sidecar);
                }
                catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
                {
                    _logger.LogCritical(deleteError, "Le fichier annexe « {FileName} » de la base endommagée ne peut être ni déplacé ni supprimé.", Path.GetFileName(sidecar));
                    return null;
                }
            }
        }
        return backup;
    }

    private async Task SwitchToMemoryAsync(CancellationToken cancellationToken)
    {
        ClearPool(_fileConnectionString);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = "pcboost-temporary-" + Guid.NewGuid().ToString("N"),
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        // Une base partagée en mémoire n'existe que tant qu'au moins une connexion reste ouverte.
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync(cancellationToken).ConfigureAwait(false);
        _memoryKeepAlive = keepAlive;
        SchemaVersion = await OpenCheckAndMigrateAsync(connectionString, isFile: false, cancellationToken).ConfigureAwait(false);
        _activeConnectionString = connectionString;
        State = DatabaseState.TemporaryInMemory;
    }

    private static bool IsCorruption(Exception exception) => exception switch
    {
        DatabaseCorruptedException => true,
        // SQLITE_CORRUPT (11), SQLITE_NOTADB (26)
        SqliteException sqlite => (sqlite.SqliteErrorCode & 0xFF) is 11 or 26,
        _ => false,
    };

    private static void ClearPool(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private sealed class DatabaseCorruptedException(string detail)
        : Exception("Vérification d'intégrité SQLite en échec : " + detail);
}
