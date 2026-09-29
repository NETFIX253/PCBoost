using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Services;
using PCBoost.Infrastructure.Activity;
using PCBoost.Infrastructure.Localization;
using PCBoost.Infrastructure.Logging;
using PCBoost.Infrastructure.Settings;
using PCBoost.Infrastructure.Updates;

namespace PCBoost.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Nom de base des ressources communes (Common_*, Error_*, Infra_*).</summary>
    public const string ResourceBaseName = "PCBoost.Infrastructure.Resources.Strings";

    /// <summary>Source des textes communs de l'application.</summary>
    public static IStringResourceSource CreateCommonStringSource()
        => ResourceManagerStringSource.ForAssembly(typeof(ServiceCollectionExtensions).Assembly, ResourceBaseName);

    /// <summary>
    /// Enregistre les services transverses (singletons) : localisation, préférences, journal d'activité, mises à jour
    /// (source locale + emplacement réservé en ligne), informations d'application, horloge système (si absente).
    /// La source des textes communs est placée en tête des sources, quel que soit l'ordre d'appel des modules.
    /// Nécessite <c>IKeyValueStore</c> et <c>IActivityLogRepository</c> (AddPCBoostPersistence) et la journalisation
    /// (<see cref="AddPCBoostLogging"/> ou <c>AddLogging</c>).
    /// </summary>
    public static IServiceCollection AddPCBoostInfrastructure(this IServiceCollection services, Action<InfrastructureOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new InfrastructureOptions();
        configure?.Invoke(options);
        options.Branding ??= new();
        options.Updates ??= new UpdateOptions();

        services.AddSingleton(options);
        services.TryAddSingleton(options.Branding);
        services.AddSingleton(options.Updates);
        services.TryAddSingleton<IClock, SystemClock>();

        services.Insert(0, ServiceDescriptor.Singleton(CreateCommonStringSource()));

        services.AddSingleton(sp => new Localizer(
            sp.GetServices<IStringResourceSource>(),
            sp.GetRequiredService<ILogger<Localizer>>()));
        services.AddSingleton<ILocalizer>(sp => sp.GetRequiredService<Localizer>());

        services.AddSingleton<SettingsService>();
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());

        services.AddSingleton<ActivityJournal>();
        services.AddSingleton<IActivityJournal>(sp => sp.GetRequiredService<ActivityJournal>());

        services.AddSingleton<IAppInfo, AppInfo>();
        services.AddSingleton<IDiagnosticLogSource>(sp => new DiagnosticLogSource(sp.GetRequiredService<IAppInfo>()));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IUpdateProvider, LocalUpdateProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IUpdateProvider, PlaceholderRemoteUpdateProvider>());
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IUpdateService>(sp => sp.GetRequiredService<UpdateService>());
        return services;
    }

    /// <summary>
    /// Branche la journalisation Serilog créée par <see cref="PCBoostLogging.Create"/> : <c>ILoggerFactory</c>, <c>ILogger&lt;T&gt;</c>.
    /// L'hôte reste propriétaire de <paramref name="logging"/> et le libère à la fermeture.
    /// </summary>
    public static IServiceCollection AddPCBoostLogging(this IServiceCollection services, PCBoostLoggingHost logging)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logging);
        services.AddLogging();
        services.Replace(ServiceDescriptor.Singleton(logging.LoggerFactory));
        services.TryAddSingleton(logging);
        return services;
    }
}
