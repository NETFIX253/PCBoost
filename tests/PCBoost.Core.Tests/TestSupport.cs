using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Localization;
using PCBoost.Core.Services;
using PCBoost.Persistence;
using PCBoost.TestUtilities;

namespace PCBoost.Core.Tests;

/// <summary>Dossier temporaire supprimé à la fin du test.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcboost-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string SubDirectory(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Nettoyage au mieux : un fichier encore ouvert par le système ne doit pas faire échouer le test.
        }
    }
}

/// <summary>Base SQLite temporaire (fichier réel dans un dossier supprimé à la fin).</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly TempDirectory _directory = new();

    public TestDatabase(FakeClock? clock = null)
    {
        Clock = clock ?? new FakeClock();
        DatabasePath = _directory.File("pcboost.db");
        Database = Create();
    }

    public FakeClock Clock { get; }

    public string DatabasePath { get; }

    public string DirectoryPath => _directory.Path;

    public SqliteDatabase Database { get; }

    /// <summary>Nouvelle instance sur le même fichier (simule un redémarrage de l'application).</summary>
    public SqliteDatabase Create() => new(new PersistenceOptions { DatabasePath = DatabasePath }, Clock, NullLogger<SqliteDatabase>.Instance);

    public async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        Database.Dispose();
        _directory.Dispose();
    }
}

/// <summary>Source de textes en mémoire pour tester le localiseur.</summary>
public sealed class DictionaryStringSource(Dictionary<string, string> french, Dictionary<string, string>? english = null) : IStringResourceSource
{
    public string? GetString(string key, CultureInfo culture)
    {
        var table = culture.TwoLetterISOLanguageName == "en" && english is not null ? english : french;
        return table.TryGetValue(key, out var value) ? value : null;
    }
}

public sealed class FakeAppInfo(string dataDirectory, Version? version = null) : IAppInfo
{
    public string ProductName => "PCBoost";
    public Version Version { get; } = version ?? new Version(1, 0, 0, 0);
    public string DotNetVersion => ".NET";
    public string WindowsAppSdkVersion => string.Empty;
    public string DataDirectory { get; } = dataDirectory;
    public string LogDirectory => System.IO.Path.Combine(DataDirectory, "logs");
}

/// <summary>Journal en mémoire (vérifie les messages sans dépendre d'un fichier).</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
    }
}
