using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Formatting;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.Presentation;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre le formateur de valeurs, le contexte commun des ViewModels, tous les ViewModels
    /// (pages : Transient ; Shell : Singleton) et les ressources de texte de la présentation.
    /// L'App fournit : INavigationService, IDialogService, IUiDispatcher, IThemeService, IAppLifecycle
    /// (et facultativement BrandingOptions).
    /// </summary>
    public static IServiceCollection AddPCBoostPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton<IValueFormatter, ValueFormatter>();
        services.AddSingleton<ViewModelContext>();

        services.AddSingleton<ShellViewModel>();

        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<AnalysisViewModel>();
        services.AddTransient<DiagnosisViewModel>();
        services.AddTransient<HealthViewModel>();
        services.AddTransient<OptimizationViewModel>();
        services.AddTransient<ProfilesViewModel>();
        services.AddTransient<OldPcViewModel>();
        services.AddTransient<CleanupViewModel>();
        services.AddTransient<StartupViewModel>();
        services.AddTransient<ProcessesViewModel>();
        services.AddTransient<GamingViewModel>();
        services.AddTransient<BenchmarkViewModel>();
        services.AddTransient<PerformanceViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<JournalViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<PrivacyViewModel>();
        services.AddTransient<AboutViewModel>();
        services.AddTransient<StorageViewModel>();
        services.AddTransient<ExpertViewModel>();
        services.AddTransient<DiagnosticReportViewModel>();
        services.AddTransient<ProgramsViewModel>();
        services.AddTransient<FilesViewModel>();
        services.AddTransient<GameProfileViewModel>();

        services.AddSingleton<IStringResourceSource>(
            ResourceManagerStringSource.ForAssembly(typeof(ServiceCollectionExtensions).Assembly, "PCBoost.Presentation.Resources.Strings"));

        return services;
    }
}
