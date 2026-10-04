using PCBoost.Presentation.ViewModels;

namespace PCBoost.Presentation.Navigation;

/// <summary>Correspondance clé de page → type de ViewModel et clé du titre (pour la navigation de l'App).</summary>
public static class PageRegistry
{
    private static readonly Dictionary<string, (Type ViewModel, string TitleKey)> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        [PageKeys.Welcome] = (typeof(WelcomeViewModel), "Welcome_Title"),
        [PageKeys.Home] = (typeof(HomeViewModel), "Home_Title"),
        [PageKeys.Analysis] = (typeof(AnalysisViewModel), "Analysis_Title"),
        [PageKeys.Health] = (typeof(HealthViewModel), "Health_Title"),
        [PageKeys.Drivers] = (typeof(DriversViewModel), "Drivers_Title"),
        [PageKeys.Diagnosis] = (typeof(DiagnosisViewModel), "Diagnosis_Title"),
        [PageKeys.Optimization] = (typeof(OptimizationViewModel), "Optimization_Title"),
        [PageKeys.OldPc] = (typeof(OldPcViewModel), "OldPc_Title"),
        [PageKeys.Cleanup] = (typeof(CleanupViewModel), "Cleanup_Page_Title"),
        [PageKeys.Startup] = (typeof(StartupViewModel), "Startup_Title"),
        [PageKeys.Processes] = (typeof(ProcessesViewModel), "Processes_Title"),
        [PageKeys.Gaming] = (typeof(GamingViewModel), "Gaming_Title"),
        [PageKeys.Benchmark] = (typeof(BenchmarkViewModel), "Benchmark_Title"),
        [PageKeys.Performance] = (typeof(PerformanceViewModel), "Performance_Title"),
        [PageKeys.History] = (typeof(HistoryViewModel), "History_Title"),
        [PageKeys.Journal] = (typeof(JournalViewModel), "Journal_Title"),
        [PageKeys.Settings] = (typeof(SettingsViewModel), "Settings_Title"),
        [PageKeys.Privacy] = (typeof(PrivacyViewModel), "Privacy_Title"),
        [PageKeys.About] = (typeof(AboutViewModel), "About_Title"),
        [PageKeys.Storage] = (typeof(StorageViewModel), "Storage_Title"),
        [PageKeys.Expert] = (typeof(ExpertViewModel), "Expert_Title"),
        [PageKeys.Report] = (typeof(DiagnosticReportViewModel), "Report_Title"),
        [PageKeys.Programs] = (typeof(ProgramsViewModel), "Programs_Title"),
        [PageKeys.Files] = (typeof(FilesViewModel), "Files_Title"),
        [PageKeys.GameProfile] = (typeof(GameProfileViewModel), "GameProfile_Title"),
        [Profiles] = (typeof(ProfilesViewModel), "Profiles_Title"),
    };

    /// <summary>Profils : section de la page Optimisation, également utilisable comme page autonome.</summary>
    public const string Profiles = "profiles";

    public static IReadOnlyCollection<string> Keys => Pages.Keys;

    public static Type? ViewModelTypeFor(string pageKey)
        => pageKey is not null && Pages.TryGetValue(pageKey, out var entry) ? entry.ViewModel : null;

    public static string? TitleKeyFor(string pageKey)
        => pageKey is not null && Pages.TryGetValue(pageKey, out var entry) ? entry.TitleKey : null;
}
