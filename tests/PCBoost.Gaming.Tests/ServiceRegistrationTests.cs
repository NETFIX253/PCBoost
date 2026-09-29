using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Gaming.Detection;
using PCBoost.Gaming.Services;
using PCBoost.Gaming.Tests.Fakes;
using PCBoost.TestUtilities;

namespace PCBoost.Gaming.Tests;

public sealed class ServiceRegistrationTests
{
    private static ServiceProvider Build()
    {
        var processes = new FakeProcessProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IProcessProvider>(processes);
        services.AddSingleton<IProcessControl>(processes);
        services.AddSingleton<IPowerProvider, FakePowerProvider>();
        services.AddSingleton<ISystemInfoProvider, FakeSystemInfoProvider>();
        services.AddSingleton<IRegistryProvider, InMemoryRegistryProvider>();
        services.AddSingleton<IFileSystemProvider, InMemoryFileSystemProvider>();
        services.AddSingleton<IForegroundWindowProvider, FakeForegroundWindowProvider>();
        services.AddSingleton<IFrameTimeSource, FakeFrameTimeSource>();
        services.AddSingleton<ISettingsService, FakeSettingsService>();
        services.AddSingleton<INotificationService, FakeNotificationService>();
        services.AddSingleton<IPerformanceMonitor, FakePerformanceMonitor>();
        services.AddSingleton<IRollbackManager, FakeRollbackManager>();
        services.AddSingleton<IGamingSessionRepository, InMemoryGamingSessionRepository>();
        services.AddSingleton<IBenchmarkRepository, InMemoryBenchmarkRepository>();
        services.AddPCBoostGaming();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task All_gaming_services_resolve()
    {
        await using var provider = Build();

        Assert.IsType<GamingService>(provider.GetRequiredService<IGamingService>());
        Assert.IsType<GameDetectionService>(provider.GetRequiredService<IGameDetectionService>());
        Assert.IsType<AutoGamingMode>(provider.GetRequiredService<IAutoGamingMode>());
        Assert.IsType<BenchmarkService>(provider.GetRequiredService<IBenchmarkService>());
        Assert.NotNull(provider.GetRequiredService<GamingSessionReconciler>());
        Assert.Same(provider.GetRequiredService<IGamingService>(), provider.GetRequiredService<GamingService>());
        Assert.Equal(10, provider.GetServices<IGameLibraryScanner>().Count());
    }

    [Fact]
    public async Task Gaming_modules_are_exposed_as_optimizations()
    {
        await using var provider = Build();

        var ids = provider.GetServices<IOptimization>().Select(o => o.Id).ToList();

        Assert.Equal(GamingOptimizationIds.All, ids);
    }

    [Fact]
    public async Task Resources_are_registered_in_french_and_english()
    {
        await using var provider = Build();
        var source = Assert.Single(provider.GetServices<IStringResourceSource>());

        Assert.Equal("Paramètres précédents restaurés.", source.GetString("Game_Deactivated_Body", CultureInfo.GetCultureInfo("fr")));
        Assert.Equal("Previous settings restored.", source.GetString("Game_Deactivated_Body", CultureInfo.GetCultureInfo("en")));
        Assert.Equal("Jeu détecté : lancement du mode Gaming ?", source.GetString("Game_Detected_Title", CultureInfo.GetCultureInfo("fr")));
    }

    [Fact]
    public async Task Detected_game_starts_are_ignored_without_crashing_when_auto_mode_is_off()
    {
        await using var provider = Build();
        var settings = provider.GetRequiredService<ISettingsService>();
        settings.Current.Gaming.AutoActivation = Core.Settings.AutoGamingBehavior.Off;

        var auto = provider.GetRequiredService<IAutoGamingMode>();
        auto.Start();
        auto.Stop();

        Assert.False(provider.GetRequiredService<IGameDetectionService>().IsWatching);
    }

    [Fact]
    public void No_resource_text_promises_a_frame_rate_gain()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PCBoost.Gaming", "Resources", "Strings.i18n.json");
        var text = File.ReadAllText(path);

        Assert.DoesNotContain("+", text.Replace("C++", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        foreach (var banned in new[] { "turbo", "boost ultime", "garanti", "guaranteed", "virus", "malware" })
            Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
    }
}
