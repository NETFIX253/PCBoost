using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Gaming.Optimizations;
using PCBoost.Gaming.Settings;

namespace PCBoost.Gaming.Tests;

public sealed class WindowsGameSettingsTests
{
    private static readonly string GpuKey = GamingGpuPreferenceOptimization.PreferencesKey.KeyPath;

    [Fact]
    public async Task Game_mode_absent_is_enabled_and_zero_can_be_fixed_reversibly()
    {
        await using var h = new GamingHarness();
        var check = h.Checker.Check(null).Single(c => c.Id == GameSettingCheckIds.GameMode);
        Assert.True(check.IsRecommendedState);
        Assert.False(check.CanFixAutomatically);

        h.Registry.Set(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueData.DWord(0));
        check = h.Checker.Check(null).Single(c => c.Id == GameSettingCheckIds.GameMode);
        Assert.False(check.IsRecommendedState);
        Assert.True(check.CanFixAutomatically);

        var result = await h.Service.FixWindowsGameSettingAsync(GameSettingCheckIds.GameMode, null);

        Assert.True(result.Success);
        Assert.Equal(1, h.Registry.GetValue(WindowsGameSettingsChecker.GameBarKey, "AutoGameModeEnabled")!.Value);
        var session = Assert.Single(h.Rollback.Sessions);
        Assert.Equal(SessionType.Manual, session.Type);
        Assert.Contains(session.Id, h.Rollback.CompletedSessions);
        var change = Assert.Single(session.Changes);
        Assert.Equal(ChangeKinds.RegistryValue, change.Kind);
        Assert.Equal(0, ChangeStateSerializer.Deserialize<RegistryValueState>(change.BeforeState)!.NumericValue);

        await h.Rollback.RestoreSessionAsync(session.Id);
        Assert.Equal(0, h.Registry.GetValue(WindowsGameSettingsChecker.GameBarKey, "AutoGameModeEnabled")!.Value);
    }

    [Fact]
    public async Task Windowed_optimizations_fix_preserves_other_pairs()
    {
        await using var h = new GamingHarness();
        h.Registry.Set(RegistryHiveKind.CurrentUser, GpuKey, "DirectXUserGlobalSettings", RegistryValueData.String("VRROptimizeEnable=0;SwapEffectUpgradeEnable=0;"));
        var check = h.Checker.Check(null).Single(c => c.Id == GameSettingCheckIds.WindowedOptimizations);
        Assert.False(check.IsRecommendedState);

        var result = await h.Checker.FixAsync(GameSettingCheckIds.WindowedOptimizations, null);

        Assert.True(result.Success);
        Assert.Equal("VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;", h.Registry.GetValue(GamingGpuPreferenceOptimization.PreferencesKey, "DirectXUserGlobalSettings")!.Value);
        Assert.True(h.Checker.Check(null).Single(c => c.Id == GameSettingCheckIds.WindowedOptimizations).IsRecommendedState);
    }

    [Fact]
    public async Task Windowed_optimizations_are_only_checked_on_windows_11()
    {
        await using var h = new GamingHarness(x => x.SystemInfo.Os = x.SystemInfo.Os with { BuildNumber = 19045, IsWindows11 = false, ProductName = "Windows 10 Pro" });

        Assert.DoesNotContain(h.Checker.Check(null), c => c.Id == GameSettingCheckIds.WindowedOptimizations);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(null, true)]
    public async Task Hardware_gpu_scheduling_is_read_only(int? value, bool recommended)
    {
        await using var h = new GamingHarness();
        if (value is { } v)
            h.Registry.Set(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueData.DWord(v));
        var writesBefore = h.Registry.WriteCount;

        var check = h.Checker.Check(null).Single(c => c.Id == GameSettingCheckIds.HardwareGpuScheduling);
        var fix = await h.Checker.FixAsync(GameSettingCheckIds.HardwareGpuScheduling, null);

        Assert.Equal(recommended, check.IsRecommendedState);
        Assert.False(check.CanFixAutomatically);
        Assert.Equal(writesBefore, h.Registry.WriteCount);
        Assert.Empty(h.Rollback.Sessions);
        if (!recommended) Assert.Equal(OperationErrorKind.NotSupported, fix.Error);
    }

    [Fact]
    public async Task Gpu_preference_check_and_fix_for_hybrid_graphics()
    {
        await using var h = new GamingHarness(x => x.SystemInfo.Gpus =
        [
            new("AMD Radeon(TM) Graphics", GpuVendor.Amd, 512 * ByteSize.MiB, 8L * ByteSize.GiB, "31", false, true),
            new("NVIDIA GeForce RTX 3050 Laptop GPU", GpuVendor.Nvidia, 4L * ByteSize.GiB, 8L * ByteSize.GiB, "551", false, false),
            new("Microsoft Basic Render Driver", GpuVendor.Microsoft, 0, 0, null, true, false),
        ]);
        var check = h.Checker.Check(h.Game).Single(c => c.Id == GameSettingCheckIds.GpuPreference);
        Assert.False(check.IsRecommendedState);
        Assert.True(check.CanFixAutomatically);

        var result = await h.Service.FixWindowsGameSettingAsync(GameSettingCheckIds.GpuPreference, h.Game);

        Assert.True(result.Success);
        Assert.Equal("GpuPreference=2;", h.Registry.GetValue(GamingGpuPreferenceOptimization.PreferencesKey, GamingHarness.GameExe)!.Value);
        Assert.True(h.Checker.Check(h.Game).Single(c => c.Id == GameSettingCheckIds.GpuPreference).IsRecommendedState);
        Assert.Equal(SessionType.Manual, Assert.Single(h.Rollback.Sessions).Type);
    }

    [Fact]
    public async Task Gpu_preference_with_single_gpu_needs_nothing()
    {
        await using var h = new GamingHarness();

        var check = h.Checker.Check(h.Game).Single(c => c.Id == GameSettingCheckIds.GpuPreference);
        var fix = await h.Checker.FixAsync(GameSettingCheckIds.GpuPreference, h.Game);

        Assert.True(check.IsRecommendedState);
        Assert.False(check.CanFixAutomatically);
        Assert.Equal("Game_Setting_AlreadyOk", fix.Message!.Key);
        Assert.Empty(h.Rollback.Sessions);
    }

    [Fact]
    public async Task Unknown_check_is_reported()
    {
        await using var h = new GamingHarness();

        var fix = await h.Checker.FixAsync("inexistant", null);

        Assert.Equal(OperationErrorKind.NotFound, fix.Error);
    }
}
