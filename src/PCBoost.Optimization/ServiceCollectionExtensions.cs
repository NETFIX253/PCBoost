using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Security;
using PCBoost.Core.Services;
using PCBoost.Optimization.Cleanup;
using PCBoost.Optimization.Common;
using PCBoost.Optimization.Handlers;
using PCBoost.Optimization.Modules;
using PCBoost.Optimization.Orchestration;
using PCBoost.Optimization.Processes;
using PCBoost.Optimization.Rollback;
using PCBoost.Optimization.Safety;
using PCBoost.Optimization.Startup;

namespace PCBoost.Optimization;

public static class ServiceCollectionExtensions
{
    /// <summary>Fichier d'extension de la liste de protection (ajouts uniquement), relatif à %LOCALAPPDATA%.</summary>
    public const string UserProtectionFileRelativePath = @"PCBoost\protected-processes.user.json";

    /// <summary>
    /// Enregistre le moteur d'optimisation : restauration, validation, gestionnaires d'annulation, nettoyage, démarrage,
    /// processus, pilotes, modules (<see cref="IOptimization"/>), profils, assistant ancien PC, surveillance intelligente et ressources
    /// de texte (<c>Opt_*</c>, <c>Cleanup_*</c>, <c>Protection_*</c>).
    /// </summary>
    public static IServiceCollection AddPCBoostOptimization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();

        // Sécurité des processus : liste embarquée + extensions utilisateur (ajouts uniquement).
        services.TryAddSingleton<ICriticalProcessProtection>(sp =>
        {
            var localAppData = sp.GetRequiredService<IFileSystemProvider>().GetKnownFolder(KnownFolder.LocalAppData);
            var userFile = string.IsNullOrWhiteSpace(localAppData) ? null : PathUtil.Combine(localAppData, UserProtectionFileRelativePath);
            return CriticalProcessProtection.CreateWithUserExtensions(userFile);
        });
        services.TryAddSingleton<ISecurityService, SecurityService>();

        // Écritures privilégiées (repli PCBoost.Elevator).
        services.TryAddSingleton<RegistryWriter>();
        services.TryAddSingleton<ScheduledTaskWriter>();

        // Gestionnaires d'annulation, un par type de modification.
        services.AddSingleton<IChangeHandler>(sp => new RegistryValueChangeHandler(sp.GetRequiredService<RegistryWriter>()));
        services.AddSingleton<IChangeHandler, PowerSchemeChangeHandler>();
        services.AddSingleton<IChangeHandler, ProcessPriorityChangeHandler>();
        services.AddSingleton<IChangeHandler, ProcessEfficiencyChangeHandler>();
        services.AddSingleton<IChangeHandler, VisualEffectsChangeHandler>();
        services.AddSingleton<IChangeHandler>(sp => new ScheduledTaskChangeHandler(sp.GetRequiredService<IScheduledTaskProvider>(), sp.GetRequiredService<ScheduledTaskWriter>()));
        services.AddSingleton<IChangeHandler>(_ => new IrreversibleChangeHandler(ChangeKinds.FileDeletion));
        services.AddSingleton<IChangeHandler>(_ => new IrreversibleChangeHandler(ChangeKinds.RecycleBin));
        services.AddSingleton<IChangeHandler, DriverUpdateChangeHandler>();

        // Restauration, validation, récupération.
        services.TryAddSingleton<IOptimizationSafetyValidator, OptimizationSafetyValidator>();
        services.TryAddSingleton<RollbackManager>();
        services.TryAddSingleton<IRollbackManager>(sp => sp.GetRequiredService<RollbackManager>());
        services.TryAddSingleton<IRecoveryManager, RecoveryManager>();
        services.TryAddSingleton<IRestorePointService, RestorePointService>();

        // Désinstallation assistée (programme officiel de l'éditeur, après confirmation).
        services.TryAddSingleton<IProgramInventoryService, Programs.ProgramInventoryService>();

        // Mises à jour de pilotes (Windows Update, point de restauration obligatoire, retour au pilote précédent).
        services.TryAddSingleton<IDriverUpdateService, Drivers.DriverUpdateService>();

        // Gros fichiers et doublons des dossiers personnels (Corbeille uniquement).
        services.TryAddSingleton<IFileCleanupService, Files.FileCleanupService>();

        // Nettoyage.
        services.TryAddSingleton<SafeCleanupEngine>();
        services.TryAddSingleton<ICleanupService, CleanupService>();

        // Démarrage.
        services.TryAddSingleton<IStartupProvider, StartupScanner>();
        services.TryAddSingleton(sp => new StartupToggler(
            sp.GetRequiredService<IRegistryProvider>(), sp.GetRequiredService<RegistryWriter>(), sp.GetRequiredService<IScheduledTaskProvider>(),
            sp.GetRequiredService<ScheduledTaskWriter>(), sp.GetRequiredService<IClock>()));
        services.TryAddSingleton<IStartupService>(sp => new StartupService(
            sp.GetRequiredService<IStartupProvider>(), sp.GetRequiredService<IProcessProvider>(), sp.GetRequiredService<StartupToggler>(),
            sp.GetRequiredService<IRollbackManager>(), sp.GetRequiredService<IActivityJournal>(), sp.GetService<ILogger<StartupService>>()));

        // Processus.
        services.TryAddSingleton<IProcessService, ProcessService>();

        // Modules d'optimisation.
        services.AddSingleton<IOptimization, TemporaryFilesOptimization>();
        services.AddSingleton<IOptimization>(sp => new StartupAppsOptimization(
            sp.GetRequiredService<IStartupService>(), sp.GetRequiredService<StartupToggler>(), sp.GetRequiredService<IRollbackManager>()));
        services.AddSingleton<IOptimization, PowerPlanOptimization>();
        services.AddSingleton<IOptimization, VisualEffectsOptimization>();
        services.AddSingleton<IOptimization, BackgroundAppsOptimization>();

        // Orchestration.
        services.TryAddSingleton<IOptimizationManager, OptimizationManager>();
        services.TryAddSingleton<IProfileService, ProfileService>();
        services.TryAddSingleton<IOldPcAssistant, OldPcAssistant>();
        services.TryAddSingleton<ISmartOptimizationService, SmartOptimizationService>();

        services.AddSingleton<IStringResourceSource>(
            ResourceManagerStringSource.ForAssembly(typeof(ServiceCollectionExtensions).Assembly, "PCBoost.Optimization.Resources.Strings"));
        return services;
    }
}
