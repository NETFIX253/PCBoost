using System.Runtime.CompilerServices;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Drivers;
using PCBoost.Platform;
using PCBoost.Platform.Drivers;
using PCBoost.Platform.Elevation;
using PCBoost.Platform.Interop;

namespace PCBoost.System.Tests;

/// <summary>Validation des opérations élevées sur les pilotes et logique pure de la couche Windows des pilotes.</summary>
public sealed class DriverPlatformTests
{
    private const string A = "0c3a9a55-5b0f-4f1d-9b3e-2a6b7f0c1d2e";
    private const string B = "6f1e2d3c-4b5a-4968-8776-5a4b3c2d1e0f";
    private const string Device1 = @"PCI\VEN_8086&DEV_5917&SUBSYS_00000000&REV_07\3&11583659&0&10";

    private static ElevatedRequest Request(string operation, params (string Key, string Value)[] parameters)
        => new(operation, parameters.ToDictionary(p => p.Key, p => p.Value));

    // ---- drivers.install ---------------------------------------------------------------------------------------

    [Fact]
    public void Install_with_valid_ids_is_accepted()
    {
        var pipe = ElevatedRequestValidator.NewProgressPipeName();
        var result = ElevatedRequestValidator.Validate(Request(ElevatedDriverOperations.Install,
            ("updates", $"{A},{B.ToUpperInvariant()}"), ("enableProtection", "false"), ("progressPipe", pipe)));

        Assert.True(result.Success);
        var install = Assert.IsType<ValidatedDriverInstall>(result.Value);
        Assert.Equal([A, B], install.UpdateIds);
        Assert.False(install.EnableProtection);
        Assert.Equal(pipe, install.ProgressPipe);
    }

    [Theory]
    [InlineData("updates", "C:\\Windows\\drivers.cab")]
    [InlineData("updates", "https://example.com/driver.exe")]
    [InlineData("updates", A + "," + A)]
    [InlineData("updates", "")]
    [InlineData("enableProtection", "oui")]
    [InlineData("progressPipe", "PCBoost.Frames.0123456789abcdef0123456789abcdef")]
    [InlineData("progressPipe", @"\\.\pipe\autre")]
    public void Install_with_invalid_parameter_is_refused(string key, string value)
    {
        var parameters = new Dictionary<string, string> { ["updates"] = A, ["enableProtection"] = "true" };
        parameters[key] = value;

        var result = ElevatedRequestValidator.Validate(new ElevatedRequest(ElevatedDriverOperations.Install, parameters));

        Assert.False(result.Success);
        Assert.Equal(OperationErrorKind.InvalidInput, result.Error);
    }

    [Fact]
    public void Install_with_unexpected_or_missing_parameter_is_refused()
    {
        Assert.False(ElevatedRequestValidator.Validate(Request(ElevatedDriverOperations.Install, ("updates", A))).Success);
        Assert.False(ElevatedRequestValidator.Validate(Request(ElevatedDriverOperations.Install,
            ("updates", A), ("enableProtection", "true"), ("path", @"C:\Windows\INF\oem1.inf"))).Success);
    }

    // ---- driver.rollback ---------------------------------------------------------------------------------------

