using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Services;
using PCBoost.Diagnostics.Analysis;
using PCBoost.Diagnostics.Hardware;
using PCBoost.Diagnostics.Monitoring;
using PCBoost.Diagnostics.Recommendations;
using PCBoost.Diagnostics.Rules;
using PCBoost.Diagnostics.Scoring;
using PCBoost.Diagnostics.SlowPc;
using PCBoost.Diagnostics.Storage;

namespace PCBoost.Diagnostics;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre les services de diagnostic (singletons), chaque règle de santé intégrée et les textes « Diag_* ».
    /// Dépendances attendues ailleurs : fournisseurs de plateforme (ISystemInfoProvider, ISystemMetricsProvider, IHardwareProvider,
    /// IPowerProvider, IProcessProvider, IFileSystemProvider), IStartupService, ICleanupService, IProcessService, ISettingsService,
    /// IScanHistoryRepository, IPerformanceSnapshotRepository et la journalisation.
    /// Les options (<see cref="SystemAnalyzerOptions"/>…) peuvent être enregistrées avant cet appel pour être remplacées.
    /// L'application démarre elle-même la surveillance (<see cref="IPerformanceMonitor.Start"/>) et l'historique
    /// (<see cref="PerformanceHistoryRecorder.Start"/>).
    /// </summary>
    public static IServiceCollection AddPCBoostDiagnostics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();

        services.TryAddSingleton(new SystemAnalyzerOptions());
        services.TryAddSingleton(new SlowPcDiagnosticOptions());
        services.TryAddSingleton(new StorageAnalyzerOptions());
        services.TryAddSingleton(new PerformanceMonitorOptions());
        services.TryAddSingleton(new PerformanceHistoryOptions());

        // Règles de santé : extensibles, une règle ajoutée par l'application est exécutée avec les autres.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthRule, MemoryUsageRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthRule, SystemDriveFreeSpaceRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthRule, StartupCountRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthRule, SustainedCpuRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthRule, DiskActivityRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthRule, CpuTemperatureRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthRule, GpuTemperatureRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthRule, StorageTemperatureRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthRule, UptimeRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthRule, CleanableFilesRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthRule, BackgroundProcessesRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthRule, PowerSaverOnAcRule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthRule, UnsupportedBuildRule>());

        services.TryAddSingleton<IHealthRulesEngine, HealthRulesEngine>();
        services.TryAddSingleton<IPerformanceScoreCalculator, PerformanceScoreCalculator>();
        services.TryAddSingleton<IPerformanceRecommendationEngine, PerformanceRecommendationEngine>();
        services.TryAddSingleton<IHardwareProfileClassifier, HardwareProfileClassifier>();
        services.TryAddSingleton<IHardwareAdvisor, HardwareAdvisor>();
        services.TryAddSingleton<IStorageAnalyzer, StorageAnalyzer>();

        services.TryAddSingleton<PerformanceMonitor>();
        services.TryAddSingleton<IPerformanceMonitor>(sp => sp.GetRequiredService<PerformanceMonitor>());
        services.TryAddSingleton<PerformanceHistoryRecorder>();
        services.TryAddSingleton<IPerformanceHistoryService>(sp => sp.GetRequiredService<PerformanceHistoryRecorder>());

        services.TryAddSingleton<ISystemAnalyzer, SystemAnalyzer>();
        services.TryAddSingleton<ISlowPcDiagnosticService, SlowPcDiagnosticService>();

        services.AddSingleton<IStringResourceSource>(
            ResourceManagerStringSource.ForAssembly(typeof(ServiceCollectionExtensions).Assembly, "PCBoost.Diagnostics.Resources.Strings"));
        return services;
    }
}
