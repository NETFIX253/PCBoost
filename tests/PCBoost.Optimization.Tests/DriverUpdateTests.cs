using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Drivers;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;
using PCBoost.Optimization.Drivers;
using PCBoost.Optimization.Handlers;
using PCBoost.TestUtilities;

namespace PCBoost.Optimization.Tests;

public sealed class DriverUpdateServiceTests : IDisposable
{
    private const string Gpu = "0c3a9a55-5b0f-4f1d-9b3e-2a6b7f0c1d2e";
    private const string Wifi = "6f1e2d3c-4b5a-4968-8776-5a4b3c2d1e0f";
    private const string Bios = "11111111-2222-4333-8444-555555555555";
    private const string WifiDevice = @"PCI\VEN_8086&DEV_24FD&SUBSYS_00108086&REV_78\4&2&0&00E0";

    private readonly Harness _h = new();
    private static readonly DateTimeOffset RestorePointAt = new(2026, 9, 28, 11, 59, 0, TimeSpan.Zero);

    public void Dispose() => _h.Dispose();

    private IDriverUpdateService Service => _h.Get<IDriverUpdateService>();

    private void Hklm(string key, string name, int value)
        => _h.Registry.Set(RegistryHiveKind.LocalMachine, key, name, RegistryValueData.DWord(value));

    private void SetupTwoUpdates()
    {
        _h.Devices.Drivers.Add(DriverSamples.Device());
        _h.Devices.Drivers.Add(DriverSamples.Device(instanceId: WifiDevice, hardwareId: @"PCI\VEN_8086&DEV_24FD", name: "Intel(R) Dual Band Wireless-AC 8265",
            deviceClass: "Net", version: "20.70.0.5", provider: "Intel", inf: "oem33.inf"));
        _h.DriverSource.Result = new DriverSearchResult(OperationResult.Ok(),
        [
            DriverSamples.Offer(updateId: Bios, driverClass: "Firmware", title: "Intel - Firmware - 2.0.0.1", hardwareId: @"PCI\VEN_8086&DEV_5917"),
            DriverSamples.Offer(updateId: Wifi, hardwareId: @"PCI\VEN_8086&DEV_24FD", driverClass: "Net", version: "22.200.0.6", provider: "Intel", optional: true),
            DriverSamples.Offer(updateId: Gpu),
        ], false, false);
    }

    private static Dictionary<string, string> Installed(params (string Id, DriverInstallStatus Status)[] drivers)
        => DriverElevatedData.EncodeInstall(DriverInstallStop.None, RestorePointStatus.Created, RestorePointAt, false,
            drivers.Select(d => new DriverInstallOutcome(d.Id, d.Status, d.Status == DriverInstallStatus.Installed ? 0 : unchecked((int)0x80240022), false,
                d.Status == DriverInstallStatus.Installed ? "31.0.101.2125" : null, d.Status == DriverInstallStatus.Installed ? 0 : null)).ToList(), false);

    // ---- Recherche ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scan_classifies_and_sorts_updates()
    {
        SetupTwoUpdates();

        var scan = await Service.ScanAsync();

        Assert.Equal(DriverScanState.Ready, scan.State);
        Assert.Equal([Gpu, Wifi, Bios], scan.Candidates.Select(c => c.Offer.UpdateId));
        Assert.Equal([DriverUpdateTier.Recommended, DriverUpdateTier.Review, DriverUpdateTier.Excluded], scan.Candidates.Select(c => c.Tier));
        Assert.True(scan.CanInstall);
        Assert.Equal(SystemProtectionState.Unknown, scan.Protection);
        Assert.Same(scan, Service.Latest);
        Assert.Equal(1, _h.DriverSource.SearchCount);
        Assert.Empty(_h.Elevation.Requests);
    }

    [Theory]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate")]
    [InlineData(@"SOFTWARE\Microsoft\PolicyManager\current\device\Update")]
    public async Task Organization_policy_excluding_drivers_is_respected_without_searching(string key)
    {
        Hklm(key, "ExcludeWUDriversInQualityUpdate", 1);
        SetupTwoUpdates();

        var scan = await Service.ScanAsync();

        Assert.Equal(DriverScanState.ExcludedByPolicy, scan.State);
        Assert.Empty(scan.Candidates);
        Assert.False(scan.CanInstall);
        Assert.Equal(0, _h.DriverSource.SearchCount);
    }

    [Fact]
    public async Task Disabled_update_service_is_reported_and_never_changed()
    {
        Hklm(@"SYSTEM\CurrentControlSet\Services\wuauserv", "Start", 4);

        var scan = await Service.ScanAsync();

        Assert.Equal(DriverScanState.UpdateServiceDisabled, scan.State);
        Assert.Equal(0, _h.DriverSource.SearchCount);
        Assert.Equal(0, _h.Registry.WriteCount);
    }

    [Fact]
    public async Task Reboot_pending_managed_server_and_protection_are_reported()
    {
        _h.Registry.CreateKey(RegistryHiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        Hklm(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "UseWUServer", 1);
        Hklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "RPSessionInterval", 0);
        SetupTwoUpdates();

        var scan = await Service.ScanAsync();

        Assert.True(scan.RebootPending);
        Assert.True(scan.ManagedUpdateServer);
        Assert.Equal(SystemProtectionState.Disabled, scan.Protection);
        Assert.False(scan.CanInstall);
    }

