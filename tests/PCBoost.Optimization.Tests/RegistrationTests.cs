using System.Globalization;
using PCBoost.Core.Cleanup;
using PCBoost.Core.Localization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;

namespace PCBoost.Optimization.Tests;

public sealed class RegistrationTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void AddPCBoostOptimization_ResolvesEveryPublicService()
    {
        Assert.NotNull(_h.Get<IRollbackManager>());
        Assert.NotNull(_h.Get<IRecoveryManager>());
        Assert.NotNull(_h.Get<IRestorePointService>());
        Assert.NotNull(_h.Get<IProgramInventoryService>());
        Assert.NotNull(_h.Get<IFileCleanupService>());
        Assert.NotNull(_h.Get<IOptimizationSafetyValidator>());
        Assert.NotNull(_h.Get<ICleanupService>());
        Assert.NotNull(_h.Get<IStartupService>());
        Assert.NotNull(_h.Get<Core.Abstractions.Platform.IStartupProvider>());
        Assert.NotNull(_h.Get<IProcessService>());
        Assert.NotNull(_h.Get<ISecurityService>());
        Assert.NotNull(_h.Get<ICriticalProcessProtection>());
        Assert.NotNull(_h.Get<IOptimizationManager>());
        Assert.NotNull(_h.Get<IProfileService>());
        Assert.NotNull(_h.Get<IOldPcAssistant>());
        Assert.NotNull(_h.Get<ISmartOptimizationService>());
        Assert.Same(_h.Get<IRollbackManager>(), _h.Get<Rollback.RollbackManager>());
    }

    [Fact]
    public void Modules_AreRegisteredOnce_WithDocumentedMetadata()
    {
        var modules = _h.Get<IOptimizationManager>().Optimizations;

        Assert.Equal([OptimizationIds.TemporaryFiles, OptimizationIds.StartupApps, OptimizationIds.PowerPlan, OptimizationIds.VisualEffects, OptimizationIds.BackgroundApps],
            modules.Select(m => m.Id));
        Assert.False(modules.Single(m => m.Id == OptimizationIds.TemporaryFiles).IsReversible);
        Assert.All(modules.Where(m => m.Id != OptimizationIds.TemporaryFiles), m => Assert.True(m.IsReversible));
        Assert.All(modules, m => Assert.True(m.MinimumWindowsBuild >= 17763));
    }

    [Fact]
    public void Resources_CoverCleanupCatalog_Protection_AndModules_InFrenchAndEnglish()
    {
        var source = _h.Get<IEnumerable<IStringResourceSource>>().Single();
        var keys = new List<string> { "Protection_Critical", "Protection_Sensitive", "Protection_SystemDirectory" };
        keys.AddRange(CleanupCatalog.All.SelectMany(c => new[] { $"Cleanup_{c.Id}_Name", $"Cleanup_{c.Id}_Description" }));
        keys.AddRange(_h.Get<IOptimizationManager>().Optimizations.SelectMany(m => new[] { m.Name.Key, m.Description.Key }));
        keys.AddRange(_h.Get<IProfileService>().GetProfiles().SelectMany(p => new[] { p.Name.Key, p.Description.Key }));

        foreach (var key in keys)
        {
            var fr = source.GetString(key, CultureInfo.GetCultureInfo("fr-FR"));
            var en = source.GetString(key, CultureInfo.GetCultureInfo("en-US"));
            Assert.False(string.IsNullOrWhiteSpace(fr), key);
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            Assert.NotEqual(fr, en);
        }
    }

    [Fact]
    public void Resources_AvoidForbiddenMarketingAndAntivirusWording()
    {
        var source = _h.Get<IEnumerable<IStringResourceSource>>().Single();
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PCBoost.Optimization", "Resources", "Strings.i18n.json");
        var json = File.ReadAllText(path);
        foreach (var word in new[] { "turbo", "boost ultime", "virus", "malware", "malveillant" })
            Assert.DoesNotContain(word, json, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(source.GetString("Opt_Smart_SlowTitle", CultureInfo.GetCultureInfo("fr")));
    }
}
