using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Startup;

namespace PCBoost.Optimization.Tests;

public sealed class PowerPlanTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private IOptimization Module => _h.Get<IEnumerable<IOptimization>>().Single(o => o.Id == OptimizationIds.PowerPlan);

    [Fact]
    public async Task OneClick_PowerSaverOnAC_SwitchesToBalanced()
    {
        _h.Power.ActiveId = PowerScheme.PowerSaver;

        var preview = await Module.PreviewAsync(new OptimizationContext(null));

        Assert.True(preview.Applicable);
        Assert.Equal("power:" + PowerScheme.Balanced.ToString("D"), preview.Changes.Single().Target);
    }

    [Fact]
    public async Task OneClick_OnBattery_OrNotPowerSaver_IsNotApplicable()
    {
        _h.Power.ActiveId = PowerScheme.PowerSaver;
        _h.SystemInfo.Power = new PowerStatus(PowerSource.Battery, 40, true);
        Assert.False((await Module.PreviewAsync(new OptimizationContext(null))).Applicable);

        _h.SystemInfo.Power = new PowerStatus(PowerSource.AC, 100, true);
        _h.Power.ActiveId = PowerScheme.Balanced;
        var preview = await Module.PreviewAsync(new OptimizationContext(null, profileId: OptimizationIds.OneClickProfileId));
        Assert.False(preview.Applicable);
        Assert.Equal("Opt_PowerPlan_AlreadySuitable", preview.NotApplicableReason!.Key);
    }

    [Fact]
    public async Task TargetAlreadyActive_IsNotApplicable()
    {
        _h.Power.ActiveId = PowerScheme.Balanced;

        var preview = await Module.PreviewAsync(new OptimizationContext(null, profileId: PerformanceProfile.BalancedId));

        Assert.False(preview.Applicable);
        Assert.Equal("Opt_PowerPlan_AlreadyActive", preview.NotApplicableReason!.Key);
    }

    [Fact]
    public async Task Gaming_WithoutPerformanceSchemes_IsNotApplicable()
    {
        // PC « Modern Standby » : seul le mode Utilisation normale existe.
        _h.Power.Schemes.RemoveAll(s => s.Id != PowerScheme.Balanced);

        var preview = await Module.PreviewAsync(new OptimizationContext(null, profileId: PerformanceProfile.GamingId));

        Assert.False(preview.Applicable);
        Assert.Equal("Opt_PowerPlan_NoPerformanceScheme", preview.NotApplicableReason!.Key);
    }

    [Fact]
    public async Task Gaming_PrefersUltimate_ThenHighPerformance()
    {
        Assert.Contains(PowerScheme.HighPerformance.ToString("D"),
            (await Module.PreviewAsync(new OptimizationContext(null, profileId: PerformanceProfile.GamingId))).Changes.Single().Target);

        _h.Power.Schemes.Add(new PowerScheme(PowerScheme.UltimatePerformance, "Performances optimales"));
        Assert.Contains(PowerScheme.UltimatePerformance.ToString("D"),
            (await Module.PreviewAsync(new OptimizationContext(null, profileId: PerformanceProfile.GamingId))).Changes.Single().Target);
    }

    [Fact]
    public async Task PowerSaverProfile_WhenSchemeMissing_IsNotApplicable()
    {
        _h.Power.Schemes.RemoveAll(s => s.Id == PowerScheme.PowerSaver);
        Assert.False((await Module.PreviewAsync(new OptimizationContext(null, profileId: PerformanceProfile.PowerSaverId))).Applicable);
    }

    [Fact]
    public async Task Apply_RecordsPreviousSchemeAndIsReversible()
    {
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Profile, TextRef.Literal("t"));

        var result = await Module.ApplyAsync(new OptimizationContext(null, profileId: PerformanceProfile.GamingId), recorder);
        await rollback.CompleteSessionAsync(recorder.SessionId);

        Assert.True(result.Outcome.Success);
        Assert.Equal(PowerScheme.HighPerformance, _h.Power.ActiveId);
        var change = _h.History.AllChanges.Single();
        Assert.Equal(PowerScheme.Balanced, ChangeStateSerializer.Deserialize<PowerSchemeState>(change.BeforeState)!.SchemeId);

        await rollback.RestoreSessionAsync(recorder.SessionId);
        Assert.Equal(PowerScheme.Balanced, _h.Power.ActiveId);
    }
}

