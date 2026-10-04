using PCBoost.Core.Drivers;
using PCBoost.Core.Models.Drivers;
using PCBoost.Core.Models.Health;
using PCBoost.TestUtilities;

namespace PCBoost.Core.Tests.Drivers;

public sealed class DriverUpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);

    private static DriverUpdateCandidate Evaluate(DriverUpdateOffer offer, params InstalledDriver[] devices)
        => DriverUpdatePolicy.Evaluate(offer, devices, Now);

    [Fact]
    public void Newer_automatic_mature_update_is_recommended_and_preselected()
    {
        var candidate = Evaluate(DriverSamples.Offer(), DriverSamples.Device());

        Assert.Equal(DriverUpdateTier.Recommended, candidate.Tier);
        Assert.True(candidate.SelectedByDefault);
        Assert.True(candidate.CanInstall);
        Assert.Empty(candidate.Reasons);
        Assert.Equal("Intel(R) UHD Graphics 620", candidate.Device!.DeviceName);
    }

    [Theory]
    [InlineData("27.20.100.8681")] // identique
    [InlineData("26.20.100.7000")] // plus ancienne
    public void Same_or_older_version_from_same_publisher_is_never_installed(string offered)
    {
        var candidate = Evaluate(DriverSamples.Offer(version: offered, date: new DateOnly(2024, 1, 1)), DriverSamples.Device());

        Assert.Equal(DriverUpdateTier.Excluded, candidate.Tier);
        Assert.Contains(DriverUpdateReason.NotNewer, candidate.Reasons);
        Assert.False(candidate.CanInstall);
    }

    [Fact]
    public void Different_publishers_are_compared_by_date_not_by_version_number()
    {
        // Pilote générique Microsoft 10.0.x de 2006 contre pilote Realtek 6.0.x de 2023 : plus récent malgré un numéro plus petit.
        var device = DriverSamples.Device(provider: "Microsoft", version: "10.0.19041.1", date: new DateOnly(2006, 6, 21), inf: "hdaudio.inf");
        var newer = Evaluate(DriverSamples.Offer(provider: "Realtek Semiconductor Corp.", version: "6.0.9235.1", date: new DateOnly(2023, 1, 10)), device);
        var older = Evaluate(DriverSamples.Offer(provider: "Realtek Semiconductor Corp.", version: "11.0.0.1", date: new DateOnly(2005, 1, 10)), device);

        Assert.Equal(DriverUpdateTier.Recommended, newer.Tier);
        Assert.Equal(DriverUpdateTier.Excluded, older.Tier);
        Assert.Contains(DriverUpdateReason.NotNewer, older.Reasons);
    }

    [Fact]
    public void Unknown_comparison_is_review_not_preselected()
    {
        var device = DriverSamples.Device(provider: "Microsoft", version: null) with { Date = null };
        var candidate = Evaluate(DriverSamples.Offer(provider: "Intel Corporation"), device);

        Assert.Equal(DriverUpdateTier.Review, candidate.Tier);
        Assert.Contains(DriverUpdateReason.VersionNotComparable, candidate.Reasons);
        Assert.False(candidate.SelectedByDefault);
        Assert.True(candidate.CanInstall);
    }

    [Fact]
    public void Extension_inf_is_never_compared_with_the_base_driver_version()
    {
        var candidate = Evaluate(DriverSamples.Offer(driverClass: "Extension", version: "1.0.0.5"), DriverSamples.Device());

        Assert.Equal(DriverUpdateTier.Review, candidate.Tier);
        Assert.Contains(DriverUpdateReason.VersionNotComparable, candidate.Reasons);
    }

    [Fact]
    public void Device_without_driver_gets_the_update_as_newer_with_problem_note()
    {
        var device = DriverSamples.Device(version: null, provider: null, inf: null, problem: 28) with { Date = null };
        var candidate = Evaluate(DriverSamples.Offer(optional: true), device);

        Assert.Equal(DriverUpdateTier.Review, candidate.Tier);
        Assert.Equal([DriverUpdateReason.Optional, DriverUpdateReason.DeviceHasProblem], candidate.Reasons);
    }

    [Theory]
    [InlineData("Firmware", "Intel Corporation - Firmware - 1.2.3.4")]
    [InlineData("System", "Dell - BIOS Update - 1.15.0")]
    [InlineData("System", "Lenovo Ltd. - UEFI - 2.3.0.0")]
    [InlineData(null, "Surface - Firmware - 7.0.0.1")]
    public void Firmware_is_always_excluded(string? driverClass, string title)
    {
        var candidate = Evaluate(DriverSamples.Offer(driverClass: driverClass, title: title), DriverSamples.Device());

        Assert.Equal(DriverUpdateTier.Excluded, candidate.Tier);
        Assert.Equal([DriverUpdateReason.Firmware], candidate.Reasons);
    }

    [Fact]
    public void Biostar_board_is_not_mistaken_for_a_bios()
        => Assert.False(DriverUpdatePolicy.IsFirmware(DriverSamples.Offer(title: "BIOSTAR - System - 1.0.0.1", driverClass: "System")));

    [Fact]
    public void Optional_recent_and_system_critical_updates_are_review()
    {
        var optional = Evaluate(DriverSamples.Offer(optional: true), DriverSamples.Device());
        var recent = Evaluate(DriverSamples.Offer(published: Now.AddDays(-3)), DriverSamples.Device());
        var storage = Evaluate(DriverSamples.Offer(driverClass: "SCSIAdapter"), DriverSamples.Device(deviceClass: "SCSIAdapter"));
        var tpm = Evaluate(DriverSamples.Offer(driverClass: "SecurityDevices"), DriverSamples.Device(deviceClass: "SecurityDevices"));

        Assert.Equal([DriverUpdateReason.Optional], optional.Reasons);
        Assert.Equal([DriverUpdateReason.RecentlyPublished], recent.Reasons);
        Assert.Equal([DriverUpdateReason.SystemCritical], storage.Reasons);
        Assert.Equal([DriverUpdateReason.SystemCritical], tpm.Reasons);
        Assert.All([optional, recent, storage, tpm], c => Assert.Equal(DriverUpdateTier.Review, c.Tier));
    }

    [Fact]
    public void Maturity_period_is_fourteen_days()
    {
        Assert.Equal(TimeSpan.FromDays(14), DriverUpdatePolicy.MaturityPeriod);
        Assert.True(DriverUpdatePolicy.IsRecentlyPublished(DriverSamples.Offer(published: Now.AddDays(-13)), Now));
        Assert.False(DriverUpdatePolicy.IsRecentlyPublished(DriverSamples.Offer(published: Now.AddDays(-15)), Now));
        Assert.False(DriverUpdatePolicy.IsRecentlyPublished(DriverSamples.Offer() with { PublishedAt = null }, Now));
    }

    [Fact]
    public void User_input_and_unaccepted_license_are_excluded()
    {
        var input = Evaluate(DriverSamples.Offer(userInput: true), DriverSamples.Device());
        var eula = Evaluate(DriverSamples.Offer(eula: false), DriverSamples.Device());

        Assert.Equal(DriverUpdateTier.Excluded, input.Tier);
        Assert.Contains(DriverUpdateReason.RequiresUserInput, input.Reasons);
        Assert.Equal(DriverUpdateTier.Excluded, eula.Tier);
        Assert.Contains(DriverUpdateReason.LicenseNotAccepted, eula.Reasons);
    }

    [Fact]
    public void Missing_device_is_excluded_because_its_state_cannot_be_recorded()
    {
        var candidate = Evaluate(DriverSamples.Offer(hardwareId: @"USB\VID_8087&PID_0A2B"), DriverSamples.Device());

        Assert.Null(candidate.Device);
        Assert.Equal(DriverUpdateTier.Excluded, candidate.Tier);
        Assert.False(candidate.CanInstall);
        Assert.Contains(DriverUpdateReason.DeviceNotFound, candidate.Reasons);
    }

    [Fact]
    public void Targeted_rollback_needs_a_main_driver_a_known_new_version_and_a_known_previous_version()
    {
        Assert.True(DriverUpdatePolicy.SupportsTargetedRollback(Evaluate(DriverSamples.Offer(), DriverSamples.Device())));
        // Extension ou composant logiciel : « Restaurer le pilote » viserait le pilote principal.
        Assert.False(DriverUpdatePolicy.SupportsTargetedRollback(Evaluate(DriverSamples.Offer(driverClass: "Extension"), DriverSamples.Device())));
        Assert.False(DriverUpdatePolicy.SupportsTargetedRollback(Evaluate(DriverSamples.Offer(driverClass: "SoftwareComponent"), DriverSamples.Device())));
        // Version installée inconnue, ou aucun pilote précédent.
        Assert.False(DriverUpdatePolicy.SupportsTargetedRollback(Evaluate(DriverSamples.Offer() with { Version = null }, DriverSamples.Device())));
        var noDriver = DriverSamples.Device(version: null, provider: null, inf: null, problem: 28) with { Date = null };
        Assert.False(DriverUpdatePolicy.SupportsTargetedRollback(Evaluate(DriverSamples.Offer(), noDriver)));
        // Un seul périphérique sans pilote précédent suffit : il resterait sur le nouveau pilote après l'annulation.
        var second = DriverSamples.Device(instanceId: @"PCI\VEN_8086&DEV_5917&SUBSYS_00000000&REV_07\3&11583659&0&18");
        Assert.True(DriverUpdatePolicy.SupportsTargetedRollback(Evaluate(DriverSamples.Offer(), DriverSamples.Device(), second)));
        Assert.False(DriverUpdatePolicy.SupportsTargetedRollback(Evaluate(DriverSamples.Offer(), noDriver, second)));
    }

    [Fact]
    public void Updates_for_the_same_device_are_detected()
    {
        var gpu = Evaluate(DriverSamples.Offer(), DriverSamples.Device());
        var extension = Evaluate(DriverSamples.Offer(updateId: "6f1e2d3c-4b5a-4968-8776-5a4b3c2d1e0f", driverClass: "Extension"), DriverSamples.Device());
        var wifi = Evaluate(DriverSamples.Offer(updateId: "6f1e2d3c-4b5a-4968-8776-5a4b3c2d1e0f", hardwareId: @"PCI\VEN_8086&DEV_24FD"),
            DriverSamples.Device(instanceId: @"PCI\VEN_8086&DEV_24FD\4&2", hardwareId: @"PCI\VEN_8086&DEV_24FD"));

        Assert.True(DriverUpdatePolicy.SharesDevice([gpu, extension]));
        Assert.False(DriverUpdatePolicy.SharesDevice([gpu, wifi]));
        Assert.False(DriverUpdatePolicy.SharesDevice([]));
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(0, 10, 10)]
    [InlineData(0, 14, null)]
    [InlineData(22, 10, 10)]
    [InlineData(28, 0, null)]
    [InlineData(28, 28, null)]
    [InlineData(28, 43, 43)]
    public void Only_new_problems_other_than_restart_needed_stop_the_installation(int before, int after, int? expected)
        => Assert.Equal(expected, DriverUpdatePolicy.NewProblemCode(before, after));

    [Fact]
    public void Rollback_touches_a_device_only_while_it_is_on_the_installed_version()
    {
        var target = new DriverRollbackTarget(@"PCI\VEN_1&DEV_2\3&1", "27.20.100.8681");
        InstalledDriver On(string? version) => DriverSamples.Device(instanceId: target.InstanceId, version: version);

        Assert.Null(DriverUpdatePolicy.RollbackSkip(target, On("31.0.101.2125"), "31.0.101.2125"));
        Assert.Null(DriverUpdatePolicy.RollbackSkip(target, On("31.0.101.02125"), "31.0.101.2125"));
        Assert.True(DriverUpdatePolicy.SameVersion("31.0.101", "31.0.101.0"));
        Assert.False(DriverUpdatePolicy.SameVersion(null, null));
        Assert.Equal(DriverRollbackStatus.AlreadyPrevious, DriverUpdatePolicy.RollbackSkip(target, On("27.20.100.8681"), "31.0.101.2125"));
        Assert.Equal(DriverRollbackStatus.Changed, DriverUpdatePolicy.RollbackSkip(target, On("32.0.101.6000"), "31.0.101.2125"));
        Assert.Equal(DriverRollbackStatus.Changed, DriverUpdatePolicy.RollbackSkip(target, On(null), "31.0.101.2125"));
        Assert.Equal(DriverRollbackStatus.DeviceGone, DriverUpdatePolicy.RollbackSkip(target, null, "31.0.101.2125"));
    }

    [Fact]
    public void Devices_are_matched_by_hardware_id_before_compatible_id_and_capped()
    {
        var exact = DriverSamples.Device(instanceId: @"PCI\X\1", hardwareId: @"PCI\VEN_1&DEV_2");
        var compatible = new InstalledDriver(@"PCI\X\2", "Autre", "Display", [@"PCI\VEN_9&DEV_9"], [@"PCI\VEN_1&DEV_2"], "1.0", null, null, null, 0);
        var many = Enumerable.Range(0, 12).Select(i => DriverSamples.Device(instanceId: $@"PCI\Y\{i:D2}", hardwareId: @"PCI\VEN_1&DEV_2")).ToArray();

        var matched = DriverUpdatePolicy.MatchDevices(DriverSamples.Offer(hardwareId: @"pci\ven_1&dev_2"), [compatible, exact]);
        var capped = DriverUpdatePolicy.MatchDevices(DriverSamples.Offer(hardwareId: @"PCI\VEN_1&DEV_2"), many);

        Assert.Equal([@"PCI\X\1", @"PCI\X\2"], matched.Select(d => d.InstanceId));
        Assert.Equal(DriverUpdatePolicy.MaxDevicesPerUpdate, capped.Count);
    }

    [Theory]
    [InlineData("Intel", "Intel Corporation", true)]
    [InlineData("Realtek Semiconductor Corp.", "Realtek", true)]
    [InlineData("Advanced Micro Devices, Inc.", "AMD", false)]
    [InlineData(null, "Intel", false)]
    [InlineData("  ", "  ", false)]
    public void Publishers_compare_on_first_word(string? a, string? b, bool same) => Assert.Equal(same, DriverUpdatePolicy.SameProvider(a, b));
}

