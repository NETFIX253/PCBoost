using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Settings;
using PCBoost.Diagnostics.Scoring;
using PCBoost.Diagnostics.Tests.TestSupport;
using PCBoost.TestUtilities;

namespace PCBoost.Diagnostics.Tests;

public sealed class PerformanceScoreCalculatorTests
{
    private static readonly string[] AllowedTargets =
        ["home", "analysis", "optimization", "cleanup", "startup", "processes", "gaming", "performance", "history", "settings", "diagnosis", "storage", "oldpc"];

    private readonly PerformanceScoreCalculator _calculator = new(new FakeClock());

    private PerformanceScore Score(SystemAnalysisReport report) => _calculator.Calculate(report, new HealthThresholds());

    private static ScoreFactor Factor(PerformanceScore score, string id) => score.Factors.Single(f => f.Id == id);

    [Fact]
    public void Healthy_pc_gets_100_and_max_points_sum_to_100()
    {
        var score = Score(Reports.Healthy());

        Assert.Equal(100, score.Value);
        Assert.Equal(7, score.Factors.Count);
        Assert.Equal(100, score.Factors.Sum(f => f.MaxPoints));
        Assert.Equal(100, score.Factors.Sum(f => f.Points));
        Assert.All(score.Factors, f => Assert.Equal(FactorStatus.Good, f.Status));
        Assert.Equal(new FakeClock().UtcNow, score.ComputedAt);
    }

    [Fact]
    public void Nominal_weights_match_documentation()
    {
        var score = Score(Reports.Healthy());
        Assert.Equal(25, Factor(score, ScoreFactorIds.Memory).MaxPoints);
        Assert.Equal(15, Factor(score, ScoreFactorIds.Cpu).MaxPoints);
        Assert.Equal(20, Factor(score, ScoreFactorIds.SystemDriveFreeSpace).MaxPoints);
        Assert.Equal(15, Factor(score, ScoreFactorIds.Startup).MaxPoints);
        Assert.Equal(10, Factor(score, ScoreFactorIds.Cleanable).MaxPoints);
        Assert.Equal(10, Factor(score, ScoreFactorIds.DiskActivity).MaxPoints);
        Assert.Equal(5, Factor(score, ScoreFactorIds.Uptime).MaxPoints);
    }

    [Theory]
    [InlineData(30, 25)]
    [InlineData(60, 25)]
    [InlineData(77.5, 13)] // milieu : 12,5 arrondi à 13
    [InlineData(95, 0)]
    [InlineData(100, 0)]
    public void Memory_factor_interpolates_between_60_and_95_percent(double used, int expected)
        => Assert.Equal(expected, Factor(Score(Reports.Healthy().WithMemory(used)), ScoreFactorIds.Memory).Points);

    [Theory]
    [InlineData(5, 15)]
    [InlineData(30, 15)]
    [InlineData(60, 8)] // 7,5 → 8
    [InlineData(90, 0)]
    [InlineData(100, 0)]
    public void Cpu_factor_interpolates_between_30_and_90_percent(double cpu, int expected)
        => Assert.Equal(expected, Factor(Score(Reports.Healthy().WithCpu(cpu)), ScoreFactorIds.Cpu).Points);

    [Theory]
    [InlineData(60, 20)]
    [InlineData(25, 20)]
    [InlineData(15, 10)]
    [InlineData(5, 0)]
    [InlineData(1, 0)]
    public void Storage_factor_interpolates_between_25_and_5_percent_free(double free, int expected)
        => Assert.Equal(expected, Factor(Score(Reports.Healthy().WithSystemDrive(free)), ScoreFactorIds.SystemDriveFreeSpace).Points);

    [Theory]
    [InlineData(0, 15)]
    [InlineData(5, 15)]
    [InlineData(10, 10)]
    [InlineData(20, 0)]
    [InlineData(30, 0)]
    public void Startup_factor_interpolates_between_5_and_20_apps(int enabled, int expected)
    {
        // Une entrée désactivée garantit une liste non vide (liste vide = non mesuré).
        var report = Reports.Healthy().WithStartup(enabled, disabled: 1);
        Assert.Equal(expected, Factor(Score(report), ScoreFactorIds.Startup).Points);
    }