    [Fact]
    public void Rollback_with_aligned_devices_and_versions_is_accepted()
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedDriverOperations.Rollback,
            ("devices", Device1 + "|" + @"PCI\VEN_8086&DEV_5917\4&2"), ("previous", "27.20.100.8681|26.20.100.7000"), ("installed", "31.0.101.2125")));

        Assert.True(result.Success);
        var rollback = Assert.IsType<ValidatedDriverRollback>(result.Value);
        Assert.Equal([new DriverRollbackTarget(Device1, "27.20.100.8681"), new DriverRollbackTarget(@"PCI\VEN_8086&DEV_5917\4&2", "26.20.100.7000")], rollback.Targets);
        Assert.Equal("31.0.101.2125", rollback.InstalledVersion);
    }

    [Theory]
    [InlineData("previous", "27.20.100.8681|1.0")]
    [InlineData("previous", "1.2.3.4.5")]
    [InlineData("installed", "")]
    [InlineData("installed", "latest")]
    [InlineData("devices", "pas-un-identifiant")]
    public void Rollback_with_invalid_parameter_is_refused(string key, string value)
    {
        var parameters = new Dictionary<string, string> { ["devices"] = Device1, ["previous"] = "27.20.100.8681", ["installed"] = "31.0.101.2125" };
        parameters[key] = value;

        Assert.Equal(OperationErrorKind.InvalidInput, ElevatedRequestValidator.Validate(new ElevatedRequest(ElevatedDriverOperations.Rollback, parameters)).Error);
    }

    [Fact]
    public void Rollback_never_accepts_a_file_or_a_hardware_id_and_requires_every_parameter()
    {
        (string, string)[] valid = [("devices", Device1), ("previous", "27.20.100.8681"), ("installed", "31.0.101.2125")];
        Assert.False(ElevatedRequestValidator.Validate(Request(ElevatedDriverOperations.Rollback, [.. valid, ("inf", "oem12.inf")])).Success);
        Assert.False(ElevatedRequestValidator.Validate(Request(ElevatedDriverOperations.Rollback, [.. valid, ("hardwareId", @"PCI\VEN_8086&DEV_5917")])).Success);
        Assert.False(ElevatedRequestValidator.Validate(Request(ElevatedDriverOperations.Rollback, ("devices", Device1), ("previous", "27.20.100.8681"))).Success);
        Assert.False(ElevatedRequestValidator.Validate(Request(ElevatedDriverOperations.Rollback, ("devices", Device1), ("installed", "31.0.101.2125"))).Success);
    }

    [Fact]
    public void Progress_pipe_names_are_random_and_strict()
    {
        var a = ElevatedRequestValidator.NewProgressPipeName();
        var b = ElevatedRequestValidator.NewProgressPipeName();
        Assert.NotEqual(a, b);
        Assert.True(ElevatedRequestValidator.IsValidProgressPipeName(a));
        Assert.False(ElevatedRequestValidator.IsValidProgressPipeName("PCBoost.Progress.x"));
        Assert.False(ElevatedRequestValidator.IsValidProgressPipeName(null));
        Assert.False(ElevatedRequestValidator.IsValidFramePipeName(a));
    }

    // ---- Progression -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Progress_lines_are_relayed_until_end_of_stream()
    {
        using var stream = new MemoryStream(global::System.Text.Encoding.UTF8.GetBytes("RestorePoint;0;2\nInstalling;1;2\n\n" + new string('x', 100) + "\nVerifying;2;2\n"));
        var lines = new List<string>();

        await ElevationService.ReadProgressAsync(stream, new SyncProgress(lines), CancellationToken.None);

        Assert.Equal(["RestorePoint;0;2", "Installing;1;2", "Verifying;2;2"], lines);
    }

    private sealed class SyncProgress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }

    // ---- Agent Windows Update : lectures pures -----------------------------------------------------------------

    [Theory]
    [InlineData(0x80070422u, OperationErrorKind.NotSupported, "Drv_Error_ServiceDisabled")]
    [InlineData(0x8024402Cu, OperationErrorKind.Failed, "Drv_Error_Offline")]
    [InlineData(0x80072EE7u, OperationErrorKind.Failed, "Drv_Error_Offline")]
    [InlineData(0x8024002Eu, OperationErrorKind.Blocked, "Drv_Error_Policy")]
    [InlineData(0x80070005u, OperationErrorKind.AccessDenied, "Drv_Error_AccessDenied")]
    [InlineData(0x80040154u, OperationErrorKind.NotSupported, "Drv_Error_AgentMissing")]
    [InlineData(0x80240FFFu, OperationErrorKind.Failed, "Drv_Error_SearchFailed")]
    public void Windows_update_errors_are_classified(uint hresult, OperationErrorKind kind, string key)
    {
        var result = WindowsUpdateAgent.Classify(unchecked((int)hresult));

        Assert.Equal(kind, result.Error);
        Assert.Equal(key, result.Message?.Key);
        Assert.StartsWith($"0x{hresult:X8}", result.TechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_values_are_read_defensively()
    {
        Assert.Equal(new DateOnly(2023, 5, 15), WindowsUpdateAgent.Date(new DateTime(2023, 5, 15)));
        Assert.Null(WindowsUpdateAgent.Date(new DateTime(1899, 12, 30)));
        Assert.Null(WindowsUpdateAgent.Time(DateTime.MinValue));
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero), WindowsUpdateAgent.Time(new DateTime(2026, 6, 1, 8, 0, 0)));
        Assert.Equal(300_000_000L, WindowsUpdateAgent.Size(300_000_000m));
        Assert.Null(WindowsUpdateAgent.Size(0m));
        Assert.Null(WindowsUpdateAgent.Size("abc"));
        Assert.Equal(DriverRebootBehavior.Never, WindowsUpdateAgent.RebootBehavior(0));
        Assert.Equal(DriverRebootBehavior.Always, WindowsUpdateAgent.RebootBehavior(1));
        Assert.Equal(DriverRebootBehavior.Possible, WindowsUpdateAgent.RebootBehavior(null));
        Assert.Equal("Intel", WindowsUpdateAgent.Text(" Intel\u0007 "));
        Assert.Null(WindowsUpdateAgent.DeviceId("PCI\\VEN|1"));
    }

    // ---- WMI ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("20230315000000.******+000", 2023, 3, 15)]
    [InlineData("20060621000000.000000-000", 2006, 6, 21)]
    public void Cim_dates_are_parsed(string value, int year, int month, int day)
        => Assert.Equal(new DateOnly(year, month, day), DriverWmiParsing.ParseCimDate(value));

    [Theory]
    [InlineData(null)]
    [InlineData("2023")]
    [InlineData("19000101000000.000000-000")]
    [InlineData("abcdefgh")]
    public void Invalid_cim_dates_are_ignored(string? value) => Assert.Null(DriverWmiParsing.ParseCimDate(value));

    [Fact]
    public void Devices_are_merged_with_their_driver_and_invalid_values_dropped()
    {
        var entities = new List<DeviceDriverProvider.EntityRow>
        {
            new(Device1, "Intel(R) UHD Graphics 620", "Display", [@"PCI\VEN_8086&DEV_5917"], [], 0),
            new(@"USB\VID_1&PID_2\5&1", null, null, [@"USB\VID_1&PID_2"], [], 28),
        };
        var drivers = new List<DeviceDriverProvider.DriverRow>
        {
            new(Device1.ToLowerInvariant(), "GPU", "DISPLAY", "27.20.100.8681", new DateOnly(2021, 3, 12), "Intel Corporation", "oem12.inf"),
            new(@"USB\VID_1&PID_2\5&1", "Clé", "USB", "not-a-version", null, null, @"..\x.inf"),
        };

        var merged = DriverWmiParsing.Merge(entities, drivers, 10);

        Assert.Equal("27.20.100.8681", merged[0].Version);
        Assert.Equal("oem12.inf", merged[0].InfName);
        Assert.Equal("Display", merged[0].DeviceClass);
        Assert.Equal("Clé", merged[1].DeviceName);
        Assert.Null(merged[1].Version);
        Assert.Null(merged[1].InfName);
        Assert.True(merged[1].HasNoDriver);
        Assert.Single(DriverWmiParsing.Merge(entities, drivers, 1));
    }

    // ---- Retour au pilote précédent ----------------------------------------------------------------------------

    private const string Installed = "31.0.101.2125";
    private const string Previous = "27.20.100.8681";
    private const string Device2 = @"PCI\VEN_8086&DEV_5917\4&2";
    private const string Device3 = @"PCI\VEN_8086&DEV_5917\4&3";

    private static InstalledDriver On(string instanceId, string? version) => new(instanceId, "GPU", "Display", [], [], version, null, null, null, 0);

    [Fact]
    public void Rollback_touches_only_devices_still_on_the_installed_version()
    {
        var log = new List<string>();
        var touched = new List<string>();
        var reads = 0;
        var rollback = new DriverRollback(log.Add,
            () => ++reads == 1
                ? [On(Device1, Installed), On(Device2, Previous), On(Device3, "32.0.101.6000")]
                : [On(Device1, Previous), On(Device2, Previous), On(Device3, "32.0.101.6000")],
            id => { touched.Add(id); return (true, 0, false); });

        var (outcomes, reboot) = rollback.Run(
            [new DriverRollbackTarget(Device1, Previous), new DriverRollbackTarget(Device2, Previous), new DriverRollbackTarget(Device3, Previous),
             new DriverRollbackTarget(@"PCI\VEN_1&DEV_2\9&9", Previous)], Installed);

        Assert.False(reboot);
        Assert.Equal([Device1], touched);
        Assert.Equal([DriverRollbackStatus.RolledBack, DriverRollbackStatus.AlreadyPrevious, DriverRollbackStatus.Changed, DriverRollbackStatus.DeviceGone],
            outcomes.Select(o => o.Status));
        Assert.Equal(Previous, outcomes[0].VersionAfter);
    }

    [Fact]
    public void Unreadable_device_list_is_a_failure_and_nothing_is_attempted()
    {
        var touched = new List<string>();
        var rollback = new DriverRollback(_ => { }, () => null, id => { touched.Add(id); return (true, 0, false); });

        var (outcomes, _) = rollback.Run([new DriverRollbackTarget(Device1, Previous)], Installed);

        Assert.Empty(touched);
        Assert.Equal(DriverRollbackStatus.Failed, Assert.Single(outcomes).Status);
    }

    [Fact]
    public void Rollback_is_verified_per_device_unless_a_restart_is_needed()
    {
        var rollback = new DriverRollback(_ => { },
            () => [On(Device1, Installed), On(Device2, Installed)],
            id => (true, 0, id == Device2));

        var (outcomes, reboot) = rollback.Run([new DriverRollbackTarget(Device1, Previous), new DriverRollbackTarget(Device2, "26.0.0.1")], Installed);

        Assert.True(reboot);
        // Device1 : Windows dit avoir réussi mais la version n'a pas changé → échec ; Device2 : actif après redémarrage.
        Assert.Equal(DriverRollbackStatus.Failed, outcomes[0].Status);
        Assert.Equal(DriverRollbackStatus.RolledBack, outcomes[1].Status);
    }

    [Fact]
    public void Windows_rollback_failure_keeps_its_error_code()
    {
        var rollback = new DriverRollback(_ => { }, () => [On(Device1, Installed)], _ => (false, 259, false));

        var (outcomes, _) = rollback.Run([new DriverRollbackTarget(Device1, Previous)], Installed);

        Assert.Equal(DriverRollbackStatus.Failed, outcomes[0].Status);
        Assert.Equal(259, outcomes[0].Win32Error);
    }

    // ---- Installation : configuration relue par l'Elevator -----------------------------------------------------

    [Theory]
    [InlineData(true, false, false, SystemProtectionState.Enabled, DriverInstallStop.ExcludedByPolicy)]
    [InlineData(false, true, false, SystemProtectionState.Enabled, DriverInstallStop.UpdateServiceDisabled)]
    [InlineData(false, false, true, SystemProtectionState.Enabled, DriverInstallStop.RebootPending)]
    [InlineData(false, false, false, SystemProtectionState.DisabledByPolicy, DriverInstallStop.RestorePointUnavailable)]
    public void Installer_refuses_before_anything_when_the_configuration_forbids_it(bool excluded, bool serviceDisabled, bool reboot,
        SystemProtectionState protection, DriverInstallStop expected)
    {
        var progress = new List<DriverInstallProgress>();
        var deviceReads = 0;
        var installer = new DriverInstaller(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, _ => { }, progress.Add,
            environment: () => new Core.Drivers.DriverEnvironment(excluded, serviceDisabled, false, reboot, protection),
            readDevices: () => { deviceReads++; return []; });

        var report = installer.Install([A, B], enableProtection: true);

        Assert.Equal(expected, report.Stop);
        Assert.False(report.ProtectionEnabled);
        Assert.All(report.Drivers, d => Assert.Equal(DriverInstallStatus.NotRun, d.Status));
        Assert.Empty(progress);
        Assert.Equal(0, deviceReads);
    }

    // ---- Connexion limitée -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Windows.Networking.Connectivity.NetworkCostType.Unrestricted, false, false, false)]
    [InlineData(Windows.Networking.Connectivity.NetworkCostType.Unrestricted, true, false, true)]
    [InlineData(Windows.Networking.Connectivity.NetworkCostType.Fixed, false, false, true)]
    [InlineData(Windows.Networking.Connectivity.NetworkCostType.Variable, false, false, true)]
    [InlineData(Windows.Networking.Connectivity.NetworkCostType.Unknown, false, true, true)]
    public void Metered_connections_are_detected(Windows.Networking.Connectivity.NetworkCostType type, bool roaming, bool overLimit, bool metered)
        => Assert.Equal(metered, NetworkCostProvider.IsMetered(type, roaming, overLimit));

    [Fact]
    public void Unknown_cost_without_roaming_is_unknown()
        => Assert.Null(NetworkCostProvider.IsMetered(Windows.Networking.Connectivity.NetworkCostType.Unknown, false, false));

    // ---- Paramètres Windows (liste fermée) ---------------------------------------------------------------------

    [Theory]
    [InlineData("privacy")]
    [InlineData("windowsupdate-action")]
    [InlineData("appsfeatures;cmd")]
    [InlineData("")]
    public void Settings_pages_outside_the_closed_list_are_blocked(string page)
    {
        var result = new ShellService().OpenWindowsSettings(page);

        Assert.Equal(OperationErrorKind.Blocked, result.Error);
        Assert.Equal("Sys_UriNotAllowed", result.Message?.Key);
    }

    [Fact]
    public void Closed_settings_list_contains_only_simple_page_names()
        => Assert.All(WindowsSettingsPages.All, p => Assert.Matches("^[a-z][a-z-]+$", p));

    // ---- Structures natives ------------------------------------------------------------------------------------

    [Fact]
    public void SetupApi_device_info_structure_has_the_native_size()
    {
        if (nint.Size != 8) return;
        Assert.Equal(32, Unsafe.SizeOf<DeviceInstall.SP_DEVINFO_DATA>());
    }
}

