using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Security;
using PCBoost.Core.Services;
using PCBoost.Gaming.Detection;
using PCBoost.Gaming.Detection.Scanners;
using PCBoost.Gaming.Optimizations;
using PCBoost.Gaming.Services;
using PCBoost.Gaming.Settings;

namespace PCBoost.Gaming;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre le module Gaming : signatures, scanners de bibliothèques, détection, modules d'optimisation Gaming
    /// (aussi exposés comme <see cref="IOptimization"/> pour l'OptimizationManager et les profils), vérifications Windows,
    /// mode Gaming, mode automatique, benchmark, réconciliation des sessions et ressources de texte « Game_* ».
    /// </summary>
    public static IServiceCollection AddPCBoostGaming(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<ICriticalProcessProtection>(_ => new CriticalProcessProtection());
        services.TryAddSingleton<GamingOptions>();
        services.TryAddSingleton<GameSignatureDatabase>();

        // Scanners de bibliothèques (chacun isolé).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameLibraryScanner, SteamLibraryScanner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameLibraryScanner, EpicLibraryScanner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameLibraryScanner, GogLibraryScanner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameLibraryScanner, UbisoftLibraryScanner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameLibraryScanner, EaLibraryScanner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameLibraryScanner, BattleNetLibraryScanner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameLibraryScanner, RiotLibraryScanner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameLibraryScanner, XboxLibraryScanner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameLibraryScanner, GameConfigStoreScanner>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGameLibraryScanner, CustomGamesScanner>());

        services.TryAddSingleton<GameDetectionService>();
        services.TryAddSingleton<IGameDetectionService>(sp => sp.GetRequiredService<GameDetectionService>());

        // Modules d'optimisation Gaming : instances uniques, exposées aussi comme IOptimization.
        services.TryAddSingleton<GamingPowerOptimization>();
        services.TryAddSingleton<GamingPriorityOptimization>();
        services.TryAddSingleton<GamingBackgroundOptimization>();
        services.TryAddSingleton<GamingGpuPreferenceOptimization>();
        services.AddSingleton<IOptimization>(sp => sp.GetRequiredService<GamingPowerOptimization>());
        services.AddSingleton<IOptimization>(sp => sp.GetRequiredService<GamingPriorityOptimization>());
        services.AddSingleton<IOptimization>(sp => sp.GetRequiredService<GamingBackgroundOptimization>());
        services.AddSingleton<IOptimization>(sp => sp.GetRequiredService<GamingGpuPreferenceOptimization>());

        services.TryAddSingleton<WindowsGameSettingsChecker>();
        services.TryAddSingleton<GamingService>();
        services.TryAddSingleton<IGamingService>(sp => sp.GetRequiredService<GamingService>());
        services.TryAddSingleton<GamingSessionReconciler>();
        services.TryAddSingleton<AutoGamingMode>();
        services.TryAddSingleton<IAutoGamingMode>(sp => sp.GetRequiredService<AutoGamingMode>());
        services.TryAddSingleton<BenchmarkService>();
        services.TryAddSingleton<IBenchmarkService>(sp => sp.GetRequiredService<BenchmarkService>());

        services.AddSingleton<IStringResourceSource>(
            ResourceManagerStringSource.ForAssembly(typeof(ServiceCollectionExtensions).Assembly, "PCBoost.Gaming.Resources.Strings"));
        return services;
    }
}
