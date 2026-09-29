using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Security;
using PCBoost.Core.Services;
using PCBoost.Gaming.Detection;
using PCBoost.Gaming.Optimizations;
using PCBoost.Gaming.Tests.Fakes;
using PCBoost.TestUtilities;

namespace PCBoost.Gaming.Tests;

public sealed class GamingOptimizationTests
{
    private const int GamePid = 5000;
    private const string GameDir = @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive";
    private const string GameExe = GameDir + @"\game\bin\win64\cs2.exe";

    private readonly FakeProcessProvider _processes = new();
    private readonly FakePowerProvider _power = new();
    private readonly FakeSystemInfoProvider _systemInfo = new();
    private readonly InMemoryRegistryProvider _registry = new();
    private readonly InMemoryFileSystemProvider _fs = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeForegroundWindowProvider _foreground = new();
    private readonly FakeClock _clock = new();
    private readonly FakeRollbackManager _rollback = new();
    private readonly GameSignatureDatabase _signatures;
    private readonly GamingOptions _options = new();
    private readonly FakeChangeRecorder _recorder = new(Guid.NewGuid());

    public GamingOptimizationTests()
    {
        _signatures = new GameSignatureDatabase(_fs);
        _rollback.Power = _power;
        _rollback.Processes = _processes;
        _rollback.Registry = _registry;
        _processes.Add(GamePid, "cs2.exe", GameExe, 3L * 1024 * 1024 * 1024, hasWindow: true);
    }

    private static OptimizationContext GameContext(IReadOnlySet<string>? selection = null)
    {
        var context = new OptimizationContext(null, selection);
        context.Items[GamingContextKeys.GameProcessId] = GamePid;
        context.Items[GamingContextKeys.GameExecutablePath] = GameExe;
        context.Items[GamingContextKeys.GameInstallDirectory] = GameDir;
        return context;
    }

    private IServiceProvider Services => new SimpleServiceProvider().Add<IRollbackManager>(_rollback);

    // ---------- Alimentation ----------

    [Fact]
    public async Task Power_switches_to_high_performance_and_records_previous_scheme()
    {
        var module = new GamingPowerOptimization(_power, _systemInfo);

        var preview = await module.PreviewAsync(GameContext());
        var result = await module.ApplyAsync(GameContext(), _recorder);

        Assert.True(preview.Applicable);
        Assert.True(Assert.Single(preview.Changes).SelectedByDefault);
        Assert.Equal(PowerScheme.HighPerformance, _power.ActiveId);
        Assert.Equal(1, result.ChangesApplied);
        var change = Assert.Single(_recorder.Changes);
        Assert.Equal(ChangeKinds.PowerScheme, change.Kind);
        Assert.Equal(PowerScheme.Balanced, ChangeStateSerializer.Deserialize<PowerSchemeState>(change.BeforeState)!.SchemeId);
        Assert.False(ForbiddenTargetPolicy.IsForbiddenTarget(change.Target));
    }

    [Fact]
    public async Task Power_prefers_ultimate_performance_when_present()
    {
        _power.Schemes.Add(new PowerScheme(PowerScheme.UltimatePerformance, "Performances optimales"));
        var module = new GamingPowerOptimization(_power, _systemInfo);

        await module.ApplyAsync(GameContext(), _recorder);

        Assert.Equal(PowerScheme.UltimatePerformance, _power.ActiveId);
    }

    [Fact]
    public async Task Power_is_not_applicable_when_already_active_or_absent()
    {
        var module = new GamingPowerOptimization(_power, _systemInfo);
        _power.ActiveId = PowerScheme.HighPerformance;
        Assert.False((await module.PreviewAsync(GameContext())).Applicable);

        _power.ActiveId = PowerScheme.Balanced;
        _power.Schemes.RemoveAll(s => s.Id == PowerScheme.HighPerformance);
        var preview = await module.PreviewAsync(GameContext());
        Assert.False(preview.Applicable);
        Assert.Equal("Game_Power_NoPerformancePlan", preview.NotApplicableReason!.Key);
        Assert.Equal(0, (await module.ApplyAsync(GameContext(), _recorder)).ChangesApplied);
        Assert.Empty(_recorder.Changes);
    }