public sealed class VisualEffectsTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private IOptimization Module => _h.Get<IEnumerable<IOptimization>>().Single(o => o.Id == OptimizationIds.VisualEffects);

    [Fact]
    public async Task OnlyApplicableOnSmallConfigurations_OrWhenForced()
    {
        Assert.False((await Module.PreviewAsync(new OptimizationContext(Reports.Create(HardwareTier.HighEnd)))).Applicable);
        Assert.True((await Module.PreviewAsync(new OptimizationContext(Reports.Create(HardwareTier.LowEnd)))).Applicable);
        Assert.True((await Module.PreviewAsync(new OptimizationContext(Reports.Create(HardwareTier.LegacyLowResource)))).Applicable);

        var forced = new OptimizationContext(Reports.Create(HardwareTier.HighEnd));
        forced.Items[OptimizationIds.ForceItemKey] = true;
        Assert.True((await Module.PreviewAsync(forced)).Applicable);
    }

    [Fact]
    public async Task Apply_DisablesAnimationsShadowsSmoothScrollingTransparency_AndRestoresFullState()
    {
        var original = _h.Visual.State;
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));

        await Module.ApplyAsync(new OptimizationContext(Reports.Create(HardwareTier.Entry)), recorder);
        await rollback.CompleteSessionAsync(recorder.SessionId);

        var s = _h.Visual.State;
        Assert.False(s.ClientAreaAnimation || s.MenuAnimation || s.ComboBoxAnimation || s.TooltipAnimation || s.WindowMinMaxAnimation);
        Assert.False(s.CursorShadow);
        Assert.False(s.ListBoxSmoothScrolling);
        Assert.False(s.Transparency);
        Assert.True(s.DragFullWindows);

        await rollback.RestoreSessionAsync(recorder.SessionId);
        Assert.Equal(original, _h.Visual.State);
    }
}

public sealed class TemporaryFilesTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private IOptimization Module => _h.Get<IEnumerable<IOptimization>>().Single(o => o.Id == OptimizationIds.TemporaryFiles);

    [Fact]
    public async Task Preview_OneIrreversibleChangePerNonEmptySafeCategory()
    {
        var old = _h.Clock.UtcNow.AddDays(-30);
        _h.FileSystem.AddFile(@"C:\Users\Test\AppData\Local\Temp\a.tmp", 800 * ByteSize.MiB, old);
        _h.FileSystem.AddFile(@"C:\Windows\Temp\b.tmp", 10, old);
        _h.FileSystem.AddFile(@"C:\Users\Test\AppData\Local\D3DSCache\shader.bin", 10, old);

        var preview = await Module.PreviewAsync(new OptimizationContext(null));

        Assert.True(preview.Applicable);
        Assert.False(preview.Reversible);
        Assert.True(preview.RequiresElevation);
        Assert.Equal(ImpactLevel.Medium, preview.Impact);
        Assert.Equal(["temp-files:user-temp", "temp-files:windows-temp"], preview.Changes.Select(c => c.Id));
        Assert.All(preview.Changes, c =>
        {
            Assert.False(c.Reversible);
            Assert.True(c.SelectedByDefault);
        });
    }

    [Fact]
    public async Task Preview_NothingToClean_IsNotApplicable()
        => Assert.False((await Module.PreviewAsync(new OptimizationContext(null))).Applicable);

    [Fact]
    public async Task Apply_OnlySelectedCategories_RecordedIrreversible()
    {
        var old = _h.Clock.UtcNow.AddDays(-30);
        _h.FileSystem.AddFile(@"C:\Users\Test\AppData\Local\Temp\a.tmp", 100, old);
        _h.FileSystem.AddFile(@"C:\Users\Test\AppData\Local\Microsoft\Windows\INetCache\c.dat", 50, old);
        var recorder = await _h.Get<IRollbackManager>().BeginSessionAsync(SessionType.OneClick, TextRef.Literal("t"));

        var result = await Module.ApplyAsync(new OptimizationContext(null, new HashSet<string> { "temp-files:user-temp" }), recorder);

        Assert.Equal(100, result.BytesFreed);
        Assert.True(_h.FileSystem.FileExists(@"C:\Users\Test\AppData\Local\Microsoft\Windows\INetCache\c.dat"));
        var change = _h.History.AllChanges.Single();
        Assert.Equal(ChangeStatus.Irreversible, change.Status);
        Assert.Equal("cleanup:user-temp", change.Target);
        Assert.Empty(_h.Elevation.Requests);
    }
}