public sealed class DriverPlatformIdentityTests
{
    [Fact]
    public void Lenovo_friendly_model_replaces_the_machine_type()
    {
        var identity = DriverWmiParsing.Identity("LENOVO", "20L5CTO1WW", "ThinkPad T480", "LENOVO");
        Assert.Equal("ThinkPad T480", identity.Model);
        Assert.Equal("LENOVO", identity.Manufacturer);
    }

    [Theory]
    [InlineData("System manufacturer", "System Product Name", "To be filled by O.E.M.", null, null)]
    [InlineData("HP", "HP Laptop 15", "Default string", "HP", "HP Laptop 15")]
    public void Generic_values_are_ignored(string maker, string model, string version, string? expectedMaker, string? expectedModel)
    {
        var identity = DriverWmiParsing.Identity(maker, model, version, "ASUSTeK COMPUTER INC.");
        Assert.Equal(expectedMaker, identity.Manufacturer);
        Assert.Equal(expectedModel, identity.Model);
        Assert.Equal("ASUSTeK COMPUTER INC.", identity.BoardManufacturer);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void On_demand_restore_point_is_accepted_with_its_single_parameter(string value, bool expected)
    {
        var result = ElevatedRequestValidator.Validate(new ElevatedRequest(ElevatedDriverOperations.RestorePoint,
            new Dictionary<string, string> { ["enableProtection"] = value }));

        Assert.True(result.Success);
        Assert.Equal(expected, Assert.IsType<ValidatedDriverRestorePoint>(result.Value).EnableProtection);
    }

    [Fact]
    public void On_demand_restore_point_refuses_other_parameters()
    {
        Assert.False(ElevatedRequestValidator.Validate(new ElevatedRequest(ElevatedDriverOperations.RestorePoint, new Dictionary<string, string>())).Success);
        Assert.False(ElevatedRequestValidator.Validate(new ElevatedRequest(ElevatedDriverOperations.RestorePoint,
            new Dictionary<string, string> { ["enableProtection"] = "true", ["description"] = "x" })).Success);
    }
}
