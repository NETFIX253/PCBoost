using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Settings;
using PCBoost.Diagnostics.Recommendations;
using PCBoost.Diagnostics.Rules;
using PCBoost.Diagnostics.Tests.TestSupport;

namespace PCBoost.Diagnostics.Tests;

public sealed class RecommendationEngineTests
{
    private static readonly string[] AllowedTargets =
        ["home", "analysis", "optimization", "cleanup", "startup", "processes", "gaming", "performance", "history", "settings", "diagnosis", "storage", "oldpc"];

    private static readonly string[] AllowedOptimizationIds = ["temp-files", "startup-apps", "power-plan", "visual-effects", "background-apps"];

    private readonly PerformanceRecommendationEngine _engine = new();

    private static IReadOnlyList<HealthFinding> Findings(SystemAnalysisReport report, HealthThresholds? thresholds = null)
    {
        IHealthRule[] rules =
        [
            new MemoryUsageRule(), new SystemDriveFreeSpaceRule(), new StartupCountRule(), new SustainedCpuRule(), new DiskActivityRule(),
            new CpuTemperatureRule(), new GpuTemperatureRule(), new StorageTemperatureRule(), new UptimeRule(), new CleanableFilesRule(),
            new BackgroundProcessesRule(), new PowerSaverOnAcRule(), new UnsupportedBuildRule(),
        ];
        return new HealthRulesEngine(rules, NullLogger<HealthRulesEngine>.Instance).Evaluate(report, thresholds ?? new HealthThresholds());
    }

    private IReadOnlyList<Recommendation> Recommend(SystemAnalysisReport report, params string[] dismissed)
        => _engine.GetRecommendations(report, Findings(report), dismissed);

    [Fact]
    public void Eight_startup_apps_give_high_impact_low_risk_recommendation_to_startup_page()
    {
        var report = Reports.Healthy().WithStartup(8, disabled: 2);
        var rec = Assert.Single(Recommend(report), r => r.Id == "rec.startup.heavy");

        Assert.Equal(RecommendationKind.Recommendation, rec.Kind);
        Assert.Equal("Diag_Rec_StartupHeavy_Title", rec.Title.Key);
        Assert.Equal(8, rec.Title.Args[0]);
        Assert.Equal(ImpactLevel.High, rec.Impact);
        Assert.Equal(RiskLevel.Low, rec.Risk);
        Assert.True(rec.Reversible);
        Assert.Equal("startup", rec.Category);
        Assert.NotNull(rec.Action);
        Assert.Equal("Diag_Action_ViewStartupApps", rec.Action.Label.Key);
        Assert.Equal("startup", rec.Action.NavigationTarget);
        Assert.Equal("startup-apps", rec.Action.OptimizationId);
        Assert.Equal("Diag_Rec_StartupHeavy_Reason", rec.Reason.Key);
        Assert.Equal(8, rec.Reason.Args[1]); // seuil par défaut
    }

    [Fact]
    public void Startup_confidence_is_high_when_entries_can_be_disabled()
    {
        var withCandidates = Reports.Healthy().WithStartup(9, canDisable: true);
        Assert.Equal(ConfidenceLevel.High, Recommend(withCandidates).Single(r => r.Id == RecommendationIds.StartupHeavy).Confidence);
        Assert.Equal(ConfidenceLevel.Medium, Recommend(Reports.Healthy().WithStartup(9)).Single(r => r.Id == RecommendationIds.StartupHeavy).Confidence);
    }

    [Fact]
    public void Healthy_pc_gets_no_recommendation()
        => Assert.Empty(Recommend(Reports.Healthy()));

    [Fact]
    public void Dismissed_ids_are_filtered()
    {
        var report = Reports.Overloaded();
        var all = Recommend(report);
        Assert.Contains(all, r => r.Id == RecommendationIds.StartupHeavy);
        Assert.Contains(all, r => r.Id == RecommendationIds.Cleanup);

        var filtered = Recommend(report, RecommendationIds.StartupHeavy, "REC.CLEANUP.RECOVERABLE");
        Assert.DoesNotContain(filtered, r => r.Id == RecommendationIds.StartupHeavy);
        Assert.DoesNotContain(filtered, r => r.Id == RecommendationIds.Cleanup);
        Assert.Equal(all.Count - 2, filtered.Count);
    }

    [Fact]
    public void Hardware_suggestions_are_possibilities_without_purchase_action()
    {
        var report = Reports.Healthy().WithSystemDrive(50, media: StorageMediaType.Hdd).WithMemory(92, 8L * ByteSize.GiB);
        var recs = Recommend(report);

        var ssd = Assert.Single(recs, r => r.Id == RecommendationIds.HardwareSsd);
        Assert.Equal(RecommendationKind.Possibility, ssd.Kind);
        Assert.Equal("oldpc", ssd.Action!.NavigationTarget);
        Assert.Null(ssd.Action.OptimizationId);

        var ram = Assert.Single(recs, r => r.Id == RecommendationIds.HardwareRam);
        Assert.Equal(RecommendationKind.Possibility, ram.Kind);
        Assert.Equal(ConfidenceLevel.Low, ram.Confidence);
        Assert.Null(ram.Action);
    }

