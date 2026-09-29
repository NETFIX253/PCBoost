using System.Globalization;
using System.Resources;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Settings;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.Presentation.Tests;

public sealed class ResourceTests
{
    private static readonly ResourceManager Resources = new("PCBoost.Presentation.Resources.Strings", typeof(ServiceCollectionExtensions).Assembly);
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private static IEnumerable<string> EnumKeys<TEnum>(string prefix, string suffix = "")
        where TEnum : struct, Enum
        => Enum.GetNames<TEnum>().Select(n => prefix + n + suffix);

    public static TheoryData<string> DynamicKeys()
    {
        var keys = new List<string>();
        keys.AddRange(EnumKeys<Severity>("Common_Label_Severity_"));
        keys.AddRange(EnumKeys<RiskLevel>("Common_Label_Risk_"));
        keys.AddRange(EnumKeys<ImpactLevel>("Common_Label_Impact_"));
        keys.AddRange(EnumKeys<ConfidenceLevel>("Common_Label_Confidence_"));
        keys.AddRange(EnumKeys<Availability>("Common_Label_Availability_"));
        keys.AddRange(EnumKeys<StepState>("Common_Label_Step_"));
        keys.AddRange(EnumKeys<AnalysisStage>("Analysis_Step_"));
        keys.AddRange(EnumKeys<AnalysisStage>("Analysis_StepRunning_"));
        keys.AddRange(EnumKeys<ProcessorArchitecture>("Analysis_Arch_"));
        keys.AddRange(EnumKeys<StorageMediaType>("Analysis_Media_"));
        keys.AddRange(EnumKeys<StorageBusType>("Analysis_Bus_"));
        keys.AddRange(EnumKeys<HardwareTier>("Analysis_Tier_"));
        keys.AddRange(EnumKeys<HardwareTier>("Analysis_TierDescription_"));
        keys.AddRange(FindingCategories.Canonical.Append(FindingCategories.Other).Select(c => "Analysis_Category_" + FindingCategories.ResourceSuffix(c)));
        keys.AddRange(EnumKeys<FactorStatus>("Home_FactorStatus_"));
        keys.AddRange(["Home_ScoreLevel_Good", "Home_ScoreLevel_Fair", "Home_ScoreLevel_Poor"]);
        keys.AddRange(EnumKeys<OptimizationStage>("Optimization_Step_"));
        keys.AddRange(EnumKeys<OptimizationStage>("Optimization_StepRunning_"));
        keys.AddRange(EnumKeys<OptimizationCategory>("Profiles_Category_"));
        keys.AddRange(EnumKeys<OldPcLevel>("OldPc_Level_", "_Name"));
        keys.AddRange(EnumKeys<OldPcLevel>("OldPc_Level_", "_Description"));
        keys.AddRange(EnumKeys<SafetyCategory>("Cleanup_Page_Safety_"));
        keys.AddRange(EnumKeys<SafetyCategory>("Cleanup_Page_SafetyDescription_"));
        keys.AddRange(EnumKeys<StartupImpact>("Startup_Impact_"));
        keys.AddRange(EnumKeys<StartupRecommendation>("Startup_Recommendation_"));
        keys.AddRange(EnumKeys<StartupLocation>("Startup_Location_"));
        keys.AddRange(EnumKeys<ProtectionLevel>("Processes_Protection_"));
        keys.AddRange(EnumKeys<GamingState>("Gaming_State_"));
        keys.AddRange(EnumKeys<GameSource>("Gaming_Source_"));
        keys.AddRange(EnumKeys<FrameCaptureAvailability>("Gaming_Capture_"));
        keys.AddRange(EnumKeys<BenchmarkPhase>("Benchmark_Phase_"));
        keys.AddRange(EnumKeys<SessionType>("History_Type_"));
        keys.AddRange(EnumKeys<SessionStatus>("History_Status_"));
        keys.AddRange(EnumKeys<ChangeStatus>("History_ChangeStatus_"));
        keys.AddRange(EnumKeys<ActivityKind>("Journal_Kind_"));
        keys.AddRange(EnumKeys<StorageCategoryKind>("Storage_Category_"));
        keys.AddRange(EnumKeys<ThemePreference>("Settings_Theme_"));
        keys.AddRange(EnumKeys<AutoGamingBehavior>("Settings_AutoGaming_"));
        keys.AddRange(new[] { "30s", "5m", "30m", "24h", "7d" }.SelectMany(p => new[] { "Performance_Period_" + p, "Performance_AxisStart_" + p }));
        var data = new TheoryData<string>();
        foreach (var k in keys.Distinct()) data.Add(k);
        return data;
    }

    [Theory]
    [MemberData(nameof(DynamicKeys))]
    public void EnumDrivenKey_ExistsInFrenchAndEnglish(string key)
    {
        Assert.False(string.IsNullOrWhiteSpace(Resources.GetString(key, Fr)), $"fr manquant : {key}");
        Assert.False(string.IsNullOrWhiteSpace(Resources.GetString(key, En)), $"en manquant : {key}");
    }

    [Fact]
    public void FrenchAndEnglish_HaveSameKeys_AndEnglishDiffersFromFrenchSource()
    {
        var fr = Read(CultureInfo.InvariantCulture);
        var en = Read(En);
        Assert.Equal(fr.Keys.OrderBy(k => k, StringComparer.Ordinal), en.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(fr.Count > 500);
        Assert.Equal("Votre PC est prêt.", fr["Optimization_Report_Ready"]);
        Assert.Equal("Your PC is ready.", en["Optimization_Report_Ready"]);
    }

    [Fact]
    public void NoForbiddenWording_AndNoReservedKeys()
    {
        var fr = Read(CultureInfo.InvariantCulture);
        var en = Read(En);
        foreach (var (key, value) in fr.Concat(en))
        {
            Assert.DoesNotContain("virus", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("malware", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("turbo", value, StringComparison.OrdinalIgnoreCase);
            Assert.False(key == "Common_NotAvailable" || key.StartsWith("Error_", StringComparison.Ordinal) || key.StartsWith("Protection_", StringComparison.Ordinal), key);
        }

        Assert.Equal("Aucun problème de performance correspondant à cette catégorie n'a été identifié.", fr["Analysis_Category_NoIssue"]);
    }

    [Fact]
    public void FormatArguments_AreConsistentBetweenLanguages()
    {
        var fr = Read(CultureInfo.InvariantCulture);
        var en = Read(En);
        foreach (var key in fr.Keys)
        {
            var args = new object[] { "a", "b", "c", "d" };
            var f = string.Format(Fr, fr[key], args);
            var e = string.Format(En, en[key], args);
            Assert.Equal(fr[key].Contains("{0", StringComparison.Ordinal), en[key].Contains("{0", StringComparison.Ordinal));
            Assert.NotNull(f);
            Assert.NotNull(e);
        }
    }

    private static Dictionary<string, string> Read(CultureInfo culture)
    {
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!;
        return set.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
    }
}