    [Theory]
    [InlineData(0L, 10)]
    [InlineData(500L * ByteSize.MiB, 10)]
    [InlineData((500L * ByteSize.MiB + 10L * ByteSize.GiB) / 2, 5)]
    [InlineData(10L * ByteSize.GiB, 0)]
    [InlineData(50L * ByteSize.GiB, 0)]
    public void Cleanable_factor_interpolates_between_500MB_and_10GB(long bytes, int expected)
        => Assert.Equal(expected, Factor(Score(Reports.Healthy().WithCleanable(bytes)), ScoreFactorIds.Cleanable).Points);

    [Theory]
    [InlineData(0, 10)]
    [InlineData(30, 10)]
    [InlineData(62.5, 5)]
    [InlineData(95, 0)]
    public void Disk_factor_interpolates_between_30_and_95_percent(double active, int expected)
        => Assert.Equal(expected, Factor(Score(Reports.Healthy().WithDisk(active)), ScoreFactorIds.DiskActivity).Points);

    [Theory]
    [InlineData(1, 5)]
    [InlineData(3, 5)]
    [InlineData(8.5, 3)] // 2,5 → 3
    [InlineData(14, 0)]
    [InlineData(40, 0)]
    public void Uptime_factor_interpolates_between_3_and_14_days(double days, int expected)
        => Assert.Equal(expected, Factor(Score(Reports.Healthy().WithUptime(TimeSpan.FromDays(days))), ScoreFactorIds.Uptime).Points);

    [Fact]
    public void Unknown_factors_are_excluded_and_score_is_rescaled()
    {
        // CPU et disque non mesurés (aucun échantillon) ; mémoire à 70 % → 17,86 → 18/25.
        var report = Reports.Healthy().WithMemory(70).WithNoLoadSamples();
        var score = Score(report);

        Assert.Equal(FactorStatus.Unknown, Factor(score, ScoreFactorIds.Cpu).Status);
        Assert.Equal(FactorStatus.Unknown, Factor(score, ScoreFactorIds.DiskActivity).Status);
        Assert.Equal(0, Factor(score, ScoreFactorIds.Cpu).Points);
        Assert.Equal("Diag_Score_Unknown_Explanation", Factor(score, ScoreFactorIds.Cpu).Explanation.Key);
        // Mémoire : moyenne d'échantillonnage absente → instantané de GetMemoryInfo (70 %).
        Assert.Equal(18, Factor(score, ScoreFactorIds.Memory).Points);
        // (18 + 20 + 15 + 10 + 5) / 75 = 90,67 % → 91
        Assert.Equal(91, score.Value);
    }

    [Fact]
    public void Unknown_factor_never_counts_as_full_points()
    {
        var measured = Score(Reports.Healthy().WithMemory(95)); // mémoire 0/25, tout le reste plein → 75
        var cleanableUnknown = Score(Reports.Healthy().WithMemory(95).WithCleanable(null)); // 65/90 → 72

        Assert.Equal(75, measured.Value);
        Assert.Equal(72, cleanableUnknown.Value);
        Assert.Equal(FactorStatus.Unknown, Factor(cleanableUnknown, ScoreFactorIds.Cleanable).Status);
    }

    [Fact]
    public void Missing_measurements_are_unknown_not_assumed()
    {
        var report = Reports.Healthy().WithoutDrives().WithStartup(0).WithUptime(TimeSpan.Zero).WithDisk(null);
        var score = Score(report);

        Assert.Equal(FactorStatus.Unknown, Factor(score, ScoreFactorIds.SystemDriveFreeSpace).Status);
        Assert.Equal(FactorStatus.Unknown, Factor(score, ScoreFactorIds.Startup).Status);
        Assert.Equal(FactorStatus.Unknown, Factor(score, ScoreFactorIds.Uptime).Status);
        Assert.Equal(FactorStatus.Unknown, Factor(score, ScoreFactorIds.DiskActivity).Status);
        Assert.Equal(100, score.Value); // mémoire, CPU, nettoyage : tous pleins
    }

    [Fact]
    public void Nothing_measurable_gives_zero_with_all_factors_unknown()
    {
        var score = Score(Reports.Unmeasured());
        Assert.Equal(0, score.Value);
        Assert.All(score.Factors, f => Assert.Equal(FactorStatus.Unknown, f.Status));
    }