public sealed class StartupAppsTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private int _nextPid = 100;

    private IOptimization Module => _h.Get<IEnumerable<IOptimization>>().Single(o => o.Id == OptimizationIds.StartupApps);

    private void Entry(string name, string exe, string signer, long? runningWorkingSet = null, RegistryHiveKind hive = RegistryHiveKind.CurrentUser)
    {
        _h.AddSignedExe(exe, signer);
        _h.Registry.Set(hive, Run, name, RegistryValueData.String($"\"{exe}\""), hive == RegistryHiveKind.LocalMachine ? RegistryViewKind.Registry64 : RegistryViewKind.Default);
        if (runningWorkingSet is long ws) _h.Processes.Add(_nextPid++, PathName(exe), exe, ws);
    }

    private static string PathName(string exe) => exe[(exe.LastIndexOf('\\') + 1)..];

    [Fact]
    public async Task Preview_OnlyCanDisable_PreselectedWhenImpactHighOrMedium_NeverKeepOrSecurity()
    {
        Entry("Discord", @"C:\Apps\Discord\Discord.exe", "Discord Inc.", 400 * ByteSize.MiB);
        Entry("Spotify", @"C:\Apps\Spotify\Spotify.exe", "Spotify AB");
        Entry("SecurityHealth", @"C:\Windows\System32\SecurityHealthSystray.exe", "Microsoft Windows", 500 * ByteSize.MiB, RegistryHiveKind.LocalMachine);
        Entry("RtkAudUService", @"C:\Windows\System32\RtkAudUService64.exe", "Realtek Semiconductor Corp.", 500 * ByteSize.MiB, RegistryHiveKind.LocalMachine);
        Entry("OneDrive", @"C:\Apps\OneDrive\OneDrive.exe", "Microsoft Corporation", 500 * ByteSize.MiB);

        var preview = await Module.PreviewAsync(new OptimizationContext(null));

        Assert.True(preview.Applicable);
        Assert.Equal(2, preview.Changes.Count);
        Assert.True(preview.Changes.Single(c => c.Id.EndsWith("Discord")).SelectedByDefault);
        Assert.False(preview.Changes.Single(c => c.Id.EndsWith("Spotify")).SelectedByDefault);
        Assert.DoesNotContain(preview.Changes, c => c.Id.Contains("SecurityHealth") || c.Id.Contains("RtkAud") || c.Id.Contains("OneDrive"));
    }

    [Fact]
    public async Task Preview_MinimumImpactFilter_KeepsOnlyHighImpact()
    {
        Entry("Discord", @"C:\Apps\Discord\Discord.exe", "Discord Inc.", 400 * ByteSize.MiB);
        Entry("Steam", @"C:\Apps\Steam\steam.exe", "Valve Corp.", 150 * ByteSize.MiB);
        var context = new OptimizationContext(null);
        context.Items[OptimizationIds.StartupMinimumImpactItemKey] = StartupImpact.High;

        var preview = await Module.PreviewAsync(context);

        Assert.Single(preview.Changes);
        Assert.EndsWith("Discord", preview.Changes[0].Id);
    }

    [Fact]
    public async Task Apply_DisablesOnlySelectedEntries_ViaStartupApproved()
    {
        Entry("Discord", @"C:\Apps\Discord\Discord.exe", "Discord Inc.", 400 * ByteSize.MiB);
        Entry("Spotify", @"C:\Apps\Spotify\Spotify.exe", "Spotify AB");
        var recorder = await _h.Get<IRollbackManager>().BeginSessionAsync(SessionType.OneClick, TextRef.Literal("t"));

        var result = await Module.ApplyAsync(new OptimizationContext(null), recorder);

        Assert.Equal(1, result.ChangesApplied);
        var approved = StartupApproved.ApprovedLocation(StartupLocation.RegistryRunUser)!;
        Assert.False(StartupApproved.IsEnabled(_h.Registry.GetValue(approved, "Discord")));
        Assert.True(StartupApproved.IsEnabled(_h.Registry.GetValue(approved, "Spotify")));
        // L'entrée Run elle-même n'est jamais supprimée.
        Assert.NotNull(_h.Registry.GetValue(new RegistryLocation(RegistryHiveKind.CurrentUser, Run), "Discord"));
    }
}