    [Fact]
    public async Task Power_on_battery_is_proposed_but_not_selected_by_default()
    {
        _systemInfo.Power = new PowerStatus(PowerSource.Battery, 60, true);
        var module = new GamingPowerOptimization(_power, _systemInfo);

        var preview = await module.PreviewAsync(GameContext());
        var result = await module.ApplyAsync(GameContext(), _recorder);

        var change = Assert.Single(preview.Changes);
        Assert.False(change.SelectedByDefault);
        Assert.Equal("Game_Power_ChangeOnBattery", change.Description.Key);
        Assert.Equal(0, result.ChangesApplied);
        Assert.Equal(PowerScheme.Balanced, _power.ActiveId);

        // Sélection explicite de l'utilisateur : appliqué.
        await module.ApplyAsync(GameContext(new HashSet<string> { change.Id }), _recorder);
        Assert.Equal(PowerScheme.HighPerformance, _power.ActiveId);
    }

    // ---------- Priorité ----------

    private GamingPriorityOptimization Priority() => new(_processes, _processes, new CriticalProcessProtection(), _signatures, Services);

    [Fact]
    public async Task Priority_is_raised_to_above_normal_never_higher()
    {
        var result = await Priority().ApplyAsync(GameContext(), _recorder);

        Assert.Equal(ProcessPriority.AboveNormal, _processes.GetProcess(GamePid)!.Priority);
        Assert.Equal(ProcessPriority.AboveNormal, GamingPriorityOptimization.TargetPriority);
        Assert.Equal(1, result.ChangesApplied);
        var state = ChangeStateSerializer.Deserialize<ProcessPriorityState>(Assert.Single(_recorder.Changes).BeforeState)!;
        Assert.Equal(ProcessPriority.Normal, state.Priority);
        Assert.Equal(GamePid, state.ProcessId);
        Assert.Equal("cs2.exe", state.ProcessName);
    }

    [Theory]
    [InlineData(ProcessPriority.AboveNormal)]
    [InlineData(ProcessPriority.High)]
    [InlineData(ProcessPriority.RealTime)]
    public async Task Priority_never_lowers_or_changes_an_already_high_priority(ProcessPriority current)
    {
        _processes.Update(GamePid, p => p with { Priority = current });

        var preview = await Priority().PreviewAsync(GameContext());
        await Priority().ApplyAsync(GameContext(), _recorder);

        Assert.False(preview.Applicable);
        Assert.Equal(current, _processes.GetProcess(GamePid)!.Priority);
        Assert.Empty(_recorder.Changes);
    }

    [Fact]
    public async Task Priority_access_denied_is_ignored_cleanly()
    {
        _processes.AccessDeniedProcessIds.Add(GamePid);

        var result = await Priority().ApplyAsync(GameContext(), _recorder);

        Assert.True(result.Outcome.Success);
        Assert.Equal(0, result.ChangesApplied);
        Assert.Equal(0, result.ChangesFailed);
        Assert.Equal("Game_Priority_Protected", Assert.Single(result.Messages).Key);
        Assert.Empty(_recorder.Changes);
        Assert.Equal(ProcessPriority.Normal, _processes.GetProcess(GamePid)!.Priority);
    }

    [Fact]
    public async Task Priority_needs_a_running_game_and_skips_anti_cheat_or_protected_processes()
    {
        Assert.False((await Priority().PreviewAsync(new OptimizationContext(null))).Applicable);

        _processes.Add(6000, "vgc.exe", @"C:\Program Files\Riot Vanguard\vgc.exe");
        var context = new OptimizationContext(null);
        context.Items[GamingContextKeys.GameProcessId] = 6000;
        Assert.False((await Priority().PreviewAsync(context)).Applicable);

        context.Items[GamingContextKeys.GameProcessId] = 99999;
        Assert.Equal("Game_Priority_GameNotRunning", (await Priority().PreviewAsync(context)).NotApplicableReason!.Key);
    }

    // ---------- Arrière-plan ----------

    private GamingBackgroundOptimization Background() => new(_processes, _processes, new CriticalProcessProtection(), _foreground, _systemInfo,
        _settings, _fs, _signatures, _clock, _options, Services);