public sealed class DriverIdentifiersTests
{
    [Theory]
    [InlineData("Intel Corporation - Display - 31.0.101.4502", "31.0.101.4502")]
    [InlineData("Realtek Semiconductor Corp. - Extension - 10.0.22621.31248", "10.0.22621.31248")]
    [InlineData("Synaptics - Mouse - 19.5.35.1", "19.5.35.1")]
    [InlineData("Intel - net - 22.200.0.6", "22.200.0.6")]
    [InlineData("Intel - Display - 99999.0", null)]
    [InlineData("Pilote sans version", null)]
    [InlineData("Version 70000.1.1.1", null)]
    [InlineData(null, null)]
    public void Version_is_read_from_the_end_of_the_title(string? title, string? expected) => Assert.Equal(expected, DriverIdentifiers.VersionFromTitle(title));

    [Theory]
    [InlineData("1.2.3.4", true)]
    [InlineData("10.0", true)]
    [InlineData("65535.65535.65535.65535", true)]
    [InlineData("65536.0", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("1.a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Versions_are_validated(string? version, bool valid) => Assert.Equal(valid, DriverIdentifiers.IsValidVersion(version));

    [Fact]
    public void Versions_compare_numerically()
    {
        Assert.True(DriverIdentifiers.TryParseVersion("31.0.101.10", out var a));
        Assert.True(DriverIdentifiers.TryParseVersion("31.0.101.9", out var b));
        Assert.True(a > b);
    }

    [Theory]
    [InlineData(@"PCI\VEN_8086&DEV_5917&SUBSYS_00000000&REV_07\3&11583659&0&10", true)]
    [InlineData(@"SWD\MMDEVAPI\{0.0.0.00000000}.{5a2e9c1d-8f27-4b3c-9f1a-2d3c4b5a6e7f}", true)]
    [InlineData(@"ROOT\SYSTEM\0001", true)]
    [InlineData("SansSeparateur", false)]
    [InlineData(@"\debut", false)]
    [InlineData(@"fin\", false)]
    [InlineData("PCI\\VEN|1", false)]
    [InlineData("PCI\\\"VEN", false)]
    [InlineData(" PCI\\VEN", false)]
    [InlineData("PCI\\VEN\u00e9", false)]
    [InlineData("PCI\\VEN\n", false)]
    public void Instance_ids_are_validated(string id, bool valid) => Assert.Equal(valid, DriverIdentifiers.IsValidInstanceId(id));

    [Fact]
    public void Instance_ids_longer_than_the_windows_limit_are_refused()
        => Assert.False(DriverIdentifiers.IsValidInstanceId("PCI\\" + new string('A', 200)));

    [Theory]
    [InlineData("oem42.inf", true)]
    [InlineData("netrtwlane.inf", true)]
    [InlineData("OEM7.INF", true)]
    [InlineData(@"..\oem42.inf", false)]
    [InlineData(@"C:\Windows\INF\oem42.inf", false)]
    [InlineData("oem42.inf:stream", false)]
    [InlineData("oem42.exe", false)]
    [InlineData("", false)]
    public void Inf_names_are_file_names_only(string name, bool valid) => Assert.Equal(valid, DriverIdentifiers.IsValidInfName(name));

    [Theory]
    [InlineData("0c3a9a55-5b0f-4f1d-9b3e-2a6b7f0c1d2e", true)]
    [InlineData("0C3A9A55-5B0F-4F1D-9B3E-2A6B7F0C1D2E", true)]
    [InlineData("0c3a9a555b0f4f1d9b3e2a6b7f0c1d2e", false)]
    [InlineData("{0c3a9a55-5b0f-4f1d-9b3e-2a6b7f0c1d2e}", false)]
    [InlineData("pas-un-guid", false)]
    public void Update_ids_are_guids(string id, bool valid) => Assert.Equal(valid, DriverIdentifiers.IsValidUpdateId(id));
}

public sealed class DriverElevatedDataTests
{
    private const string A = "0c3a9a55-5b0f-4f1d-9b3e-2a6b7f0c1d2e";
    private const string B = "6f1e2d3c-4b5a-4968-8776-5a4b3c2d1e0f";

    [Fact]
    public void Install_parameters_round_trip()
    {
        var parameters = DriverElevatedData.InstallParameters([A, B], enableProtection: true);

        Assert.Equal($"{A},{B}", parameters[DriverElevatedData.UpdatesKey]);
        Assert.Equal("true", parameters[DriverElevatedData.EnableProtectionKey]);
        Assert.Equal([A, B], DriverElevatedData.ParseUpdateIds(parameters[DriverElevatedData.UpdatesKey]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas-un-guid")]
    [InlineData(A + "," + A)]
    [InlineData(A + ",")]
    [InlineData(A + ";" + B)]
    public void Invalid_update_lists_are_refused(string text) => Assert.Null(DriverElevatedData.ParseUpdateIds(text));

    [Fact]
    public void Update_list_is_limited()
    {
        var ids = Enumerable.Range(0, DriverElevatedData.MaxUpdates + 1).Select(_ => Guid.NewGuid().ToString("D"));
        Assert.Null(DriverElevatedData.ParseUpdateIds(string.Join(',', ids)));
    }

    [Fact]
    public void Rollback_parameters_keep_each_device_with_its_own_previous_version()
    {
        var state = new DriverUpdateState(A, "t", "GPU", "Display", "31.0.1.1",
        [
            new DriverDeviceState(@"PCI\VEN_1&DEV_2\3&1", "27.20.1.1", new DateOnly(2021, 1, 1), "Intel", "oem12.inf"),
            new DriverDeviceState(@"PCI\VEN_1&DEV_2\3&2", "26.20.1.1", null, "Intel", null),
        ]);

        var parameters = DriverElevatedData.RollbackParameters(state)!;

        Assert.Equal([DriverElevatedData.DevicesKey, DriverElevatedData.InstalledVersionKey, DriverElevatedData.PreviousVersionsKey], parameters.Keys.Order());
        Assert.Equal(@"PCI\VEN_1&DEV_2\3&1|PCI\VEN_1&DEV_2\3&2", parameters[DriverElevatedData.DevicesKey]);
        Assert.Equal("27.20.1.1|26.20.1.1", parameters[DriverElevatedData.PreviousVersionsKey]);
        Assert.Equal("31.0.1.1", parameters[DriverElevatedData.InstalledVersionKey]);
        var targets = DriverElevatedData.ParseRollbackTargets(parameters[DriverElevatedData.DevicesKey], parameters[DriverElevatedData.PreviousVersionsKey])!;
        Assert.Equal([new DriverRollbackTarget(@"PCI\VEN_1&DEV_2\3&1", "27.20.1.1"), new DriverRollbackTarget(@"PCI\VEN_1&DEV_2\3&2", "26.20.1.1")], targets);
    }

    [Fact]
    public void Rollback_is_refused_when_one_noted_device_cannot_be_restored()
    {
        var restorable = new DriverDeviceState(@"PCI\VEN_1&DEV_2\3&1", "27.20.1.1", null, null, null);
        Assert.Null(DriverElevatedData.RollbackParameters(new DriverUpdateState(A, "t", "GPU", "Display", "31.0.1.1",
            [restorable, new DriverDeviceState(@"PCI\VEN_1&DEV_2\3&2", null, null, null, null)])));
        Assert.Null(DriverElevatedData.RollbackParameters(new DriverUpdateState(A, "t", "GPU", "Display", "31.0.1.1",
            [restorable, new DriverDeviceState("sans-separateur", "1.0", null, null, null)])));
    }

    [Fact]
    public void Rollback_is_impossible_without_installed_version_or_previous_version()
    {
        var device = new DriverDeviceState(@"PCI\VEN_1&DEV_2\3&1", "27.20.1.1", null, null, null);
        Assert.Null(DriverElevatedData.RollbackParameters(new DriverUpdateState(A, "t", "GPU", "Display", null, [device])));
        Assert.Null(DriverElevatedData.RollbackParameters(new DriverUpdateState(A, "t", "GPU", "Display", "31.0.1.1", [device with { PreviousVersion = null }])));
        Assert.Null(DriverElevatedData.RollbackParameters(new DriverUpdateState(A, "t", "GPU", "Display", "31.0.1.1", [])));
    }

    [Theory]
    [InlineData(@"PCI\A\1|PCI\A\2", "1.0")]
    [InlineData(@"PCI\A\1", "1.0|2.0")]
    [InlineData(@"PCI\A\1", "pas-une-version")]
    [InlineData(@"PCI\A\1", "")]
    public void Misaligned_or_invalid_rollback_targets_are_refused(string devices, string versions)
        => Assert.Null(DriverElevatedData.ParseRollbackTargets(devices, versions));

    [Theory]
    [InlineData(@"PCI\A\1|PCI\A\1")]
    [InlineData(@"PCI\A\1|")]
    [InlineData("")]
    [InlineData("sans-separateur")]
    public void Invalid_device_lists_are_refused(string text) => Assert.Null(DriverElevatedData.ParseDevices(text));

    [Fact]
    public void Install_result_round_trips_and_ignores_invalid_entries()
    {
        var at = new DateTimeOffset(2026, 10, 4, 9, 30, 0, TimeSpan.Zero);
        var data = DriverElevatedData.EncodeInstall(DriverInstallStop.DeviceProblem, RestorePointStatus.Created, at, protectionEnabled: true,
            [new DriverInstallOutcome(A, DriverInstallStatus.Installed, 0, true, "31.0.101.2125", 0),
             new DriverInstallOutcome(B, DriverInstallStatus.Failed, unchecked((int)0x80240022), false, "x", null)], rebootRequired: true);
        data["2.id"] = "pas-un-guid";
        data["2.status"] = "Installed";
        data["count"] = "3";

        var (stop, rp, rpAt, enabled, drivers, reboot) = DriverElevatedData.DecodeInstall(data);

        Assert.Equal(DriverInstallStop.DeviceProblem, stop);
        Assert.Equal(RestorePointStatus.Created, rp);
        Assert.Equal(at, rpAt);
        Assert.True(enabled);
        Assert.True(reboot);
        Assert.Equal(2, drivers.Count);
        Assert.Equal(new DriverInstallOutcome(A, DriverInstallStatus.Installed, 0, true, "31.0.101.2125", 0), drivers[0]);
        Assert.Equal(DriverInstallStatus.Failed, drivers[1].Status);
        Assert.Equal(unchecked((int)0x80240022), drivers[1].HResult);
        Assert.Null(drivers[1].NewVersion);
    }

    [Fact]
    public void Numeric_or_unknown_enum_values_are_refused()
    {
        var data = new Dictionary<string, string> { ["stop"] = "3", ["rp.status"] = "Inconnu", ["count"] = "1", ["0.id"] = A, ["0.status"] = "0" };

        var (stop, rp, _, _, drivers, _) = DriverElevatedData.DecodeInstall(data);

        Assert.Equal(DriverInstallStop.None, stop);
        Assert.Equal(RestorePointStatus.Failed, rp);
        Assert.Empty(drivers);
    }

    [Fact]
    public void Rollback_result_round_trips()
    {
        var data = DriverElevatedData.EncodeRollback(
            [new DriverRollbackOutcome(@"PCI\A\1", DriverRollbackStatus.RolledBack, "27.20.1.1", 0),
             new DriverRollbackOutcome(@"PCI\A\2", DriverRollbackStatus.Failed, null, 5)], rebootRequired: false);

        var (outcomes, reboot) = DriverElevatedData.DecodeRollback(data);

        Assert.False(reboot);
        Assert.Equal(2, outcomes.Count);
        Assert.Equal(DriverRollbackStatus.RolledBack, outcomes[0].Status);
        Assert.Equal("27.20.1.1", outcomes[0].VersionAfter);
        Assert.Equal(5, outcomes[1].Win32Error);
    }

    [Theory]
    [InlineData("Installing;2;3", DriverInstallStage.Installing, 2, 3)]
    [InlineData("RestorePoint;0;5", DriverInstallStage.RestorePoint, 0, 5)]
    public void Progress_lines_parse(string line, DriverInstallStage stage, int index, int total)
    {
        Assert.Equal(new DriverInstallProgress(stage, index, total), DriverElevatedData.ParseProgress(line));
        Assert.Equal(line, DriverElevatedData.FormatProgress(new DriverInstallProgress(stage, index, total)));
    }

    [Theory]
    [InlineData("Installing;4;3")]
    [InlineData("Installing;1;99")]
    [InlineData("2;1;3")]
    [InlineData("Installing;-1;3")]
    [InlineData("Installing;1")]
    [InlineData("")]
    public void Invalid_progress_lines_are_ignored(string line) => Assert.Null(DriverElevatedData.ParseProgress(line));
}

public sealed class OfficialDriverSourcesTests
{
    private static InstalledDriver Gpu(string name, string? provider)
        => new($@"PCI\VEN_1\{name.Length}", name, "Display", [@"PCI\VEN_1"], [], "1.0", null, provider, "oem1.inf", 0);

    [Theory]
    [InlineData("HP", "hp")]
    [InlineData("Hewlett-Packard", "hp")]
    [InlineData("HP Inc.", "hp")]
    [InlineData("Dell Inc.", "dell")]
    [InlineData("LENOVO", "lenovo")]
    [InlineData("ASUSTeK COMPUTER INC.", "asus")]
    [InlineData("Acer", "acer")]
    [InlineData("Micro-Star International Co., Ltd.", "msi")]
    [InlineData("TOSHIBA", "dynabook")]
    [InlineData("FUJITSU CLIENT COMPUTING LIMITED", "fujitsu")]
    [InlineData("HPE", null)]
    [InlineData("Microsoft Corporation", null)]
    [InlineData("System manufacturer", null)]
    [InlineData(null, null)]
    public void Pc_manufacturers_are_recognized_strictly(string? manufacturer, string? id)
        => Assert.Equal(id, OfficialDriverSources.MatchId(manufacturer, DriverSourceKind.PcManufacturer));

    [Theory]
    [InlineData("NVIDIA", "nvidia")]
    [InlineData("Advanced Micro Devices, Inc.", "amd")]
    [InlineData("Intel Corporation", "intel")]
    [InlineData("Intel(R) UHD Graphics 620", "intel")]
    [InlineData("Intelligent Display", null)]
    public void Graphics_vendors_are_recognized(string name, string? id)
        => Assert.Equal(id, OfficialDriverSources.MatchId(name, DriverSourceKind.Graphics));

    [Fact]
    public void Sources_list_the_pc_maker_then_each_graphics_vendor_once()
    {
        var sources = OfficialDriverSources.For(new ComputerIdentity("HP", "Victus 15", "HP"),
            [Gpu("AMD Radeon 740M Graphics", "Advanced Micro Devices, Inc."), Gpu("NVIDIA GeForce RTX 4050 Laptop GPU", "NVIDIA"), Gpu("Second NVIDIA", "NVIDIA")]);

        Assert.Equal(["hp", "amd", "nvidia"], sources.Select(s => s.Id));
        Assert.Equal("Victus 15", sources[0].DetectedFor);
        Assert.Equal("AMD Radeon 740M Graphics", sources[1].DetectedFor);
        Assert.Equal(DriverSourceKind.Graphics, sources[2].Kind);
    }

    [Fact]
    public void Assembled_pc_uses_the_motherboard_maker_and_unknown_pc_has_only_graphics()
    {
        Assert.Equal("asus", OfficialDriverSources.For(new ComputerIdentity(null, null, "ASUSTeK COMPUTER INC."), [])[0].Id);
        var sources = OfficialDriverSources.For(null, [Gpu("Intel(R) HD Graphics", null)]);
        Assert.Equal(["intel"], sources.Select(s => s.Id));
    }

    [Fact]
    public void Every_source_is_an_official_https_address()
    {
        Assert.All(OfficialDriverSources.AllUris, u =>
        {
            Assert.Equal(Uri.UriSchemeHttps, u.Scheme);
            Assert.Matches(@"(^|\.)(hp|dell|lenovo|asus|acer|msi|dynabook|fujitsu|nvidia|amd|intel)\.com$", u.Host);
        });
    }
}
