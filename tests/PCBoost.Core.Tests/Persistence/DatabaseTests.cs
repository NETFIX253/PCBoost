using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Common;
using PCBoost.Persistence;
using PCBoost.Persistence.Repositories;
using PCBoost.Persistence.Schema;

namespace PCBoost.Core.Tests.Storage;

public sealed class DatabaseTests
{
    private static readonly string[] ExpectedTables = ["sessions", "changes", "gaming_sessions", "scans", "snapshots", "benchmarks", "kv", "activity"];

    [Fact]
    public async Task Initialization_creates_the_schema_and_is_idempotent()
    {
        using var db = new TestDatabase();
        var initializer = new DatabaseInitializer(db.Database);
        Assert.Equal(0, initializer.CurrentSchemaVersion);

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        Assert.Equal(SchemaMigrations.LatestVersion, initializer.CurrentSchemaVersion);
        Assert.Equal(DatabaseState.Ready, initializer.State);
        Assert.Equal(SchemaMigrations.LatestVersion, await db.ScalarAsync("PRAGMA user_version;"));
        foreach (var table in ExpectedTables)
            Assert.Equal(1, await db.ScalarAsync($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';"));
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_changes_session_sequence';"));
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_snapshots_timestamp';"));
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_activity_timestamp';"));
    }

    [Fact]
    public async Task Reopening_an_existing_database_keeps_data_and_does_not_migrate_again()
    {
        using var db = new TestDatabase();
        var store = new SqliteKeyValueStore(db.Database, db.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteKeyValueStore>.Instance);
        await store.SetAsync("k", 42);

        using var reopened = db.Create();
        var initializer = new DatabaseInitializer(reopened);
        await initializer.InitializeAsync();
        var again = new SqliteKeyValueStore(reopened, db.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteKeyValueStore>.Instance);

        Assert.Equal(DatabaseState.Ready, initializer.State);
        Assert.Equal(SchemaMigrations.LatestVersion, initializer.CurrentSchemaVersion);
        Assert.Equal(42, await again.GetAsync<int>("k"));
    }

    [Fact]
    public async Task Connections_use_wal_foreign_keys_and_busy_timeout()
    {
        using var db = new TestDatabase();

        Assert.Equal("wal", await StringScalarAsync(db, "PRAGMA journal_mode;"));
        Assert.Equal(1, await db.ScalarAsync("PRAGMA foreign_keys;"));
        Assert.Equal(5000, await db.ScalarAsync("PRAGMA busy_timeout;"));
    }

    [Fact]
    public async Task Database_folder_is_created()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "a", "b", "pcboost.db");
        using var database = new SqliteDatabase(new PersistenceOptions { DatabasePath = path }, new TestUtilities.FakeClock(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteDatabase>.Instance);

        await database.EnsureInitializedAsync();

        Assert.True(File.Exists(path));
        Assert.Equal(DatabaseState.Ready, database.State);
    }

    [Fact]
    public async Task Corrupted_file_is_renamed_and_a_new_database_is_created()
    {
        using var db = new TestDatabase();
        var garbage = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("ceci n'est pas une base SQLite ", 400)));
        await File.WriteAllBytesAsync(db.DatabasePath, garbage);
        var logger = new ListLogger<SqliteDatabase>();
        using var database = new SqliteDatabase(new PersistenceOptions { DatabasePath = db.DatabasePath }, db.Clock, logger);

        await database.EnsureInitializedAsync();

