using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Services;
using PCBoost.Diagnostics.Monitoring;
using PCBoost.Diagnostics.Rules;
using PCBoost.Diagnostics.Tests.TestSupport;
using PCBoost.TestUtilities;

namespace PCBoost.Diagnostics.Tests;

public sealed class ServiceRegistrationTests
{
    private static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IClock>(new FakeClock());
        services.AddSingleton<ISystemInfoProvider, FakeSystemInfoProvider>();
        services.AddSingleton<ISystemMetricsProvider, FakeSystemMetricsProvider>();
        services.AddSingleton<IHardwareProvider, FakeHardwareProvider>();
        services.AddSingleton<IPowerProvider, FakePowerProvider>();
        services.AddSingleton<IFileSystemProvider, InMemoryFileSystemProvider>();
        var processes = new FakeProcessProvider();
        services.AddSingleton<IProcessProvider>(processes);
        services.AddSingleton<IStartupService, FakeStartupService>();
        services.AddSingleton<ICleanupService, FakeCleanupService>();
        services.AddSingleton<IProcessService, FakeProcessService>();
        services.AddSingleton<ISettingsService, FakeSettingsService>();
        services.AddSingleton<IScanHistoryRepository, InMemoryScanHistoryRepository>();
        services.AddSingleton<IPerformanceSnapshotRepository, InMemoryPerformanceSnapshotRepository>();
        configure?.Invoke(services);
        services.AddPCBoostDiagnostics();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void All_diagnostics_services_resolve()
    {
        using var provider = BuildProvider();
        Assert.NotNull(provider.GetRequiredService<ISystemAnalyzer>());
        Assert.NotNull(provider.GetRequiredService<IHealthRulesEngine>());
        Assert.NotNull(provider.GetRequiredService<IPerformanceScoreCalculator>());
        Assert.NotNull(provider.GetRequiredService<IPerformanceRecommendationEngine>());
        Assert.NotNull(provider.GetRequiredService<IHardwareProfileClassifier>());
        Assert.NotNull(provider.GetRequiredService<ISlowPcDiagnosticService>());
        Assert.NotNull(provider.GetRequiredService<IHardwareAdvisor>());
        Assert.NotNull(provider.GetRequiredService<IStorageAnalyzer>());
        Assert.Same(provider.GetRequiredService<PerformanceMonitor>(), provider.GetRequiredService<IPerformanceMonitor>());
        Assert.Same(provider.GetRequiredService<PerformanceHistoryRecorder>(), provider.GetRequiredService<IPerformanceHistoryService>());
        Assert.Same(provider.GetRequiredService<ISystemAnalyzer>(), provider.GetRequiredService<ISystemAnalyzer>());
    }

    [Fact]
    public void Every_built_in_rule_is_registered_once()
    {
        var services = new ServiceCollection();
        services.AddPCBoostDiagnostics();
        services.AddPCBoostDiagnostics();
        var rules = services.Where(d => d.ServiceType == typeof(IHealthRule)).ToList();
        Assert.Equal(13, rules.Count);
        Assert.Equal(13, rules.Select(d => d.ImplementationType).Distinct().Count());
    }

    [Fact]
    public void Custom_rules_and_options_can_be_added()
    {
        using var provider = BuildProvider(s =>
        {
            s.AddSingleton<IHealthRule, ExtraRule>();
            s.AddSingleton(new SystemAnalyzerOptions { DefaultLoadSamplingDuration = TimeSpan.FromSeconds(7) });
        });
        Assert.Equal(14, provider.GetServices<IHealthRule>().Count());
        Assert.Equal(TimeSpan.FromSeconds(7), provider.GetRequiredService<SystemAnalyzerOptions>().DefaultLoadSamplingDuration);
    }

    [Fact]
    public void String_resources_are_registered()
    {
        using var provider = BuildProvider();
        var sources = provider.GetServices<IStringResourceSource>().ToList();
        Assert.Contains(sources, s => s.GetString("Diag_Score_Memory_Label", CultureInfo.GetCultureInfo("fr")) == "Mémoire vive");
        Assert.Contains(sources, s => s.GetString("Diag_Score_Memory_Label", CultureInfo.GetCultureInfo("en")) == "Memory (RAM)");
    }

    [Fact]
    public async Task Resolved_analyzer_runs_end_to_end()
    {
        using var provider = BuildProvider(s => s.AddSingleton(new SystemAnalyzerOptions { LoadSamplingInterval = TimeSpan.FromMilliseconds(1) }));
        var analyzer = provider.GetRequiredService<ISystemAnalyzer>();
        var report = await analyzer.AnalyzeAsync(new Core.Models.Analysis.AnalysisOptions(LoadSamplingDuration: TimeSpan.FromMilliseconds(3)));
        Assert.NotNull(report.HardwareProfile);
        Assert.Single(await provider.GetRequiredService<IScanHistoryRepository>().GetRecentAsync(5));
    }

    private sealed class ExtraRule : IHealthRule
    {
        public string Id => "extra";
        public Core.Models.Analysis.HealthFinding? Evaluate(Core.Models.Analysis.SystemAnalysisReport report, Core.Settings.HealthThresholds thresholds) => null;
    }
}
