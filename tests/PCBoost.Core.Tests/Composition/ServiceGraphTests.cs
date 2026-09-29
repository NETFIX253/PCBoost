using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;
using PCBoost.Diagnostics;
using PCBoost.Gaming;
using PCBoost.Infrastructure;
using PCBoost.Optimization;
using PCBoost.Persistence;
using PCBoost.Presentation;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Navigation;
using PCBoost.TestUtilities;
using PCBoost.TestUtilities.Composition;

namespace PCBoost.Core.Tests.Composition;

/// <summary>
/// Compose le graphe complet de l'application (comme App.xaml.cs) avec des fournisseurs de plateforme factices,
/// et vérifie que chaque service et chaque ViewModel se résout (enregistrements manquants, cycles, durées de vie).
/// </summary>
public sealed class ServiceGraphTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcboost-graph-" + Guid.NewGuid().ToString("N"));

    public static ServiceProvider BuildProvider(string dataDirectory)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        // Plateforme factice (remplace AddPCBoostPlatform)
        var processes = new FakeProcessProvider();
        services.AddSingleton<IClock>(new FakeClock());
        services.AddSingleton<IProcessProvider>(processes);
        services.AddSingleton<IProcessControl>(processes);
        services.AddSingleton<ISystemInfoProvider, FakeSystemInfoProvider>();
        services.AddSingleton<ISystemMetricsProvider, FakeSystemMetricsProvider>();
        services.AddSingleton<IHardwareProvider, FakeHardwareProvider>();
        services.AddSingleton<IPowerProvider, FakePowerProvider>();
        services.AddSingleton<IForegroundWindowProvider, FakeForegroundWindowProvider>();
        services.AddSingleton<IRegistryProvider, InMemoryRegistryProvider>();
        services.AddSingleton<IFileSystemProvider, InMemoryFileSystemProvider>();
        services.AddSingleton<IFileMetadataProvider, FakeFileMetadataProvider>();
        services.AddSingleton<ISignatureVerifier, FakeSignatureVerifier>();
        services.AddSingleton<IShortcutResolver, FakeShortcutResolver>();
        services.AddSingleton<IRecycleBinProvider, FakeRecycleBinProvider>();
        services.AddSingleton<IScheduledTaskProvider, FakeScheduledTaskProvider>();
        services.AddSingleton<IVisualEffectsProvider, FakeVisualEffectsProvider>();
        services.AddSingleton<IElevationService, FakeElevationService>();
        services.AddSingleton<ICommandRunner, FakeCommandRunner>();
        services.AddSingleton<IShellService, FakeShellService>();
        services.AddSingleton<IFrameTimeSource, FakeFrameTimeSource>();
        services.AddSingleton<IAutoStartRegistration, FakeAutoStartRegistration>();

        // Services de l'hôte UI (fournis par PCBoost.App)
        services.AddSingleton<INotificationService, FakeNotificationService>();
        services.AddSingleton<INavigationService, FakeNavigationService>();
        services.AddSingleton<IDialogService, FakeDialogService>();
        services.AddSingleton<IUiDispatcher, ImmediateDispatcher>();
        services.AddSingleton<IThemeService, FakeThemeService>();

        services.AddPCBoostInfrastructure(o =>
        {
            o.DataDirectory = dataDirectory;
            o.WindowsAppSdkVersion = "test";
        });
        services.AddPCBoostPersistence(Path.Combine(dataDirectory, "pcboost.db"));
        services.AddPCBoostDiagnostics();
        services.AddPCBoostOptimization();
        services.AddPCBoostGaming();
        services.AddPCBoostPresentation();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task Every_service_and_page_view_model_resolves()
    {
        await using var provider = BuildProvider(_dir);

        Type[] services =
        [
            typeof(ISystemAnalyzer), typeof(IHealthRulesEngine), typeof(IPerformanceScoreCalculator), typeof(IPerformanceRecommendationEngine),
            typeof(IHardwareProfileClassifier), typeof(ISlowPcDiagnosticService), typeof(IHardwareAdvisor), typeof(IStorageAnalyzer),
            typeof(IPerformanceMonitor), typeof(IPerformanceHistoryService),
            typeof(IProcessService), typeof(ICriticalProcessProtection), typeof(ISecurityService), typeof(IStartupService), typeof(ICleanupService),
            typeof(IOptimizationManager), typeof(IRollbackManager), typeof(IRecoveryManager), typeof(IProfileService), typeof(IOldPcAssistant),
            typeof(ISmartOptimizationService),
            typeof(IGameDetectionService), typeof(IGamingService), typeof(IAutoGamingMode), typeof(IBenchmarkService),
            typeof(ISettingsService), typeof(IActivityJournal), typeof(IAppInfo), typeof(IUpdateService),
            typeof(Core.Localization.ILocalizer), typeof(IValueFormatter),
            typeof(Core.Abstractions.Persistence.IDatabaseInitializer),
        ];
        foreach (var t in services)
            Assert.NotNull(provider.GetRequiredService(t));

        foreach (var key in typeof(PageKeys).GetFields().Select(f => (string)f.GetValue(null)!))
        {
            var vmType = PageRegistry.ViewModelTypeFor(key);
            if (vmType is null) continue;
            Assert.NotNull(provider.GetRequiredService(vmType));
        }
    }

    [Fact]
    public async Task Optimization_modules_from_all_modules_are_registered()
    {
        await using var provider = BuildProvider(_dir);
        var manager = provider.GetRequiredService<IOptimizationManager>();
        var ids = manager.Optimizations.Select(o => o.Id).ToHashSet();
        Assert.Contains("temp-files", ids);
        Assert.Contains("startup-apps", ids);
        Assert.Contains("power-plan", ids);
        Assert.Contains("visual-effects", ids);
        Assert.Contains("background-apps", ids);
        Assert.Contains("gaming-power", ids);
        Assert.Contains("gaming-priority", ids);
        Assert.Contains("gaming-background", ids);
        Assert.Contains("gaming-gpu-preference", ids);
    }

    [Fact]
    public async Task Database_initializes_and_settings_load()
    {
        await using var provider = BuildProvider(_dir);
        await provider.GetRequiredService<Core.Abstractions.Persistence.IDatabaseInitializer>().InitializeAsync();
        var settings = provider.GetRequiredService<ISettingsService>();
        await settings.LoadAsync();
        Assert.False(settings.Current.TelemetryEnabled);
        Assert.Equal(ThemePreference.System, settings.Current.Theme);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class FakeNavigationService : INavigationService
    {
        public string? CurrentPageKey { get; private set; }
        public bool CanGoBack => false;
        public event EventHandler<string>? Navigated;
        public bool Navigate(string pageKey, object? parameter = null) { CurrentPageKey = pageKey; Navigated?.Invoke(this, pageKey); return true; }
        public void GoBack() { }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public Task<DialogResultKind> ConfirmAsync(ConfirmationRequest request) => Task.FromResult(DialogResultKind.Primary);
        public Task ShowErrorAsync(OperationResult result, TextRef? context = null) => Task.CompletedTask;
        public Task ShowMessageAsync(TextRef title, TextRef message) => Task.CompletedTask;
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool HasThreadAccess => true;
        public void Post(Action action) => action();
        public Task InvokeAsync(Func<Task> action) => action();
    }

    private sealed class FakeThemeService : IThemeService
    {
        public ThemePreference Current { get; private set; }
        public void Apply(ThemePreference theme) => Current = theme;
    }
}