    [Theory]
    [InlineData(25, 25, FactorStatus.Good)]
    [InlineData(20, 25, FactorStatus.Good)]
    [InlineData(19, 25, FactorStatus.Fair)]
    [InlineData(10, 25, FactorStatus.Fair)]
    [InlineData(9, 25, FactorStatus.Poor)]
    [InlineData(4, 5, FactorStatus.Good)]
    [InlineData(2, 5, FactorStatus.Fair)]
    [InlineData(1, 5, FactorStatus.Poor)]
    [InlineData(0, 10, FactorStatus.Poor)]
    public void Status_thresholds_are_80_and_40_percent(int points, int max, FactorStatus expected)
        => Assert.Equal(expected, PerformanceScoreCalculator.StatusFor(points, max));

    [Fact]
    public void Overloaded_pc_scores_low_with_poor_factors()
    {
        var score = Score(Reports.Overloaded());
        Assert.True(score.Value < 20, $"score {score.Value}");
        Assert.Contains(score.Factors, f => f.Status == FactorStatus.Poor);
    }

    [Fact]
    public void Every_factor_has_label_explanation_with_measured_value_and_known_navigation_target()
    {
        var score = Score(Reports.Healthy().WithMemory(77.5).WithCpu(42));
        foreach (var factor in score.Factors)
        {
            Assert.StartsWith("Diag_Score_", factor.Label.Key);
            Assert.StartsWith("Diag_Score_", factor.Explanation.Key);
            Assert.Contains(factor.NavigationTarget, AllowedTargets);
        }
        Assert.Equal(77.5, (double)Factor(score, ScoreFactorIds.Memory).Explanation.Args[0]);
        Assert.Equal(42d, (double)Factor(score, ScoreFactorIds.Cpu).Explanation.Args[0]);
        Assert.Equal("startup", Factor(score, ScoreFactorIds.Startup).NavigationTarget);
        Assert.Equal("cleanup", Factor(score, ScoreFactorIds.Cleanable).NavigationTarget);
    }

    [Fact]
    public void Cleanable_explanation_switches_between_megabytes_and_gigabytes()
    {
        var small = Factor(Score(Reports.Healthy().WithCleanable(300 * ByteSize.MiB)), ScoreFactorIds.Cleanable).Explanation;
        var large = Factor(Score(Reports.Healthy().WithCleanable(3L * ByteSize.GiB)), ScoreFactorIds.Cleanable).Explanation;
        Assert.Equal("Diag_Score_Cleanable_Explanation_Mb", small.Key);
        Assert.Equal(300d, (double)small.Args[0]);
        Assert.Equal("Diag_Score_Cleanable_Explanation_Gb", large.Key);
        Assert.Equal(3d, (double)large.Args[0]);
    }

    /// <summary>Exemples chiffrés de docs/SCORING.md : ce test garantit que la documentation reste exacte.</summary>
    [Fact]
    public void Documented_examples_match()
    {
        var modest = Reports.Healthy()
            .WithMemory(78, 8L * ByteSize.GiB).WithCpu(35).WithSystemDrive(12).WithStartup(9, disabled: 1)
            .WithCleanable(5L * ByteSize.GiB / 2).WithDisk(20).WithUptime(TimeSpan.FromDays(5));
        var score = Score(modest);
        Assert.Equal(new[] { 12, 14, 7, 11, 8, 10, 4 }, score.Factors.Select(f => f.Points));
        Assert.Equal(
            new[] { FactorStatus.Fair, FactorStatus.Good, FactorStatus.Poor, FactorStatus.Fair, FactorStatus.Good, FactorStatus.Good, FactorStatus.Good },
            score.Factors.Select(f => f.Status));
        Assert.Equal(66, score.Value);

        var partial = Score(modest.WithDisk(null).WithCleanable(null));
        Assert.Equal(60, partial.Value); // (12 + 14 + 7 + 11 + 4) / 80
    }

    [Fact]
    public void Custom_thresholds_do_not_change_the_score()
    {
        var report = Reports.Healthy().WithMemory(85);
        var strict = new HealthThresholds { RamWarningPercent = 10, RamCriticalPercent = 20 };
        Assert.Equal(_calculator.Calculate(report, new HealthThresholds()).Value, _calculator.Calculate(report, strict).Value);
    }
}
