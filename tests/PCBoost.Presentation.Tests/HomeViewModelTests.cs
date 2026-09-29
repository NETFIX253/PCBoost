using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;
using PCBoost.TestUtilities;

namespace PCBoost.Presentation.Tests;

public sealed class HomeViewModelTests
{
    private readonly TestUi _ui = new();
    private readonly FakeAnalyzer _analyzer = new();
    private readonly FakeRules _rules = new();
    private readonly FakeScore _score = new();
    private readonly FakeRecommendations _recommendations = new();
    private readonly FakeMonitor _monitor = new();
    private readonly FakeSettingsService _settings = new();

    private HomeViewModel Create() => new(_ui.Context, _analyzer, _rules, _score, _recommendations, _monitor, _settings);

    private static ScoreFactor Factor(string id, FactorStatus status, string? target = null)
        => new(id, TextRef.Literal(id), TextRef.Literal("explication"), 20, status == FactorStatus.Good ? 20 : 8, status, target);

    [Fact]
    public void StatusSentence_Normal_WhenNothingToImprove()
        => Assert.Equal("Votre PC est actuellement en état normal.", Create().BuildStatusSentence(0));

    [Fact]
    public void StatusSentence_Singular_And_Plural()
    {
        var vm = Create();
        Assert.Equal("1 point peut être amélioré.", vm.BuildStatusSentence(1));
        Assert.Equal("3 points peuvent être améliorés.", vm.BuildStatusSentence(3));
    }

    [Fact]
    public async Task Activation_WithExistingReport_ShowsScoreFactorsAndSentence()
    {
        _analyzer.LastReport = Reports.Create(_ui.Clock.UtcNow.AddMinutes(-5));
        _score.Score = new PerformanceScore(72, [Factor("ram", FactorStatus.Poor, PageKeys.Processes), Factor("disk", FactorStatus.Fair), Factor("startup", FactorStatus.Good)], _ui.Clock.UtcNow);
        var vm = Create();

        await vm.OnNavigatedToAsync(null);

        Assert.Equal(0, _analyzer.Calls);
        Assert.True(vm.HasReport);
        Assert.Equal("72", vm.ScoreText);
        Assert.Equal("État correct", vm.ScoreLevelText);
        Assert.Equal(3, vm.Factors.Count);
        Assert.Equal("2 points peuvent être améliorés.", vm.StatusSentence);
        Assert.Equal("Dernière analyse : il y a 5\u00a0min", vm.LastAnalysisText);
        Assert.Equal("3 applications", vm.StartupAppsText);
        Assert.Equal("Windows 11 Professionnel", vm.WindowsEditionText);
        Assert.Equal("Version 24H2 (build 26100.2033)", vm.WindowsVersionText);

        var ram = vm.Factors[0];
        Assert.Equal("8 / 20", ram.PointsText);
        Assert.Equal("À améliorer", ram.StatusText);
        Assert.True(ram.OpenCommand.CanExecute(null));
        ram.OpenCommand.Execute(null);
        Assert.Equal(PageKeys.Processes, _ui.Navigation.Navigations.Last().Key);
        Assert.False(vm.Factors[1].OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task Activation_WithoutReport_RunsAnalysis()
    {
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Equal(1, _analyzer.Calls);
        Assert.True(vm.HasReport);
        Assert.False(vm.IsAnalyzing);
        Assert.Equal("Votre PC est actuellement en état normal.", vm.StatusSentence);
    }

    [Fact]
    public async Task LiveTiles_FollowMonitor_UnavailableValuesAreExplicit()
    {
        _analyzer.LastReport = Reports.Create(_ui.Clock.UtcNow);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.Equal(MonitoringMode.Active, _monitor.Mode);
        Assert.Equal("Non disponible", vm.CpuTile.Value);

        _monitor.Publish(new SystemMetricsSample(_ui.Clock.UtcNow, 34.4, 61.25, (long)(9.8 * ByteSize.GiB), 16L * ByteSize.GiB, null, null, null, null, null, null, null, 180));

        Assert.Equal("34\u00a0%", vm.CpuTile.Value);
        Assert.True(vm.CpuTile.IsAvailable);
        Assert.Equal("9,8\u00a0Go sur 16\u00a0Go", vm.MemoryTile.Detail);
        Assert.Equal("Non disponible", vm.DiskTile.Value);
        Assert.False(vm.DiskTile.IsAvailable);
        Assert.Equal("Non disponible", vm.GpuTile.Value);
        Assert.False(vm.HasTemperatures);

        vm.OnNavigatedFrom();
        Assert.Equal(MonitoringMode.Background, _monitor.Mode);
        Assert.Equal(0, _monitor.SubscriberCount);
    }

    [Fact]
    public async Task Temperatures_ShownOnlyWhenMeasured()
    {
        _analyzer.LastReport = Reports.Create(_ui.Clock.UtcNow);
        _monitor.Temperatures = new TemperatureReadings(SensorReading.Of(62, "test"), SensorReading.Unavailable(Availability.NoSensor), SensorReading.Unavailable(Availability.RequiresElevation));
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.True(vm.HasTemperatures);
        Assert.Equal("62\u00a0°C", vm.CpuTemperatureTile.Value);
        Assert.Equal("Non disponible", vm.GpuTemperatureTile.Value);
        Assert.Equal("Aucun capteur accessible", vm.GpuTemperatureTile.Detail);
        Assert.Equal("Autorisation administrateur requise pour cette mesure", vm.StorageTemperatureTile.Detail);
    }

    [Fact]
    public async Task TopRecommendations_LimitedToThree_AndDismissPersists()
    {
        _analyzer.LastReport = Reports.Create(_ui.Clock.UtcNow);
        for (var i = 0; i < 5; i++)
        {
            _recommendations.Items.Add(new Recommendation($"r{i}", TextRef.Literal($"Titre {i}"), TextRef.Literal("d"), TextRef.Literal("raison"),
                ImpactLevel.High, ConfidenceLevel.High, RiskLevel.Low, i == 1 ? RecommendationKind.Possibility : RecommendationKind.Recommendation, true, "memory",
                new RecommendationAction(TextRef.Literal("Agir"), PageKeys.Startup, "startup-opt")));
        }

        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Equal(3, vm.TopRecommendations.Count);
        Assert.Equal("Possibilité", vm.TopRecommendations[1].KindText);
        Assert.Equal("Impact élevé", vm.TopRecommendations[0].ImpactText);

        vm.TopRecommendations[0].OpenCommand.Execute(null);
        Assert.Equal((PageKeys.Startup, (object?)"startup-opt"), _ui.Navigation.Navigations.Last());

        await vm.TopRecommendations[0].DismissCommand.ExecuteAsync(null);
        Assert.Contains("r0", _settings.Current.DismissedRecommendations);
        Assert.Equal("Titre 1", vm.TopRecommendations[0].Title);
    }

    [Fact]
    public async Task QuickActions_Navigate()
    {
        _analyzer.LastReport = Reports.Create(_ui.Clock.UtcNow);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        vm.OptimizeCommand.Execute(null);
        vm.WhyIsMyPcSlowCommand.Execute(null);
        vm.CleanupCommand.Execute(null);
        Assert.Equal([PageKeys.Optimization, PageKeys.Diagnosis, PageKeys.Cleanup], _ui.Navigation.Navigations.Select(n => n.Key).ToArray());
    }
}