    private void SetupBackgroundProcesses()
    {
        _processes.Add(100, "OneDrive.exe", @"C:\Users\Test\AppData\Local\Microsoft\OneDrive\OneDrive.exe");
        _processes.Add(101, "busyhelper.exe", @"C:\Program Files\Vendor\busyhelper.exe", cpu: TimeSpan.FromSeconds(10));
        _processes.Add(102, "idlehelper.exe", @"C:\Program Files\Vendor\idlehelper.exe", cpu: TimeSpan.FromSeconds(10));
        _processes.Add(103, "Discord.exe", @"C:\Users\Test\AppData\Local\Discord\app-1.0\Discord.exe", cpu: TimeSpan.FromSeconds(10));
        _processes.Add(104, "vgc.exe", @"C:\Program Files\Riot Vanguard\vgc.exe", cpu: TimeSpan.FromSeconds(10));
        _processes.Add(105, "EasyAntiCheat.exe", @"C:\Program Files (x86)\EasyAntiCheat\EasyAntiCheat.exe", cpu: TimeSpan.FromSeconds(10));
        _processes.Add(106, "steamwebhelper.exe", @"C:\Program Files (x86)\Steam\bin\cef\steamwebhelper.exe", cpu: TimeSpan.FromSeconds(10));
        _processes.Add(107, "csgo_helper.exe", GameDir + @"\game\bin\win64\csgo_helper.exe", cpu: TimeSpan.FromSeconds(10));
        _processes.Add(108, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", hasWindow: true);
        _processes.Add(109, "nvcontainer.exe", @"C:\Program Files\NVIDIA Corporation\NvContainer\nvcontainer.exe", cpu: TimeSpan.FromSeconds(10));
        _processes.Add(110, "svchost.exe", @"C:\Windows\System32\svchost.exe", cpu: TimeSpan.FromSeconds(10));
        _processes.Add(111, "otheruser.exe", @"C:\Program Files\X\otheruser.exe", cpu: TimeSpan.FromSeconds(10), currentUser: false);
        _processes.Add(112, "firefox.exe", @"C:\Program Files\Mozilla Firefox\firefox.exe", hasWindow: true);
        _processes.Add(113, "dropbox.exe", @"C:\Program Files\Dropbox\Client\Dropbox.exe", priority: ProcessPriority.BelowNormal);

        // Pendant la mesure : chaque processus « occupé » consomme 0,5 s de CPU (8 cœurs, 1 s → 6,25 %).
        var busy = new[] { 101, 103, 104, 105, 106, 107, 109, 110, 111 };
        _options.BackgroundCpuSampleDuration = TimeSpan.FromSeconds(1);
        _options.Delay = (_, _) =>
        {
            foreach (var pid in busy) _processes.Update(pid, p => p with { TotalProcessorTime = p.TotalProcessorTime + TimeSpan.FromMilliseconds(500) });
            _processes.Update(GamePid, p => p with { TotalProcessorTime = p.TotalProcessorTime + TimeSpan.FromSeconds(4) });
            return Task.CompletedTask;
        };
        _foreground.ForegroundProcessId = 112; // Firefox au premier plan
    }

    [Fact]
    public async Task Background_selects_non_essential_processes_and_respects_every_exclusion()
    {
        SetupBackgroundProcesses();

        var preview = await Background().PreviewAsync(GameContext());

        Assert.True(preview.Applicable);
        var targets = preview.Changes.Select(c => c.Target).ToList();
        Assert.Contains(targets, t => t.Contains("OneDrive.exe", StringComparison.Ordinal));
        Assert.Contains(targets, t => t.Contains("busyhelper.exe", StringComparison.Ordinal));
        Assert.Contains(targets, t => t.Contains("chrome.exe", StringComparison.Ordinal));
        Assert.Equal(3, targets.Count);
        Assert.All(preview.Changes, c => Assert.True(c.SelectedByDefault));
        // idlehelper (< 2 % CPU), Discord (exclusion par défaut), vgc / EasyAntiCheat (anti-triche), steamwebhelper (lanceur),
        // csgo_helper (processus du jeu), nvcontainer (pilote), svchost (critique), autre utilisateur, Firefox (premier plan),
        // Dropbox (priorité déjà abaissée) et le jeu lui-même sont exclus.
    }

    [Fact]
    public async Task Background_uses_efficiency_mode_when_supported_and_records_state()
    {
        SetupBackgroundProcesses();
        var module = Background();
        await module.PreviewAsync(GameContext());

        var result = await module.ApplyAsync(GameContext(), _recorder);

        Assert.Equal(3, result.ChangesApplied);
        Assert.All(_recorder.Changes, c => Assert.Equal(ChangeKinds.ProcessEfficiency, c.Kind));
        Assert.True(_processes.GetEfficiencyMode(100).Value);
        Assert.False(_processes.GetEfficiencyMode(103).Value);
        Assert.False(_processes.GetEfficiencyMode(GamePid).Value);
        var state = ChangeStateSerializer.Deserialize<ProcessEfficiencyState>(_recorder.Changes[0].BeforeState)!;
        Assert.False(state.Enabled);
    }

    [Fact]
    public async Task Background_falls_back_to_below_normal_priority_without_efficiency_mode()
    {
        SetupBackgroundProcesses();
        _processes.IsEfficiencyModeSupported = false;

        await Background().ApplyAsync(GameContext(), _recorder);

        Assert.All(_recorder.Changes, c => Assert.Equal(ChangeKinds.ProcessPriority, c.Kind));
        Assert.Equal(ProcessPriority.BelowNormal, _processes.GetProcess(100)!.Priority);
        Assert.Equal(ProcessPriority.Normal, _processes.GetProcess(103)!.Priority);
        Assert.Equal(ProcessPriority.Normal, _processes.GetProcess(GamePid)!.Priority);
    }

    [Fact]
    public async Task Background_honours_user_exclusions_and_the_fifteen_process_limit()
    {
        _settings.Current.Gaming.BackgroundExclusions.Add("OneDrive");
        for (var i = 0; i < 25; i++) _processes.Add(200 + i, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        _processes.Add(300, "OneDrive.exe", @"C:\Users\Test\AppData\Local\Microsoft\OneDrive\OneDrive.exe");
        _options.Delay = (_, _) => Task.CompletedTask;

        var preview = await Background().PreviewAsync(GameContext());

        Assert.Equal(15, preview.Changes.Count);
        Assert.DoesNotContain(preview.Changes, c => c.Target.Contains("OneDrive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Background_skips_processes_that_became_foreground_or_denied_access()
    {
        SetupBackgroundProcesses();
        var module = Background();
        await module.PreviewAsync(GameContext());
        _foreground.ForegroundProcessId = 108; // Chrome passe au premier plan
        _processes.AccessDeniedProcessIds.Add(101);

        var result = await module.ApplyAsync(GameContext(), _recorder);

        Assert.Equal(1, result.ChangesApplied);
        Assert.Equal(0, result.ChangesFailed);
        Assert.Contains(result.Messages, m => m.Key == "Game_Background_Skipped");
        Assert.False(_processes.GetEfficiencyMode(108).Value);
    }

    [Fact]
    public async Task Background_rollback_undoes_only_its_changes_in_reverse_order()
    {
        SetupBackgroundProcesses();
        var recorder = (FakeChangeRecorder)await _rollback.BeginSessionAsync(SessionType.Gaming, TextRef.Of("t"));
        await new GamingPowerOptimization(_power, _systemInfo).ApplyAsync(GameContext(), recorder);
        var module = Background();
        await module.ApplyAsync(GameContext(), recorder);

        var rollback = await module.RollbackAsync(recorder.SessionId);

        Assert.Equal(3, rollback.Restored);
        Assert.False(_processes.GetEfficiencyMode(100).Value);
        Assert.Equal(PowerScheme.HighPerformance, _power.ActiveId); // l'autre module n'est pas touché
        var undone = recorder.Changes.Where(c => c.Status == ChangeStatus.RolledBack).OrderByDescending(c => c.Sequence).Select(c => c.Id).ToList();
        Assert.Equal(undone, _rollback.UndoneChanges);
    }

    // ---------- Préférence GPU ----------

    private GamingGpuPreferenceOptimization Gpu() => new(_registry, _systemInfo, Services);

    private void TwoGpus() => _systemInfo.Gpus =
    [
        new("Intel(R) UHD Graphics 620", GpuVendor.Intel, 128 * ByteSize.MiB, 4L * ByteSize.GiB, "31", false, true),
        new("NVIDIA GeForce MX150", GpuVendor.Nvidia, 2L * ByteSize.GiB, 4L * ByteSize.GiB, "551", false, false),
    ];

    [Fact]
    public async Task Gpu_preference_is_not_applicable_with_a_single_gpu()
    {
        var preview = await Gpu().PreviewAsync(GameContext());

        Assert.False(preview.Applicable);
        Assert.Equal("Game_Gpu_SingleGpu", preview.NotApplicableReason!.Key);
    }

    [Fact]
    public async Task Gpu_preference_is_never_selected_by_default()
    {
        TwoGpus();
        var preview = await Gpu().PreviewAsync(GameContext());
        var result = await Gpu().ApplyAsync(GameContext(), _recorder);

        Assert.True(preview.Applicable);
        Assert.False(Assert.Single(preview.Changes).SelectedByDefault);
        Assert.Equal(0, result.ChangesApplied);
        Assert.Null(_registry.GetValue(GamingGpuPreferenceOptimization.PreferencesKey, GameExe));
    }

    [Fact]
    public async Task Gpu_preference_explicitly_selected_writes_documented_value_and_preserves_other_pairs()
    {
        TwoGpus();
        _registry.Set(RegistryHiveKind.CurrentUser, GamingGpuPreferenceOptimization.PreferencesKey.KeyPath, GameExe, RegistryValueData.String("SwapEffectUpgradeEnable=1;GpuPreference=1;"));
        var change = Assert.Single((await Gpu().PreviewAsync(GameContext())).Changes);

        var result = await Gpu().ApplyAsync(GameContext(new HashSet<string> { change.Id }), _recorder);

        Assert.Equal(1, result.ChangesApplied);
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=2;", _registry.GetValue(GamingGpuPreferenceOptimization.PreferencesKey, GameExe)!.Value);
        var state = ChangeStateSerializer.Deserialize<RegistryValueState>(Assert.Single(_recorder.Changes).BeforeState)!;
        Assert.True(state.Existed);
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=1;", state.StringValue);
        Assert.Equal(ChangeKinds.RegistryValue, _recorder.Changes[0].Kind);
        Assert.StartsWith(@"HKCU\", _recorder.Changes[0].Target, StringComparison.Ordinal);

        Assert.False((await Gpu().PreviewAsync(GameContext())).Applicable);
    }

    [Fact]
    public async Task Gpu_preference_new_value_is_removed_on_undo()
    {
        TwoGpus();
        var recorder = (FakeChangeRecorder)await _rollback.BeginSessionAsync(SessionType.Manual, TextRef.Of("t"));
        var change = Assert.Single((await Gpu().PreviewAsync(GameContext())).Changes);
        await Gpu().ApplyAsync(GameContext(new HashSet<string> { change.Id }), recorder);
        Assert.Equal("GpuPreference=2;", _registry.GetValue(GamingGpuPreferenceOptimization.PreferencesKey, GameExe)!.Value);

        await Gpu().RollbackAsync(recorder.SessionId);

        Assert.Null(_registry.GetValue(GamingGpuPreferenceOptimization.PreferencesKey, GameExe));
    }

    [Fact]
    public void Modules_expose_expected_metadata()
    {
        IOptimization[] modules = [new GamingPowerOptimization(_power, _systemInfo), Priority(), Background(), Gpu()];

        Assert.Equal(GamingOptimizationIds.All, modules.Select(m => m.Id));
        Assert.All(modules, m => Assert.True(m.IsReversible));
        Assert.Equal([OptimizationCategory.Power, OptimizationCategory.Cpu, OptimizationCategory.Background, OptimizationCategory.Gpu], modules.Select(m => m.Category));
        Assert.Equal([RiskLevel.Low, RiskLevel.Low, RiskLevel.Medium, RiskLevel.Low], modules.Select(m => m.RiskLevel));
    }

    [Fact]
    public async Task Rollback_without_rollback_manager_reports_it()
    {
        var module = new GamingPowerOptimization(_power, _systemInfo);

        var result = await module.RollbackAsync(Guid.NewGuid());

        Assert.Equal("Game_RollbackUnavailable", Assert.Single(result.Errors).Message!.Key);
    }
}
