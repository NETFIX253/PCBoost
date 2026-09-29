using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using PCBoost.App.Services;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Branding;
using PCBoost.Core.Localization;
using PCBoost.Core.Services;
using PCBoost.Diagnostics;
using PCBoost.Diagnostics.Monitoring;
using PCBoost.Gaming;
using PCBoost.Gaming.Services;
using PCBoost.Infrastructure;
using PCBoost.Infrastructure.Updates;
using PCBoost.Infrastructure.Logging;
using PCBoost.Infrastructure.Settings;
using PCBoost.Optimization;
using PCBoost.Persistence;
using PCBoost.Platform;
using PCBoost.Presentation;
using PCBoost.Presentation.Abstractions;

namespace PCBoost.App;

/// <summary>
/// Racine de composition : injection de dépendances, initialisation légère (aucune analyse lourde au démarrage, §30),
/// fenêtre principale, zone de notification, notifications, récupération après plantage.
/// </summary>
public partial class App : Application
{
    private static IServiceProvider? _services;
    private PCBoostLoggingHost? _logging;
    private ILogger<App>? _logger;
    private MainWindow? _window;
    private IDisposable? _settingsBinding;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger?.LogError("Tâche non observée : {Message}", e.Exception.GetBaseException().Message);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _logger?.LogCritical("Exception fatale : {Message}", (e.ExceptionObject as Exception)?.Message);
    }

    public static IServiceProvider Services => _services ?? throw new InvalidOperationException("Services non initialisés.");

    public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

    public static T? TryGetService<T>() where T : class => _services?.GetService<T>();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var branding = LoadBranding();
            var infraOptions = new InfrastructureOptions
            {
                Branding = branding,
                WindowsAppSdkVersion = ReadWindowsAppSdkVersion(),
            };
            _logging = PCBoostLogging.Create(infraOptions.ResolveLogDirectory(), verbose: false);
            _services = ConfigureServices(infraOptions, _logging);
            _logger = Services.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("Démarrage de {Product} {Version} (.NET {Runtime}).", branding.ProductName,
                typeof(App).Assembly.GetName().Version, Environment.Version);

            await Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync().ConfigureAwait(true);
            var settings = Services.GetRequiredService<ISettingsService>();
            await settings.LoadAsync().ConfigureAwait(true);
            _settingsBinding = SettingsBindings.Bind(settings, Services.GetRequiredService<ILocalizer>(), _logging);
            if (Program.LanguageOverride is "fr" or "en")
                Services.GetRequiredService<ILocalizer>().SetLanguage(Program.LanguageOverride);

            _window = new MainWindow();
            _window.Initialize(showWindow: !Program.StartInBackground);
            Program.SecondInstanceActivated += (_, _) => _window?.DispatcherQueue.TryEnqueue(() => _window.ShowAndActivate());

            await StartBackgroundServicesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogCritical("Échec du démarrage : {Type} {Message}", ex.GetType().Name, ex.Message);
            ShowFatalStartupError(ex);
        }
    }

    private static IServiceProvider ConfigureServices(InfrastructureOptions infraOptions, PCBoostLoggingHost logging)
    {
        var services = new ServiceCollection();
        services.AddPCBoostLogging(logging);
        services.AddPCBoostPlatform();
        services.AddPCBoostInfrastructure(o =>
        {
            o.Branding = infraOptions.Branding;
            o.WindowsAppSdkVersion = infraOptions.WindowsAppSdkVersion;
            o.Updates = new UpdateOptions { LocalFeedPath = infraOptions.Branding.UpdateFeedPath };
        });
        services.AddPCBoostPersistence(infraOptions.ResolveDatabasePath());
        services.AddPCBoostDiagnostics();
        services.AddPCBoostOptimization();
        services.AddPCBoostGaming();
        services.AddPCBoostPresentation();

        // Services propres à l'hôte WinUI
        services.AddSingleton<IStringResourceSource>(ResourceManagerStringSource.ForAssembly(typeof(App).Assembly, "PCBoost.App.Resources.Strings"));
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<AppLifecycle>();
        services.AddSingleton<IAppLifecycle>(sp => sp.GetRequiredService<AppLifecycle>());
        services.AddSingleton<DevCaptureService>();
        services.AddSingleton<ReportFileService>();
        services.AddSingleton<IReportFileService>(sp => sp.GetRequiredService<ReportFileService>());
        services.AddSingleton<AppNotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<AppNotificationService>());
        services.AddSingleton<IUiDispatcher>(_ => new UiDispatcher(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    /// <summary>Services d'arrière-plan légers. Aucune analyse complète n'est lancée automatiquement.</summary>
    private async Task StartBackgroundServicesAsync()
    {
        var settings = Services.GetRequiredService<ISettingsService>().Current;
        var monitor = Services.GetRequiredService<IPerformanceMonitor>();
        if (settings.MonitoringEnabled) monitor.Start();
        Services.GetRequiredService<PerformanceHistoryRecorder>().Start();

        try
        {
            await Services.GetRequiredService<GamingSessionReconciler>().ReconcileAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Réconciliation des sessions Gaming : {Message}", ex.Message);
        }

        Services.GetRequiredService<IAutoGamingMode>().Start();
        Services.GetRequiredService<ISmartOptimizationService>().Start();

        // Santé du matériel : détection de limitation thermique (échantillons existants) et vérification périodique
        // des disques (première après 2 minutes, puis toutes les 6 heures ; lecture seule).
        Services.GetRequiredService<IThermalThrottlingDetector>().Start();
        Services.GetRequiredService<IHardwareHealthService>().Start();
    }

    /// <summary>Arrêt propre : restaure une session Gaming en cours, vide l'historique, libère les ressources.</summary>
    public async Task ShutdownAsync()
    {
        try
        {
            if (_services is not null)
            {
                // 1. Arrêter tout ce qui produit du travail en arrière-plan (minuteries, surveillance, détection),
                //    pour qu'aucune écriture en base ne coure pendant la libération des services.
                _services.GetService<IAutoGamingMode>()?.Stop();
                _services.GetService<IGameDetectionService>()?.StopWatching();
                _services.GetService<ISmartOptimizationService>()?.Stop();
                _services.GetService<IHardwareHealthService>()?.Stop();
                _services.GetService<IThermalThrottlingDetector>()?.Stop();
                _services.GetService<IPerformanceMonitor>()?.Stop();

                // 2. Restaurer une session Gaming encore active (jamais de réglage temporaire laissé en place).
                var gaming = _services.GetService<IGamingService>();
                if (gaming is not null && gaming.State is Core.Models.Gaming.GamingState.Active)
                    await gaming.DeactivateAsync().ConfigureAwait(true);

                // 3. Enregistrer la minute d'historique en cours et attendre les écritures en attente.
                var recorder = _services.GetService<PerformanceHistoryRecorder>();
                if (recorder is not null)
                {
                    recorder.Stop();
                    await recorder.FlushAsync().ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Arrêt : {Message}", ex.Message);
        }
        finally
        {
            _settingsBinding?.Dispose();
            if (_services is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(true);
            _logging?.Dispose();
        }
    }

    private static BrandingOptions LoadBranding()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "branding.json");
        try
        {
            if (File.Exists(path))
            {
                var options = JsonSerializer.Deserialize<BrandingOptions>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
                if (options is not null) return options;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"branding.json illisible : {ex.Message}");
        }
        return new BrandingOptions();
    }

    private static string ReadWindowsAppSdkVersion()
    {
        foreach (var file in new[] { "Microsoft.WindowsAppRuntime.dll", "Microsoft.UI.Xaml.dll" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, file);
            if (!File.Exists(path)) continue;
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.ProductVersion)) return info.ProductVersion!;
        }
        return string.Empty;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger?.LogError("Exception non gérée : {Type} {Message}", e.Exception.GetType().Name, e.Message);
        // Une erreur d'interface ne doit pas fermer l'application ni laisser des modifications non restaurées.
        e.Handled = true;
    }

    private static void ShowFatalStartupError(Exception ex)
    {
        var window = new Window { Title = "PCBoost" };
        window.Content = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = $"{Helpers.Str.Get("App_Startup_Error_Title")}\n\n{Helpers.Str.Get("App_Startup_Error_Message")}\n\n{ex.GetType().Name}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24),
        };
        window.Activate();
    }
}
