using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Common;
using PCBoost.Persistence.Repositories;

namespace PCBoost.Persistence;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre le stockage SQLite local (singletons) : base, initialiseur, et tous les dépôts de Core.
    /// La journalisation (<c>ILogger&lt;T&gt;</c>) doit être enregistrée par l'hôte.
    /// </summary>
    /// <param name="databasePath">Chemin du fichier (ex. %LOCALAPPDATA%\PCBoost\pcboost.db) ; le dossier est créé au besoin.</param>
    public static IServiceCollection AddPCBoostPersistence(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Le chemin de la base de données locale est requis.", nameof(databasePath));

        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton(new PersistenceOptions { DatabasePath = Path.GetFullPath(databasePath) });
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IDatabaseInitializer>(sp => sp.GetRequiredService<DatabaseInitializer>());

        services.AddSingleton<IOptimizationHistoryRepository, SqliteOptimizationHistoryRepository>();
        services.AddSingleton<IGamingSessionRepository, SqliteGamingSessionRepository>();
        services.AddSingleton<IScanHistoryRepository, SqliteScanHistoryRepository>();
        services.AddSingleton<IPerformanceSnapshotRepository, SqlitePerformanceSnapshotRepository>();
        services.AddSingleton<IBenchmarkRepository, SqliteBenchmarkRepository>();
        services.AddSingleton<IKeyValueStore, SqliteKeyValueStore>();
        services.AddSingleton<IActivityLogRepository, SqliteActivityLogRepository>();
        return services;
    }
}
