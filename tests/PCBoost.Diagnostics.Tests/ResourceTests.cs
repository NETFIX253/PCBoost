using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Settings;
using PCBoost.Diagnostics.Hardware;
using PCBoost.Diagnostics.Recommendations;
using PCBoost.Diagnostics.Rules;
using PCBoost.Diagnostics.Scoring;
using PCBoost.Diagnostics.SlowPc;
using PCBoost.Diagnostics.Tests.TestSupport;
using PCBoost.TestUtilities;

namespace PCBoost.Diagnostics.Tests;

public sealed class ResourceTests
{
    private static readonly ResourceManager Resources = new("PCBoost.Diagnostics.Resources.Strings", typeof(ServiceCollectionExtensions).Assembly);
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    /// <summary>Clés présentes dans une culture précise, sans repli sur la culture neutre.</summary>
    private static Dictionary<string, string> Strings(CultureInfo culture, bool neutral)
    {
        var set = Resources.GetResourceSet(neutral ? CultureInfo.InvariantCulture : culture, createIfNotExists: true, tryParents: neutral)
            ?? throw new InvalidOperationException($"Ressources introuvables pour {culture.Name}.");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static Dictionary<string, string> FrenchStrings() => Strings(French, neutral: true);

    private static Dictionary<string, string> EnglishStrings() => Strings(English, neutral: false);

    private static IEnumerable<string> DiagTextConstants() => typeof(DiagText)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Every_constant_exists_in_french_and_english()
    {
        var fr = FrenchStrings();
        var en = EnglishStrings();
        var missing = DiagTextConstants().Where(k => !fr.ContainsKey(k) || !en.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Clés manquantes : " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_resource_key_is_referenced_and_prefixed()
    {
        var constants = DiagTextConstants().ToHashSet();
        var fr = FrenchStrings();
        Assert.All(fr.Keys, k => Assert.StartsWith("Diag_", k));
        var unreferenced = fr.Keys.Where(k => !constants.Contains(k)).ToList();
        Assert.True(unreferenced.Count == 0, "Clés non référencées : " + string.Join(", ", unreferenced));
        Assert.Equal(fr.Count, EnglishStrings().Count);
    }

    [Fact]
    public void Texts_follow_product_wording_rules()
    {
        string[] forbidden = ["virus", "malware", "turbo", "boost ultime", "+200", "nécessaire", "necessary", "PCBoost"];
        foreach (var (key, value) in FrenchStrings().Concat(EnglishStrings()))
        {
            foreach (var word in forbidden)
                Assert.False(value.Contains(word, StringComparison.OrdinalIgnoreCase), $"{key} contient « {word} »");
        }
    }

    [Fact]
    public void Every_produced_text_exists_and_formats_in_both_languages()
    {
        var produced = ProducedTexts().ToList();
        Assert.True(produced.Count > 150, $"{produced.Count} textes produits");

        var fr = FrenchStrings();
        var en = EnglishStrings();
        foreach (var text in produced)
        {
            Assert.False(text.IsLiteral);
            Assert.True(fr.ContainsKey(text.Key), $"Clé fr absente : {text.Key}");
            Assert.True(en.ContainsKey(text.Key), $"Clé en absente : {text.Key}");
            var frText = string.Format(French, fr[text.Key], text.Args);
            var enText = string.Format(English, en[text.Key], text.Args);
            Assert.DoesNotContain("{", frText);
            Assert.DoesNotContain("{", enText);
            Assert.All(text.Args, a => Assert.False(a is TextRef, $"{text.Key} : argument TextRef imbriqué non pris en charge"));
        }

        // Chaque clé des ressources est effectivement produite par au moins un scénario.
        var producedKeys = produced.Select(t => t.Key).ToHashSet();
        var neverProduced = fr.Keys.Where(k => !producedKeys.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(neverProduced.Count == 0, "Clés jamais produites : " + string.Join(", ", neverProduced));
    }

    [Fact]
    public void Resource_source_resolves_keys_in_both_languages()
    {
        var source = Core.Localization.ResourceManagerStringSource.ForAssembly(typeof(ServiceCollectionExtensions).Assembly, "PCBoost.Diagnostics.Resources.Strings");
        Assert.Equal("Voir les applications", source.GetString("Diag_Action_ViewStartupApps", French));
        Assert.Equal("View apps", source.GetString("Diag_Action_ViewStartupApps", English));
    }

    /// <summary>Tous les textes produits par les moteurs sur un ensemble de scénarios couvrant chaque branche.</summary>
    private static IEnumerable<TextRef> ProducedTexts()
    {
        var clock = new FakeClock(Reports.Now);
        var score = new PerformanceScoreCalculator(clock);
        IHealthRule[] ruleSet =
        [
            new MemoryUsageRule(), new SystemDriveFreeSpaceRule(), new StartupCountRule(), new SustainedCpuRule(), new DiskActivityRule(),
            new CpuTemperatureRule(), new GpuTemperatureRule(), new StorageTemperatureRule(), new UptimeRule(), new CleanableFilesRule(),
            new BackgroundProcessesRule(), new PowerSaverOnAcRule(), new UnsupportedBuildRule(),
        ];
        var rules = new HealthRulesEngine(ruleSet, NullLogger<HealthRulesEngine>.Instance);
        var recommendations = new PerformanceRecommendationEngine();
        var advisor = new HardwareAdvisor();
        var classifier = new HardwareProfileClassifier();
        var thresholds = new HealthThresholds();
        var warningOnly = new HealthThresholds { CleanableWarningBytes = 100 * ByteSize.MiB };

        var oldBuild = Reports.Healthy().Os with { BuildNumber = 17134 };
        var legacyProfile = new HardwareProfile(HardwareTier.LegacyLowResource, [], true, false, true, false, false);
        var scenarios = new List<SystemAnalysisReport>
        {
            Reports.Healthy(),
            Reports.Unmeasured(),
            Reports.Overloaded() with { Os = oldBuild },
            Reports.Overloaded().WithTopProcesses() with { TopMemoryProcesses = [], TopCpuProcesses = [] },
            // Seuils « warning » (et non critiques), petites tailles, temps de fonctionnement récent.
            Reports.Healthy().WithMemory(82).WithCpu(75).WithDisk(85).WithSystemDrive(12).WithStartup(1).WithCleanable(300 * ByteSize.MiB)
                .WithUptime(TimeSpan.FromDays(8)) with { HardwareProfile = legacyProfile, Cpu = new CpuInfo("CPU", "V", 0, 0, null, null, null) },
            Reports.Healthy().WithStartup(9).WithTemperatures(null, 90, null).WithUptime(TimeSpan.FromHours(30)).WithSystemDrive(8).WithCleanable(null),
            Reports.Healthy().WithTemperatures(null, null, 80) with { HardwareProfile = legacyProfile },
        };

        foreach (var report in scenarios)
        {
            var computed = score.Calculate(report, thresholds);
            foreach (var f in computed.Factors) { yield return f.Label; yield return f.Explanation; }

            foreach (var t in new[] { thresholds, warningOnly, new HealthThresholds { StartupWarningCount = 1 } })
            {
                var findings = rules.Evaluate(report, t);
                foreach (var f in findings) { yield return f.Title; yield return f.Detail; }

                foreach (var r in recommendations.GetRecommendations(report, findings, []))
                {
                    yield return r.Title; yield return r.Description; yield return r.Reason;
                    if (r.Action is not null) yield return r.Action.Label;
                }

                foreach (var f in SlowPcDiagnosticService.Evaluate(report, t))
                {
                    yield return f.Title; yield return f.Evidence; yield return f.Why; yield return f.WhatToDo;
                    if (f.Action is not null) yield return f.Action.Label;
                }
            }

            var busy = Enumerable.Range(0, 50).Select(i => new PerformanceSnapshot(Reports.Now.AddMinutes(-i), 97, 95, null, null, null, null, null, null)).ToList();
            foreach (var a in advisor.GetAdvice(report, busy)) { yield return a.Observation; yield return a.Suggestion; }

            var profile = classifier.Classify(report.Cpu, report.Memory, report.Drives, report.Gpus);
            foreach (var reason in profile.Reasons) yield return reason;
        }

        // Profils matériels : SSD/HDD, GPU dédié avec ou sans mémoire connue, disque presque plein.
        var gpuUnknownMemory = new GpuInfo("GPU", GpuVendor.Amd, null, null, null, false, false);
        foreach (var reason in classifier.Classify(new CpuInfo("C", "V", 8, 16, null, null, null), new MemoryInfo(32L * ByteSize.GiB, 0, 0, 0, null, null),
                     [Reports.Drive(100L * ByteSize.GiB, 5L * ByteSize.GiB, StorageMediaType.Ssd)], [gpuUnknownMemory]).Reasons)
            yield return reason;
    }
}