        Assert.Equal(DatabaseState.RecoveredFromCorruption, database.State);
        Assert.Equal(SchemaMigrations.LatestVersion, database.SchemaVersion);
        var backup = Assert.Single(Directory.GetFiles(db.DirectoryPath, "pcboost.db.corrupt-*"));
        Assert.Equal(backup, database.CorruptBackupPath);
        Assert.EndsWith(".corrupt-20260928-120000", backup);
        Assert.Equal(garbage, await File.ReadAllBytesAsync(backup));
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error && e.Message.Contains("endommagée", StringComparison.Ordinal));

        var store = new SqliteKeyValueStore(database, db.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteKeyValueStore>.Instance);
        await store.SetAsync("after-recovery", "ok");
        Assert.Equal("ok", await store.GetAsync<string>("after-recovery"));
    }

    [Fact]
    public async Task Second_corruption_on_the_same_second_gets_a_distinct_backup_name()
    {
        using var db = new TestDatabase();
        await File.WriteAllTextAsync(db.DatabasePath + ".corrupt-20260928-120000", "previous backup");
        await File.WriteAllTextAsync(db.DatabasePath, new string('x', 8192));
        using var database = db.Create();

        await database.EnsureInitializedAsync();

        Assert.Equal(DatabaseState.RecoveredFromCorruption, database.State);
        Assert.EndsWith(".corrupt-20260928-120000-1", database.CorruptBackupPath);
        Assert.Equal("previous backup", await File.ReadAllTextAsync(db.DatabasePath + ".corrupt-20260928-120000"));
    }

    [Fact]
    public async Task Unusable_location_falls_back_to_a_temporary_in_memory_database()
    {
        using var temp = new TempDirectory();
        var blocker = temp.File("not-a-folder");
        await File.WriteAllTextAsync(blocker, "fichier ordinaire");
        var logger = new ListLogger<SqliteDatabase>();
        using var database = new SqliteDatabase(new PersistenceOptions { DatabasePath = Path.Combine(blocker, "sub", "pcboost.db") },
            new TestUtilities.FakeClock(), logger);

        await database.EnsureInitializedAsync();
        var store = new SqliteKeyValueStore(database, new TestUtilities.FakeClock(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteKeyValueStore>.Instance);
        await store.SetAsync("k", "v");

        Assert.Equal(DatabaseState.TemporaryInMemory, database.State);
        Assert.Equal(SchemaMigrations.LatestVersion, database.SchemaVersion);
        Assert.Equal("v", await store.GetAsync<string>("k"));
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Critical);
    }

    [Fact]
    public async Task Repositories_initialize_the_database_on_first_use()
    {
        using var db = new TestDatabase();
        var repository = new SqliteScanHistoryRepository(db.Database);

        Assert.Empty(await repository.GetRecentAsync(10));
        Assert.Equal(DatabaseState.Ready, db.Database.State);
    }

    [Fact]
    public async Task Concurrent_initialization_and_writes_succeed()
    {
        using var db = new TestDatabase();
        var repository = new SqliteActivityLogRepository(db.Database);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => repository.AddAsync(new Core.Models.Activity.ActivityLogEntry(
            Guid.NewGuid(), db.Clock.UtcNow.AddSeconds(i), Core.Models.Activity.ActivityKind.Info, TextRef.Of("K", i), null))));

        Assert.Equal(40, await db.ScalarAsync("SELECT COUNT(*) FROM activity;"));
    }

    [Fact]
    public void Empty_database_path_is_rejected()
        => Assert.Throws<ArgumentException>(() => new SqliteDatabase(new PersistenceOptions(), new TestUtilities.FakeClock(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteDatabase>.Instance));

    [Fact]
    public async Task Service_registration_resolves_every_repository()
    {
        using var temp = new TempDirectory();
        var services = new ServiceCollection().AddLogging().AddPCBoostPersistence(temp.File("pcboost.db"));
        await using var provider = services.BuildServiceProvider();

        var initializer = provider.GetRequiredService<IDatabaseInitializer>();
        await initializer.InitializeAsync();

        Assert.Same(provider.GetRequiredService<DatabaseInitializer>(), initializer);
        Assert.IsType<SqliteOptimizationHistoryRepository>(provider.GetRequiredService<IOptimizationHistoryRepository>());
        Assert.IsType<SqliteGamingSessionRepository>(provider.GetRequiredService<IGamingSessionRepository>());
        Assert.IsType<SqliteScanHistoryRepository>(provider.GetRequiredService<IScanHistoryRepository>());
        Assert.IsType<SqlitePerformanceSnapshotRepository>(provider.GetRequiredService<IPerformanceSnapshotRepository>());
        Assert.IsType<SqliteBenchmarkRepository>(provider.GetRequiredService<IBenchmarkRepository>());
        Assert.IsType<SqliteKeyValueStore>(provider.GetRequiredService<IKeyValueStore>());
        Assert.IsType<SqliteActivityLogRepository>(provider.GetRequiredService<IActivityLogRepository>());
        Assert.IsType<SystemClock>(provider.GetRequiredService<IClock>());
        Assert.Equal(SchemaMigrations.LatestVersion, initializer.CurrentSchemaVersion);
    }

    private static async Task<string?> StringScalarAsync(TestDatabase db, string sql)
    {
        await using var connection = await db.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string?)await command.ExecuteScalarAsync();
    }
}