    [Theory]
    [InlineData(1, null, SystemProtectionState.Enabled)]
    [InlineData(0, 1, SystemProtectionState.DisabledByPolicy)]
    [InlineData(1, 1, SystemProtectionState.DisabledByPolicy)]
    public async Task System_protection_state_is_read_from_settings_and_policy(int interval, int? policy, SystemProtectionState expected)
    {
        Hklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "RPSessionInterval", interval);
        if (policy is { } p) Hklm(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", "DisableSR", p);

        var scan = await Service.ScanAsync();

        Assert.Equal(expected, scan.Protection);
    }

    [Theory]
    [InlineData("Drv_Error_Offline", DriverScanState.SearchFailed)]
    [InlineData("Drv_Error_ServiceDisabled", DriverScanState.UpdateServiceDisabled)]
    [InlineData("Drv_Error_Policy", DriverScanState.ExcludedByPolicy)]
    public async Task Search_failures_are_mapped(string key, DriverScanState state)
    {
        _h.DriverSource.Result = DriverSearchResult.Failed(OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of(key), "0x8024402C"));

        var scan = await Service.ScanAsync();

        Assert.Equal(state, scan.State);
        Assert.Equal(key, scan.Error!.Message!.Key);
        Assert.Empty(scan.Candidates);
    }

    // ---- Installation ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Install_writes_ahead_then_uses_one_elevated_operation()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        var selected = scan.Candidates.Where(c => c.CanInstall).ToList();
        IReadOnlyList<ChangeRecord>? journalAtElevation = null;
        _h.Elevation.ProgressToReport.AddRange(["RestorePoint;0;2", "garbage", "Installing;1;2"]);
        _h.Elevation.Handler = request =>
        {
            // Write-ahead : les deux pilotes sont déjà inscrits (Pending) au moment de l'autorisation administrateur.
            journalAtElevation = _h.History.Sessions.Single().Changes.ToList();
            return new ElevatedResponse(OperationResult.Ok(), Installed((Gpu, DriverInstallStatus.Installed), (Wifi, DriverInstallStatus.Installed)));
        };
        var progress = new List<DriverInstallProgress>();

        var result = await Service.InstallAsync(selected, enableSystemProtection: false, new SyncProgress<DriverInstallProgress>(progress.Add));

        Assert.True(result.Outcome.Success);
        Assert.Equal(2, result.InstalledCount);
        Assert.Equal(RestorePointStatus.Created, result.RestorePoint);
        Assert.Equal(RestorePointAt, result.RestorePointAt);
        var request = Assert.Single(_h.Elevation.Requests);
        Assert.Equal(ElevatedDriverOperations.Install, request.Operation);
        Assert.Equal($"{Gpu},{Wifi}", request.Parameters[DriverElevatedData.UpdatesKey]);
        Assert.Equal("false", request.Parameters[DriverElevatedData.EnableProtectionKey]);
        Assert.NotNull(journalAtElevation);
        Assert.Equal(2, journalAtElevation!.Count);
        Assert.All(journalAtElevation, c => Assert.Equal(ChangeStatus.Pending, c.Status));
        Assert.Equal([DriverInstallStage.RestorePoint, DriverInstallStage.Installing], progress.Select(p => p.Stage));

        var session = _h.History.Sessions.Single();
        Assert.Equal(SessionType.DriverUpdate, session.Type);
        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.Equal(result.SessionId, session.Id);
        Assert.All(session.Changes, c => Assert.Equal(ChangeStatus.Applied, c.Status));
        Assert.All(session.Changes, c => Assert.True(c.Reversible));
        Assert.Contains(_h.Journal.Entries, e => e.Message.Key == "Drv_Journal_RestorePoint");
        Assert.Contains(_h.Journal.Entries, e => e.Message.Key == "Drv_Journal_Installed" && e.Kind == ActivityKind.Optimization);
    }

    [Fact]
    public async Task Without_restore_point_nothing_is_installed_and_changes_fail()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        _h.Elevation.Handler = _ => new ElevatedResponse(
            OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drv_Stop_RestorePointUnavailable")),
            DriverElevatedData.EncodeInstall(DriverInstallStop.RestorePointUnavailable, RestorePointStatus.Failed, null, false,
                [new DriverInstallOutcome(Gpu, DriverInstallStatus.NotRun, 0, false, null, null)], false));

        var result = await Service.InstallAsync([scan.Candidates.First(c => c.Offer.UpdateId == Gpu)], false);

        Assert.False(result.Outcome.Success);
        Assert.Equal(DriverInstallStop.RestorePointUnavailable, result.Stop);
        Assert.Equal("Drv_Stop_RestorePointUnavailable", result.Outcome.Message!.Key);
        Assert.Equal(0, result.InstalledCount);
        var change = Assert.Single(_h.History.Sessions.Single().Changes);
        Assert.Equal(ChangeStatus.Failed, change.Status);
        Assert.Equal(SessionStatus.Failed, _h.History.Sessions.Single().Status);
        Assert.DoesNotContain(_h.Journal.Entries, e => e.Message.Key == "Drv_Journal_RestorePoint");
    }

    [Fact]
    public async Task Declined_permission_installs_nothing()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        _h.Elevation.UserCancels = true;

        var result = await Service.InstallAsync(scan.Candidates.Where(c => c.CanInstall).ToList(), false);

        Assert.Equal(OperationErrorKind.ElevationCancelled, result.Outcome.Error);
        Assert.All(result.Drivers, d => Assert.Equal(DriverInstallStatus.NotRun, d.Status));
        Assert.All(_h.History.Sessions.Single().Changes, c => Assert.Equal(ChangeStatus.Failed, c.Status));
    }

    [Fact]
    public async Task Excluded_updates_are_never_sent_for_installation()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();

        var result = await Service.InstallAsync(scan.Candidates.ToList(), false);

        Assert.Equal(OperationErrorKind.Blocked, result.Outcome.Error);
        Assert.Equal("Drv_Install_Excluded", result.Outcome.Message!.Key);
        Assert.Empty(_h.Elevation.Requests);
        Assert.Empty(_h.History.Sessions);
    }

    [Fact]
    public async Task Nothing_is_installed_when_the_organization_disabled_system_restore()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        Hklm(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", "DisableSR", 1);

        var result = await Service.InstallAsync([scan.Candidates[0]], true);

        Assert.Equal(OperationErrorKind.Blocked, result.Outcome.Error);
        Assert.Equal("Drv_Stop_ProtectionPolicy", result.Outcome.Message!.Key);
        Assert.Empty(_h.Elevation.Requests);
    }

    [Fact]
    public async Task Device_problem_stops_the_remaining_installations()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        _h.Elevation.Handler = _ => new ElevatedResponse(OperationResult.Ok(), DriverElevatedData.EncodeInstall(DriverInstallStop.DeviceProblem,
            RestorePointStatus.Created, RestorePointAt, false,
            [new DriverInstallOutcome(Gpu, DriverInstallStatus.Installed, 0, false, "31.0.101.2125", 43),
             new DriverInstallOutcome(Wifi, DriverInstallStatus.NotRun, 0, false, null, null)], false));

        var result = await Service.InstallAsync(scan.Candidates.Where(c => c.CanInstall).ToList(), false);

        Assert.Equal(DriverInstallStop.DeviceProblem, result.Stop);
        Assert.Equal("Drv_Install_Partial", result.Outcome.Message!.Key);
        Assert.Equal(43, result.Drivers[0].ProblemCodeAfter);
        var changes = _h.History.Sessions.Single().Changes.OrderBy(c => c.Sequence).ToList();
        Assert.Equal(ChangeStatus.Applied, changes[0].Status);
        Assert.Equal(ChangeStatus.Failed, changes[1].Status);
        Assert.Equal(SessionStatus.PartiallyCompleted, _h.History.Sessions.Single().Status);
    }

    [Fact]
    public async Task Selection_limits_are_enforced()
    {
        Assert.Equal("Drv_Install_NothingSelected", (await Service.InstallAsync([], false)).Outcome.Message!.Key);
        var many = Enumerable.Range(0, DriverElevatedData.MaxUpdates + 1)
            .Select(i => DriverUpdatePolicyEvaluate(DriverSamples.Offer(updateId: Guid.NewGuid().ToString("D")))).ToList();
        Assert.Equal("Drv_Install_TooMany", (await Service.InstallAsync(many, false)).Outcome.Message!.Key);
        Assert.Empty(_h.Elevation.Requests);
    }

    private static DriverUpdateCandidate DriverUpdatePolicyEvaluate(DriverUpdateOffer offer)
        => Core.Drivers.DriverUpdatePolicy.Evaluate(offer, [DriverSamples.Device()], new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Journal_entry_keeps_each_device_previous_driver_and_is_reversible_only_if_one_existed()
    {
        var withDriver = DriverUpdateService.PendingFor(DriverUpdatePolicyEvaluate(DriverSamples.Offer()));
        var noDriver = DriverUpdateService.PendingFor(Core.Drivers.DriverUpdatePolicy.Evaluate(DriverSamples.Offer(),
            [DriverSamples.Device(version: null, provider: null, inf: null, problem: 28) with { Date = null }], DateTimeOffset.UnixEpoch));

        Assert.Equal(ChangeKinds.DriverUpdate, withDriver.Kind);
        Assert.Equal("driver:Intel(R) UHD Graphics 620", withDriver.Target);
        Assert.True(withDriver.Reversible);
        Assert.Equal("Drv_Change_Description", withDriver.Description.Key);
        Assert.Equal(["Intel(R) UHD Graphics 620", "27.20.100.8681", "31.0.101.2125"], withDriver.Description.Args);
        var state = ChangeStateSerializer.Deserialize<DriverUpdateState>(withDriver.BeforeState)!;
        Assert.Equal("31.0.101.2125", state.NewVersion);
        Assert.Equal("Display", state.DriverClass);
        var device = Assert.Single(state.Devices);
        Assert.Equal(DriverSamples.Device().InstanceId, device.InstanceId);
        Assert.Equal("27.20.100.8681", device.PreviousVersion);
        Assert.Equal("oem12.inf", device.PreviousInfName);
        Assert.Equal(new DateOnly(2021, 3, 12), device.PreviousDate);
        Assert.False(noDriver.Reversible);
        Assert.Equal("Drv_Change_DescriptionRestorePoint", noDriver.Description.Key);
    }

    [Fact]
    public void Extension_updates_are_journaled_as_restore_point_only()
    {
        var extension = DriverUpdateService.PendingFor(DriverUpdatePolicyEvaluate(DriverSamples.Offer(driverClass: "Extension", version: "1.0.0.7")));

        Assert.False(extension.Reversible);
        Assert.Equal("Drv_Change_DescriptionRestorePoint", extension.Description.Key);
    }

    [Theory]
    [InlineData(OperationErrorKind.Timeout, "Sys_ElevationFailed", true)]
    [InlineData(OperationErrorKind.Cancelled, "Sys_ElevationFailed", true)]
    [InlineData(OperationErrorKind.Failed, "Sys_ElevationNoResult", true)]
    [InlineData(OperationErrorKind.ElevationCancelled, "Sys_ElevationCancelled", false)]
    [InlineData(OperationErrorKind.InvalidInput, "Sys_ElevatedInvalidParameters", false)]
    [InlineData(OperationErrorKind.Failed, "Sys_ElevationFailed", false)]
    public void Only_a_started_helper_without_result_is_uncertain(OperationErrorKind error, string key, bool uncertain)
        => Assert.Equal(uncertain, DriverUpdateService.IsUncertain(OperationResult.Fail(error, TextRef.Of(key))));

    [Fact]
    public async Task Helper_without_result_leaves_changes_pending_and_undoable()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        _h.Elevation.Handler = _ => new ElevatedResponse(OperationResult.Fail(OperationErrorKind.Timeout, TextRef.Of("Sys_ElevationFailed")), new Dictionary<string, string>());

        var result = await Service.InstallAsync(scan.Candidates.Where(c => c.CanInstall).ToList(), false);

        Assert.False(result.Outcome.Success);
        Assert.Equal("Drv_Install_Unknown", result.Outcome.Message!.Key);
        Assert.All(result.Drivers, d => Assert.Equal(DriverInstallStatus.Unknown, d.Status));
        var changes = _h.History.Sessions.Single().Changes;
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(ChangeStatus.Pending, c.Status));
        Assert.Contains(_h.Journal.Entries, e => e.Message.Key == "Drv_Journal_Unknown" && e.Kind == ActivityKind.Warning);

        // Le retour reste proposé ; le pilote n'ayant pas changé, rien n'est fait et aucune autorisation n'est demandée.
        var gpu = changes.Single(c => c.Target.Contains("UHD", StringComparison.Ordinal));
        var undo = await Service.RollbackAsync(gpu.Id);
        Assert.True(undo.Success);
        Assert.Equal("Opt_Undo_AlreadyRestored", undo.Message!.Key);
        Assert.Single(_h.Elevation.Requests);
    }

    [Fact]
    public async Task Unexpected_helper_exception_is_an_unknown_result_and_never_blocks()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        _h.Elevation.Handler = _ => throw new InvalidOperationException("canal rompu");

        var result = await Service.InstallAsync([scan.Candidates.First(c => c.Offer.UpdateId == Gpu)], false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DriverInstallStatus.Unknown, Assert.Single(result.Drivers).Status);
        Assert.Equal(ChangeStatus.Pending, _h.History.Sessions.Single().Changes.Single().Status);
    }

    [Fact]
    public async Task Devices_are_read_again_and_their_current_state_is_journaled()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        // Entre la recherche et l'installation, une autre version (toujours plus ancienne) a été mise en place.
        _h.Devices.Drivers[0] = _h.Devices.Drivers[0] with { Version = "30.0.101.1000", Date = new DateOnly(2022, 6, 1), InfName = "oem40.inf" };
        _h.Elevation.Handler = _ => new ElevatedResponse(OperationResult.Ok(), Installed((Gpu, DriverInstallStatus.Installed)));

        await Service.InstallAsync([scan.Candidates.First(c => c.Offer.UpdateId == Gpu)], false);

        var state = ChangeStateSerializer.Deserialize<DriverUpdateState>(_h.History.Sessions.Single().Changes.Single().BeforeState)!;
        Assert.Equal("30.0.101.1000", state.Devices[0].PreviousVersion);
        Assert.Equal("oem40.inf", state.Devices[0].PreviousInfName);
    }

    [Fact]
    public async Task Nothing_is_journaled_or_installed_when_the_situation_changed_or_devices_are_unreadable()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        var gpu = scan.Candidates.First(c => c.Offer.UpdateId == Gpu);

        _h.Devices.Drivers[0] = _h.Devices.Drivers[0] with { Version = "31.0.101.2125" };
        var changed = await Service.InstallAsync([gpu], false);
        Assert.Equal("Drv_Install_Changed", changed.Outcome.Message!.Key);

        _h.Devices.Fails = true;
        var unreadable = await Service.InstallAsync([gpu], false);
        Assert.Equal(DriverInstallStop.DeviceReadFailed, unreadable.Stop);
        Assert.Equal("Drv_Stop_DeviceReadFailed", unreadable.Outcome.Message!.Key);

        Assert.Empty(_h.History.Sessions);
        Assert.Empty(_h.Elevation.Requests);
    }

    [Theory]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, DriverInstallStop.ExcludedByPolicy)]
    [InlineData(@"SOFTWARE\Microsoft\PolicyManager\current\device\Update", "ExcludeWUDriversInQualityUpdate", 1, DriverInstallStop.ExcludedByPolicy)]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\wuauserv", "Start", 4, DriverInstallStop.UpdateServiceDisabled)]
    public async Task Configuration_is_checked_again_at_installation_time(string key, string name, int value, DriverInstallStop stop)
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        Hklm(key, name, value);

        var result = await Service.InstallAsync([scan.Candidates.First(c => c.Offer.UpdateId == Gpu)], false);

        Assert.Equal(stop, result.Stop);
        Assert.Equal("Drv_Stop_" + stop, result.Outcome.Message!.Key);
        Assert.Empty(_h.Elevation.Requests);
        Assert.Empty(_h.History.Sessions);
    }

    [Fact]
    public async Task Restart_pending_since_the_search_blocks_installation()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        _h.Registry.CreateKey(RegistryHiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");

        var result = await Service.InstallAsync([scan.Candidates.First(c => c.Offer.UpdateId == Gpu)], false);

        Assert.Equal(DriverInstallStop.RebootPending, result.Stop);
        Assert.Empty(_h.Elevation.Requests);
    }

    [Fact]
    public async Task Enabled_system_protection_is_recorded_in_history()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        _h.Elevation.Handler = _ => new ElevatedResponse(OperationResult.Ok(), DriverElevatedData.EncodeInstall(DriverInstallStop.None, RestorePointStatus.Created,
            RestorePointAt, protectionEnabled: true, [new DriverInstallOutcome(Gpu, DriverInstallStatus.Installed, 0, false, "31.0.101.2125", null)], false));

        var result = await Service.InstallAsync([scan.Candidates.First(c => c.Offer.UpdateId == Gpu)], enableSystemProtection: true);

        Assert.True(result.ProtectionEnabled);
        var protection = Assert.Single(_h.History.Sessions.Single().Changes, c => c.Kind == ChangeKinds.SystemProtection);
        Assert.Equal(ChangeStatus.Irreversible, protection.Status);
        Assert.False(protection.Reversible);
        Assert.Equal("Drv_Change_ProtectionEnabled", protection.Description.Key);
        Assert.Contains(_h.Journal.Entries, e => e.Message.Key == "Drv_Journal_ProtectionEnabled");
    }

    [Fact]
    public async Task Two_updates_for_the_same_device_are_installed_one_at_a_time()
    {
        SetupTwoUpdates();
        _h.DriverSource.Result = new DriverSearchResult(OperationResult.Ok(),
        [
            DriverSamples.Offer(updateId: Gpu),
            DriverSamples.Offer(updateId: Wifi, driverClass: "Extension", version: "1.0.0.7"),
        ], false, false);
        var scan = await Service.ScanAsync();

        var result = await Service.InstallAsync(scan.Candidates.Where(c => c.CanInstall).ToList(), false);

        Assert.Equal("Drv_Install_SameDevice", result.Outcome.Message!.Key);
        Assert.Empty(_h.Elevation.Requests);
        Assert.Empty(_h.History.Sessions);
    }

    [Fact]
    public async Task Unreadable_devices_at_search_time_block_installation()
    {
        SetupTwoUpdates();
        _h.Devices.Fails = true;

        var scan = await Service.ScanAsync();

        Assert.Equal(DriverScanState.Ready, scan.State);
        Assert.NotNull(scan.Error);
        Assert.False(scan.CanInstall);
    }

    [Fact]
    public async Task Protection_enabled_for_an_on_demand_restore_point_is_recorded_in_history()
    {
        _h.Elevation.Handler = _ => new ElevatedResponse(OperationResult.Ok(), DriverElevatedData.EncodeRestorePoint(RestorePointStatus.Created, RestorePointAt, true));

        var result = await Service.CreateRestorePointAsync(enableSystemProtection: true);

        Assert.True(result.ProtectionEnabled);
        var session = Assert.Single(_h.History.Sessions);
        var change = Assert.Single(session.Changes);
        Assert.Equal(ChangeKinds.SystemProtection, change.Kind);
        Assert.Equal(ChangeStatus.Irreversible, change.Status);
        Assert.NotEqual(SessionStatus.InProgress, session.Status);
    }

    [Fact]
    public async Task Installation_and_on_demand_restore_point_never_overlap()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _h.Elevation.Handler = request =>
        {
            if (request.Operation == ElevatedDriverOperations.RestorePoint)
                return new ElevatedResponse(OperationResult.Ok(), DriverElevatedData.EncodeRestorePoint(RestorePointStatus.Created, RestorePointAt, false));
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new ElevatedResponse(OperationResult.Ok(), Installed((Gpu, DriverInstallStatus.Installed)));
        };

        var install = Service.InstallAsync([scan.Candidates.First(c => c.Offer.UpdateId == Gpu)], false);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var restorePoint = Service.CreateRestorePointAsync(false);
        await Task.Delay(150);

        Assert.False(restorePoint.IsCompleted);
        Assert.Single(_h.Elevation.Requests);
        release.Set();
        await install.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True((await restorePoint.WaitAsync(TimeSpan.FromSeconds(10))).IsCreated);
        Assert.Equal([ElevatedDriverOperations.Install, ElevatedDriverOperations.RestorePoint], _h.Elevation.Requests.Select(r => r.Operation));
    }

    // ---- Sources officielles et point de restauration à la demande ---------------------------------------------

    [Fact]
    public async Task Scan_lists_official_sources_of_the_pc_and_graphics_makers()
    {
        SetupTwoUpdates();
        _h.Devices.Computer = new Core.Drivers.ComputerIdentity("HP", "Victus 15", "HP");

        var scan = await Service.ScanAsync();

        Assert.Equal(["hp", "intel"], scan.Sources.Select(s => s.Id));
        Assert.Equal("Victus 15", scan.Computer!.Model);
    }

    [Fact]
    public async Task Sources_are_hidden_when_the_organization_manages_drivers()
    {
        Hklm(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1);
        _h.Devices.Computer = new Core.Drivers.ComputerIdentity("Dell Inc.", "Latitude", null);

        var scan = await Service.ScanAsync();

        Assert.Empty(scan.Sources);
    }

    [Fact]
    public async Task Sources_remain_available_when_the_update_service_is_disabled()
    {
        Hklm(@"SYSTEM\CurrentControlSet\Services\wuauserv", "Start", 4);
        _h.Devices.Computer = new Core.Drivers.ComputerIdentity("LENOVO", "ThinkPad T480", "LENOVO");

        var scan = await Service.ScanAsync();

        Assert.Equal(["lenovo"], scan.Sources.Select(s => s.Id));
    }

    [Theory]
    [InlineData(RestorePointStatus.Created, true, "Drv_RestorePoint_Created")]
    [InlineData(RestorePointStatus.Disabled, false, "Drv_Stop_ProtectionDisabled")]
    [InlineData(RestorePointStatus.Failed, false, "Opt_RestorePoint_Failed")]
    public async Task On_demand_restore_point_is_created_by_the_elevated_helper(RestorePointStatus status, bool success, string key)
    {
        _h.Elevation.Handler = _ => new ElevatedResponse(OperationResult.Ok(),
            DriverElevatedData.EncodeRestorePoint(status, status == RestorePointStatus.Created ? RestorePointAt : null, false));

        var result = await Service.CreateRestorePointAsync(enableSystemProtection: true);

        Assert.Equal(success, result.Outcome.Success);
        Assert.Equal(key, result.Outcome.Message!.Key);
        var request = Assert.Single(_h.Elevation.Requests);
        Assert.Equal(ElevatedDriverOperations.RestorePoint, request.Operation);
        Assert.Equal("true", request.Parameters[DriverElevatedData.EnableProtectionKey]);
        Assert.Contains(_h.Journal.Entries, e => e.Message.Key == (success ? "Drv_Journal_RestorePointManual" : "Opt_RestorePoint_Failed"));
    }

    [Fact]
    public async Task On_demand_restore_point_respects_the_organization_policy_and_declined_permission()
    {
        Hklm(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", "DisableSR", 1);
        Assert.Equal("Drv_Stop_ProtectionPolicy", (await Service.CreateRestorePointAsync(true)).Outcome.Message!.Key);
        Assert.Empty(_h.Elevation.Requests);

        _h.Registry.DeleteValue(new RegistryLocation(RegistryHiveKind.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore"), "DisableSR");
        _h.Elevation.UserCancels = true;
        var declined = await Service.CreateRestorePointAsync(false);
        Assert.Equal(OperationErrorKind.ElevationCancelled, declined.Outcome.Error);
        Assert.False(declined.IsCreated);
    }

    // ---- Retour au pilote précédent ----------------------------------------------------------------------------

    [Fact]
    public async Task Installed_driver_can_be_rolled_back_from_the_journal()
    {
        SetupTwoUpdates();
        var scan = await Service.ScanAsync();
        _h.Elevation.Handler = request => request.Operation == ElevatedDriverOperations.Install
            ? new ElevatedResponse(OperationResult.Ok(), Installed((Gpu, DriverInstallStatus.Installed)))
            : new ElevatedResponse(OperationResult.Ok(), DriverElevatedData.EncodeRollback(
                [new DriverRollbackOutcome(DriverSamples.Device().InstanceId, DriverRollbackStatus.RolledBack, "27.20.100.8681", 0)], false));
        var result = await Service.InstallAsync([scan.Candidates.First(c => c.Offer.UpdateId == Gpu)], false);
        var change = _h.History.Sessions.Single().Changes.Single();
        // Après installation, le périphérique utilise le nouveau pilote.
        _h.Devices.Drivers[0] = _h.Devices.Drivers[0] with { Version = "31.0.101.2125" };

        var undo = await Service.RollbackAsync(change.Id);

        Assert.True(undo.Success);
        Assert.Equal("Drv_Rollback_Done", undo.Message!.Key);
        var rollback = _h.Elevation.Requests.Last();
        Assert.Equal(ElevatedDriverOperations.Rollback, rollback.Operation);
        Assert.Equal(DriverSamples.Device().InstanceId, rollback.Parameters[DriverElevatedData.DevicesKey]);
        Assert.Equal("27.20.100.8681", rollback.Parameters[DriverElevatedData.PreviousVersionsKey]);
        Assert.Equal("31.0.101.2125", rollback.Parameters[DriverElevatedData.InstalledVersionKey]);
        Assert.Equal(3, rollback.Parameters.Count);
        Assert.Equal(ChangeStatus.RolledBack, _h.History.Sessions.Single().Changes.Single().Status);
        Assert.NotNull(result.SessionId);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public sealed class DriverUpdateChangeHandlerTests
{
    private readonly FakeDeviceDriverProvider _devices = new();
    private readonly FakeElevationService _elevation = new();

    private DriverUpdateChangeHandler Handler => new(_devices, _elevation);

    private static ChangeRecord Change(DriverUpdateState state, bool reversible = true) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        OptimizationId = DriverUpdateService.OptimizationId,
        Kind = ChangeKinds.DriverUpdate,
        Target = "driver:GPU",
        Description = TextRef.Of("Drv_Change_Description", "GPU", "1", "2"),
        BeforeState = ChangeStateSerializer.Serialize(state),
        Reversible = reversible,
        Status = ChangeStatus.Applied,
        RecordedAt = DateTimeOffset.UnixEpoch,
    };

    private const string Device2 = @"PCI\VEN_8086&DEV_5917&SUBSYS_00000000&REV_07\3&11583659&0&18";

    private static DriverUpdateState State(string? previous = "27.20.100.8681", params DriverDeviceState[] extra)
        => new("0c3a9a55-5b0f-4f1d-9b3e-2a6b7f0c1d2e", "Intel - Display - 31.0.101.2125", "GPU", "Display", "31.0.101.2125",
            [new DriverDeviceState(DriverSamples.Device().InstanceId, previous, new DateOnly(2021, 3, 12), "Intel", "oem12.inf"), .. extra]);

    [Fact]
    public void Undo_requires_a_valid_reversible_state()
    {
        Assert.True(Handler.CanUndo(Change(State())));
        Assert.False(Handler.CanUndo(Change(State(), reversible: false)));
        Assert.False(Handler.CanUndo(Change(State()) with { BeforeState = "{}" }));
        Assert.False(Handler.CanUndo(Change(State(previous: null))));
        Assert.False(Handler.CanUndo(Change(State() with { NewVersion = null })));
        Assert.False(Handler.CanUndo(Change(State() with { Devices = [new DriverDeviceState("sans-separateur", "1.0", null, null, null)] })));
    }

    [Fact]
    public async Task Previous_driver_already_in_place_needs_no_permission()
    {
        _devices.Drivers.Add(DriverSamples.Device());

        var result = await Handler.UndoAsync(Change(State()));

        Assert.True(result.Success);
        Assert.Equal("Opt_Undo_AlreadyRestored", result.Message!.Key);
        Assert.Empty(_elevation.Requests);
    }

    [Fact]
    public async Task Removed_device_is_not_a_success_and_points_to_the_restore_point()
    {
        var result = await Handler.UndoAsync(Change(State()));

        Assert.False(result.Success);
        Assert.Equal(OperationErrorKind.NotFound, result.Error);
        Assert.Equal("Drv_Undo_DeviceGone", result.Message!.Key);
        Assert.Empty(_elevation.Requests);
    }

    [Fact]
    public async Task Driver_changed_since_the_update_is_never_replaced()
    {
        _devices.Drivers.Add(DriverSamples.Device(version: "32.0.101.6000"));

        var result = await Handler.UndoAsync(Change(State()));

        Assert.False(result.Success);
        Assert.Equal("Drv_Undo_Changed", result.Message!.Key);
        Assert.Empty(_elevation.Requests);
    }

    [Fact]
    public async Task Each_device_is_sent_with_its_own_previous_version()
    {
        _devices.Drivers.Add(DriverSamples.Device(version: "31.0.101.2125"));
        _devices.Drivers.Add(DriverSamples.Device(instanceId: Device2, version: "26.20.100.7000"));
        _elevation.Handler = _ => new ElevatedResponse(OperationResult.Ok(), DriverElevatedData.EncodeRollback(
            [new DriverRollbackOutcome(DriverSamples.Device().InstanceId, DriverRollbackStatus.RolledBack, "27.20.100.8681", 0),
             new DriverRollbackOutcome(Device2, DriverRollbackStatus.AlreadyPrevious, "26.20.100.7000", 0)], false));

        var result = await Handler.UndoAsync(Change(State(extra: new DriverDeviceState(Device2, "26.20.100.7000", null, "Intel", "oem9.inf"))));

        Assert.True(result.Success);
        Assert.Equal("Drv_Rollback_Done", result.Message!.Key);
        var request = Assert.Single(_elevation.Requests);
        Assert.Equal(DriverSamples.Device().InstanceId + "|" + Device2, request.Parameters[DriverElevatedData.DevicesKey]);
        Assert.Equal("27.20.100.8681|26.20.100.7000", request.Parameters[DriverElevatedData.PreviousVersionsKey]);
    }

    [Fact]
    public async Task Nothing_is_done_or_concluded_while_an_elevated_helper_is_still_running()
    {
        // Ex. installation dont l'application a cessé d'attendre la fin : le pilote précédent semble en place, mais
        // l'installation peut encore aboutir — l'annulation ne doit pas être déclarée réussie.
        _devices.Drivers.Add(DriverSamples.Device());
        _elevation.HelperRunning = true;

        var result = await Handler.UndoAsync(Change(State()) with { Status = ChangeStatus.Pending });

        Assert.False(result.Success);
        Assert.Equal(OperationErrorKind.InUse, result.Error);
        Assert.Equal("Drv_Undo_HelperRunning", result.Message!.Key);
        Assert.Empty(_elevation.Requests);
    }

    [Fact]
    public async Task Unreadable_device_list_lets_the_helper_decide()
    {
        _devices.Fails = true;
        _elevation.Handler = _ => new ElevatedResponse(OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drv_Rollback_Failed")),
            DriverElevatedData.EncodeRollback([new DriverRollbackOutcome(DriverSamples.Device().InstanceId, DriverRollbackStatus.Failed, null, 0)], false));

        var result = await Handler.UndoAsync(Change(State()));

        Assert.Single(_elevation.Requests);
        Assert.False(result.Success);
        Assert.Equal("Drv_Rollback_Failed", result.Message!.Key);
    }

    [Fact]
    public void Partial_rollback_is_never_reported_as_success()
    {
        var rolled = new DriverRollbackOutcome(@"PCI\A\1", DriverRollbackStatus.RolledBack, "1.0", 0);
        Assert.Equal("Drv_Undo_DeviceGone", DriverUpdateChangeHandler.Summarize([rolled, new(@"PCI\A\2", DriverRollbackStatus.DeviceGone, null, 0)], false).Message!.Key);
        Assert.Equal("Drv_Undo_Changed", DriverUpdateChangeHandler.Summarize([rolled, new(@"PCI\A\2", DriverRollbackStatus.Changed, "2.0", 0)], false).Message!.Key);
        Assert.Equal("Drv_Rollback_Failed", DriverUpdateChangeHandler.Summarize([rolled, new(@"PCI\A\2", DriverRollbackStatus.Failed, null, 5)], false).Message!.Key);
        Assert.False(DriverUpdateChangeHandler.Summarize([], false).Success);
        Assert.Equal("Drv_Rollback_DoneRestart", DriverUpdateChangeHandler.Summarize([rolled], true).Message!.Key);
    }

    [Fact]
    public async Task Rollback_failure_points_to_the_restore_point()
    {
        _devices.Drivers.Add(DriverSamples.Device(version: "31.0.101.2125"));
        _elevation.Handler = _ => new ElevatedResponse(OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drv_Rollback_Failed"), "1 échec(s)"),
            DriverElevatedData.EncodeRollback([new DriverRollbackOutcome(DriverSamples.Device().InstanceId, DriverRollbackStatus.Failed, null, 259)], false));

        var result = await Handler.UndoAsync(Change(State()));

        Assert.False(result.Success);
        Assert.Equal("Drv_Rollback_Failed", result.Message!.Key);
    }

    [Fact]
    public async Task Rollback_requiring_restart_says_so()
    {
        _devices.Drivers.Add(DriverSamples.Device(version: "31.0.101.2125"));
        _elevation.Handler = _ => new ElevatedResponse(OperationResult.Ok(),
            DriverElevatedData.EncodeRollback([new DriverRollbackOutcome(DriverSamples.Device().InstanceId, DriverRollbackStatus.RolledBack, null, 0)], true));

        var result = await Handler.UndoAsync(Change(State()));

        Assert.True(result.Success);
        Assert.Equal("Drv_Rollback_DoneRestart", result.Message!.Key);
    }

    [Fact]
    public async Task Declined_permission_is_reported()
    {
        _devices.Drivers.Add(DriverSamples.Device(version: "31.0.101.2125"));
        _elevation.UserCancels = true;

        var result = await Handler.UndoAsync(Change(State()));

        Assert.Equal(OperationErrorKind.ElevationCancelled, result.Error);
    }
}

public sealed class WriteAheadBatchTests
{
    [Fact]
    public async Task Only_recorded_items_run_and_receive_their_result()
    {
        var batch = new WriteAheadBatch<string>(["a", "b", "c"]);
        var a = batch.ArriveAsync("a", CancellationToken.None);
        batch.Observe("b", Task.CompletedTask); // refusé : son action n'est jamais appelée
        var c = batch.ArriveAsync("c", CancellationToken.None);

        var ready = await batch.Ready;
        batch.Complete(new Dictionary<string, string> { ["a"] = "ok" });

        Assert.Equal(["a", "c"], ready);
        Assert.Equal("ok", await a);
        Assert.Null(await c);
    }

    [Fact]
    public async Task An_arrived_item_is_not_requalified_when_its_apply_completes()
    {
        var batch = new WriteAheadBatch<string>(["a"]);
        var arrived = batch.ArriveAsync("a", CancellationToken.None);
        batch.Observe("a", Task.CompletedTask);

        Assert.Equal(["a"], await batch.Ready);
        batch.Complete(new Dictionary<string, string>());
        Assert.Null(await arrived);
    }

    [Fact]
    public void Duplicate_ids_are_refused() => Assert.Throws<ArgumentException>(() => new WriteAheadBatch<int>(["a", "A"]));

    [Fact]
    public async Task Empty_batch_is_ready_immediately() => Assert.Empty(await new WriteAheadBatch<int>([]).Ready);
}