    [Fact]
    public void No_ram_possibility_with_plenty_of_memory()
        => Assert.DoesNotContain(Recommend(Reports.Healthy().WithMemory(92, 32L * ByteSize.GiB)), r => r.Id == RecommendationIds.HardwareRam);

    [Fact]
    public void Recommendations_come_before_possibilities_then_by_impact()
    {
        var recs = Recommend(Reports.Overloaded());
        var firstPossibility = recs.ToList().FindIndex(r => r.Kind == RecommendationKind.Possibility);
        Assert.True(firstPossibility > 0);
        Assert.All(recs.Skip(firstPossibility), r => Assert.Equal(RecommendationKind.Possibility, r.Kind));
        var concrete = recs.Take(firstPossibility).ToList();
        for (var i = 1; i < concrete.Count; i++) Assert.True(concrete[i - 1].Impact >= concrete[i].Impact);
    }

    [Fact]
    public void Ids_are_unique_and_actions_use_known_pages_and_optimizations()
    {
        var recs = Recommend(Reports.Overloaded() with { Os = Reports.Healthy().Os with { BuildNumber = 17134 } });
        Assert.Equal(recs.Count, recs.Select(r => r.Id).Distinct().Count());
        Assert.All(recs, r => Assert.StartsWith("rec.", r.Id));
        foreach (var action in recs.Select(r => r.Action).OfType<RecommendationAction>())
        {
            Assert.Contains(action.NavigationTarget, AllowedTargets);
            if (action.OptimizationId is not null) Assert.Contains(action.OptimizationId, AllowedOptimizationIds);
        }
    }

    [Fact]
    public void Memory_recommendation_names_top_processes()
    {
        var report = Reports.Healthy().WithMemory(92).WithTopProcesses();
        var rec = Recommend(report).Single(r => r.Id == RecommendationIds.MemoryHigh);
        Assert.Equal("Diag_Rec_MemoryHigh_Description_Processes", rec.Description.Key);
        Assert.Equal("chrome.exe, Teams.exe, OneDrive.exe", rec.Description.Args[0]);
        Assert.Equal(ImpactLevel.High, rec.Impact);
        Assert.Equal("processes", rec.Action!.NavigationTarget);
    }

    [Fact]
    public void Power_saver_on_ac_recommends_balanced_plan()
    {
        var rec = Recommend(Reports.Healthy().WithPowerSaverOnAc()).Single();
        Assert.Equal(RecommendationIds.PowerPlan, rec.Id);
        Assert.Equal("power-plan", rec.Action!.OptimizationId);
        Assert.Equal("optimization", rec.Action.NavigationTarget);
    }

    [Fact]
    public void Cleanup_recommendation_is_not_reversible_and_targets_temp_files()
    {
        var rec = Recommend(Reports.Healthy().WithCleanable(4L * ByteSize.GiB)).Single(r => r.Id == RecommendationIds.Cleanup);
        Assert.False(rec.Reversible);
        Assert.Equal("temp-files", rec.Action!.OptimizationId);
        Assert.Equal("cleanup", rec.Action.NavigationTarget);
        Assert.Equal(ImpactLevel.Low, rec.Impact);
    }

    [Fact]
    public void Custom_thresholds_flow_through_findings()
    {
        var report = Reports.Healthy().WithStartup(4);
        var strict = new HealthThresholds { StartupWarningCount = 3, StartupCriticalCount = 10 };
        var recs = _engine.GetRecommendations(report, Findings(report, strict), []);
        Assert.Contains(recs, r => r.Id == RecommendationIds.StartupHeavy);
    }

    [Fact]
    public void Visual_effects_suggested_only_for_modest_hardware()
    {
        var legacy = Reports.Healthy() with
        {
            HardwareProfile = new HardwareProfile(HardwareTier.LegacyLowResource, [], true, false, true, false, false),
        };
        var rec = Recommend(legacy).Single(r => r.Id == RecommendationIds.VisualEffects);
        Assert.Equal("visual-effects", rec.Action!.OptimizationId);

        var highEnd = Reports.Healthy() with { HardwareProfile = new HardwareProfile(HardwareTier.HighEnd, [], false, false, false, false, true) };
        Assert.DoesNotContain(Recommend(highEnd), r => r.Id == RecommendationIds.VisualEffects);
    }

    [Fact]
    public void Thermal_recommendation_is_a_possibility_naming_the_hottest_component()
    {
        var rec = Recommend(Reports.Healthy().WithTemperatures(86, 99, null)).Single(r => r.Id == RecommendationIds.Thermal);
        Assert.Equal(RecommendationKind.Possibility, rec.Kind);
        Assert.Equal("Diag_Rec_Thermal_Reason_Gpu", rec.Reason.Key);
    }
}
