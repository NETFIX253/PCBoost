using System.Buffers.Binary;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Startup;
using PCBoost.TestUtilities;

namespace PCBoost.Optimization.Tests;

public sealed class StartupApprovedTests
{
    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    [InlineData(new byte[] { 0x01, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    public void IsEnabled_ReadsFirstByteParity(byte[] value, bool expected)
        => Assert.Equal(expected, StartupApproved.IsEnabled(RegistryValueData.Binary(value)));

    [Fact]
    public void IsEnabled_MissingValueMeansEnabled() => Assert.True(StartupApproved.IsEnabled(null));

    [Fact]
    public void EnabledAndDisabledValues_MatchTaskManagerFormat()
    {
        var now = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var enabled = StartupApproved.EnabledValue();
        var disabled = StartupApproved.DisabledValue(now);

        Assert.Equal(12, enabled.Length);
        Assert.Equal(0x02, enabled[0]);
        Assert.All(enabled[1..], b => Assert.Equal(0, b));
        Assert.Equal(12, disabled.Length);
        Assert.Equal(new byte[] { 0x03, 0, 0, 0 }, disabled[..4]);
        Assert.Equal(now.ToFileTime(), BinaryPrimitives.ReadInt64LittleEndian(disabled.AsSpan(4)));
    }

    [Fact]
    public void ApprovedLocations_ForMachineEntriesAreOnTheElevatorAllowList()
    {
        foreach (var location in new[] { StartupLocation.RegistryRunMachine, StartupLocation.RegistryRunMachine32, StartupLocation.StartupFolderCommon })
        {
            var approved = StartupApproved.ApprovedLocation(location)!;
            Assert.Equal(RegistryHiveKind.LocalMachine, approved.Hive);
            Assert.True(Core.Security.ForbiddenTargetPolicy.IsAllowedElevatedRegistryPath(approved.KeyPath));
        }
        Assert.Null(StartupApproved.ApprovedLocation(StartupLocation.ScheduledTaskLogon));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Discord\\Update.exe\" --processStart Discord.exe", @"C:\Program Files\Discord\Update.exe")]
    [InlineData(@"C:\Program Files\Vendor App\app.exe /background", @"C:\Program Files\Vendor App\app.exe")]
    [InlineData(@"%LOCALAPPDATA%\Microsoft\OneDrive\OneDrive.exe /background", @"C:\Users\Test\AppData\Local\Microsoft\OneDrive\OneDrive.exe")]
    [InlineData(@"%ProgramFiles%\Tool\tool.exe", @"C:\Program Files\Tool\tool.exe")]
    [InlineData(@"C:\Apps\my.exe.dir\real.exe -x", @"C:\Apps\my.exe.dir\real.exe")]
    [InlineData(@"C:\Tools\script.bat", @"C:\Tools\script.bat")]
    public void CommandLineParser_ExtractsExecutable(string command, string expected)
        => Assert.Equal(expected, CommandLineParser.ExtractExecutable(command, new InMemoryFileSystemProvider()));

    [Fact]
    public void CommandLineParser_ResolvesBareSystemExecutable()
    {
        var fs = new InMemoryFileSystemProvider();
        fs.AddFile(@"C:\Windows\System32\rundll32.exe", 10);
        Assert.Equal(@"C:\Windows\System32\rundll32.exe", CommandLineParser.ExtractExecutable("rundll32.exe shell32.dll,Control_RunDLL", fs));
        Assert.Null(CommandLineParser.ExtractExecutable("   ", fs));
    }
}

public sealed class StartupScannerTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [Fact]
    public async Task Scan_ReadsRunKeysFoldersAndNonMicrosoftTasks_WithStartupApprovedState()
    {
        var discord = _h.AddSignedExe(@"C:\Users\Test\AppData\Local\Discord\Update.exe", "Discord Inc.", "Discord");
        var vendor = _h.AddSignedExe(@"C:\Program Files\Vendor\vendor.exe", "Vendor Ltd");
        var legacy = _h.AddSignedExe(@"C:\Program Files (x86)\Legacy\legacy.exe", "Legacy Corp");
        var spotify = _h.AddSignedExe(@"C:\Users\Test\AppData\Roaming\Spotify\Spotify.exe", "Spotify AB", "Spotify");
        _h.Registry.Set(RegistryHiveKind.CurrentUser, Run, "Discord", RegistryValueData.String($"\"{discord}\" --processStart Discord.exe"));
        _h.Registry.Set(RegistryHiveKind.LocalMachine, Run, "Vendor", RegistryValueData.String(vendor), RegistryViewKind.Registry64);
        _h.Registry.Set(RegistryHiveKind.LocalMachine, Run, "Legacy", RegistryValueData.String(legacy), RegistryViewKind.Registry32);
        _h.Registry.Set(RegistryHiveKind.CurrentUser, StartupApproved.UserApprovedRun, "Discord", RegistryValueData.Binary(StartupApproved.DisabledValue(_h.Clock.UtcNow)));

        const string startupFolder = @"C:\Users\Test\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
        _h.FileSystem.AddFile(startupFolder + @"\Spotify.lnk", 1);
        _h.FileSystem.AddFile(startupFolder + @"\desktop.ini", 1);
        _h.Shortcuts.Targets[startupFolder + @"\Spotify.lnk"] = new ShortcutTarget(spotify, "--minimized", null);

        _h.FakeTasks.Tasks.Add(new ScheduledTaskInfo(@"\Vendor\Updater", "Updater", "Vendor", vendor, "/check", true, false));
        _h.FakeTasks.Tasks.Add(new ScheduledTaskInfo(@"\Microsoft\Windows\Something", "Something", "Microsoft Corporation", @"C:\Windows\System32\x.exe", null, true, true));

        var entries = await _h.Get<IStartupProvider>().GetEntriesAsync();

        Assert.Equal(5, entries.Count);
        var d = entries.Single(e => e.ItemName == "Discord");
        Assert.Equal(StartupLocation.RegistryRunUser, d.Location);
        Assert.False(d.IsEnabled);
        Assert.Equal(discord, d.ExecutablePath);
        Assert.True(d.ExecutableExists);
        Assert.Equal("Discord Inc.", d.Publisher);
        Assert.False(d.RequiresElevation);

        var v = entries.Single(e => e.ItemName == "Vendor");
        Assert.Equal(StartupLocation.RegistryRunMachine, v.Location);
        Assert.True(v.IsEnabled);
        Assert.True(v.RequiresElevation);

        Assert.Equal(StartupLocation.RegistryRunMachine32, entries.Single(e => e.ItemName == "Legacy").Location);

        var s = entries.Single(e => e.ItemName == "Spotify.lnk");
        Assert.Equal(StartupLocation.StartupFolderUser, s.Location);
        Assert.Equal(spotify, s.ExecutablePath);
        Assert.Equal("Spotify", s.Name);

        var task = entries.Single(e => e.Location == StartupLocation.ScheduledTaskLogon);
        Assert.Equal(@"\Vendor\Updater", task.SourcePath);
        Assert.DoesNotContain(entries, e => e.ItemName == "desktop.ini" || e.ItemName == "Something");
    }

    [Fact]
    public async Task Scan_FlagsMissingExecutable_AndSecuritySoftware()
    {
        _h.Registry.Set(RegistryHiveKind.CurrentUser, Run, "Ghost", RegistryValueData.String(@"C:\Program Files\Removed\ghost.exe"));
        var security = _h.AddSignedExe(@"C:\Windows\System32\SecurityHealthSystray.exe", "Microsoft Windows", "Windows Security notification icon");
        _h.Registry.Set(RegistryHiveKind.LocalMachine, Run, "SecurityHealth", RegistryValueData.String(security), RegistryViewKind.Registry64);

        var entries = await _h.Get<IStartupProvider>().GetEntriesAsync();

        var ghost = entries.Single(e => e.ItemName == "Ghost");
        Assert.False(ghost.ExecutableExists);
        var sec = entries.Single(e => e.ItemName == "SecurityHealth");
        Assert.True(sec.IsSecuritySoftware);
        Assert.True(sec.IsMicrosoft);
    }
}

public sealed class StartupServiceTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private string AddRunEntry(string name, string exe, string signer, RegistryHiveKind hive = RegistryHiveKind.CurrentUser, string? description = null)
    {
        _h.AddSignedExe(exe, signer, description);
        _h.Registry.Set(hive, Run, name, RegistryValueData.String($"\"{exe}\""), hive == RegistryHiveKind.LocalMachine ? RegistryViewKind.Registry64 : RegistryViewKind.Default);
        return exe;
    }

    private async Task<StartupEntry> Entry(string itemName)
        => (await _h.Get<IStartupService>().GetEntriesAsync()).Single(e => e.ItemName == itemName);

    [Fact]
    public async Task Impact_IsNotMeasured_WhenProcessIsNotRunning()
    {
        AddRunEntry("Discord", @"C:\Apps\Discord\Discord.exe", "Discord Inc.");

        var entry = await Entry("Discord");

        Assert.Equal(StartupImpact.NotMeasured, entry.Impact);
        Assert.False(entry.Evidence!.ProcessRunning);
        Assert.Null(entry.Evidence.WorkingSetBytes);
    }

    [Theory]
    [InlineData(400L * 1024 * 1024, 1, 0L, StartupImpact.High)]
    [InlineData(50L * 1024 * 1024, 90, 0L, StartupImpact.High)]
    [InlineData(50L * 1024 * 1024, 1, 600L * 1024 * 1024, StartupImpact.High)]
    [InlineData(150L * 1024 * 1024, 1, 0L, StartupImpact.Medium)]
    [InlineData(20L * 1024 * 1024, 20, 0L, StartupImpact.Medium)]
    [InlineData(20L * 1024 * 1024, 2, 0L, StartupImpact.Low)]
    public async Task Impact_IsMeasuredFromTheRunningProcess(long workingSet, int cpuSeconds, long ioRead, StartupImpact expected)
    {
        var exe = AddRunEntry("Discord", @"C:\Apps\Discord\Discord.exe", "Discord Inc.");
        _h.Processes.Add(500, "Discord.exe", exe, workingSet, TimeSpan.FromSeconds(cpuSeconds), ioRead: ioRead);

        var entry = await Entry("Discord");

        Assert.Equal(expected, entry.Impact);
        Assert.True(entry.Evidence!.ProcessRunning);
        Assert.Equal(workingSet, entry.Evidence.WorkingSetBytes);
    }

    [Fact]
    public async Task Recommendations_AreConservativeAndExplained()
    {
        AddRunEntry("SecurityHealth", @"C:\Windows\System32\SecurityHealthSystray.exe", "Microsoft Windows", RegistryHiveKind.LocalMachine);
        AddRunEntry("RtkAudUService", @"C:\Windows\System32\RtkAudUService64.exe", "Realtek Semiconductor Corp.", RegistryHiveKind.LocalMachine);
        AddRunEntry("Discord", @"C:\Apps\Discord\Discord.exe", "Discord Inc.");
        AddRunEntry("OneDrive", @"C:\Users\Test\AppData\Local\Microsoft\OneDrive\OneDrive.exe", "Microsoft Corporation");
        AddRunEntry("Avast", @"C:\Program Files\Avast Software\Avast\AvastUI.exe", "AVAST Software s.r.o.");
        _h.FileSystem.AddFile(@"C:\Apps\Unknown\tool.exe", 10);
        _h.Registry.Set(RegistryHiveKind.CurrentUser, Run, "Tool", RegistryValueData.String(@"C:\Apps\Unknown\tool.exe"));
        _h.Registry.Set(RegistryHiveKind.CurrentUser, Run, "Ghost", RegistryValueData.String(@"C:\Apps\Gone\ghost.exe"));

        var entries = await _h.Get<IStartupService>().GetEntriesAsync();
        StartupEntry E(string n) => entries.Single(e => e.ItemName == n);

        Assert.Equal(StartupRecommendation.Keep, E("SecurityHealth").Recommendation);
        Assert.Equal("Opt_StartupReason_Security", E("SecurityHealth").RecommendationReason!.Key);
        Assert.Equal(StartupRecommendation.Keep, E("Avast").Recommendation);
        Assert.Equal(StartupRecommendation.Keep, E("RtkAudUService").Recommendation);
        Assert.Equal("Opt_StartupReason_Driver", E("RtkAudUService").RecommendationReason!.Key);
        Assert.Equal(StartupRecommendation.CanDisable, E("Discord").Recommendation);
        Assert.Equal(StartupRecommendation.Optional, E("OneDrive").Recommendation);
        Assert.Equal(StartupRecommendation.Review, E("Tool").Recommendation);
        Assert.Equal("Opt_StartupReason_Unsigned", E("Tool").RecommendationReason!.Key);
        Assert.Equal(StartupRecommendation.Review, E("Ghost").Recommendation);
        Assert.Equal("Opt_StartupReason_FileMissing", E("Ghost").RecommendationReason!.Key);
    }

    [Fact]
    public async Task GetEntries_NeverChangesAnything()
    {
        var exe = AddRunEntry("Discord", @"C:\Apps\Discord\Discord.exe", "Discord Inc.");
        _h.Processes.Add(500, "Discord.exe", exe, 900L * 1024 * 1024);

        await _h.Get<IStartupService>().GetEntriesAsync();
        await _h.Get<IStartupService>().GetEntriesAsync();

        Assert.Equal(0, _h.Registry.WriteCount);
        Assert.Empty(await _h.Get<IRollbackManager>().GetHistoryAsync());
    }

    [Fact]
    public async Task Disable_WritesTwelveByteDisabledValue_InItsOwnSession_AndRestoresExactly()
    {
        AddRunEntry("Discord", @"C:\Apps\Discord\Discord.exe", "Discord Inc.");
        var previous = new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        _h.Registry.Set(RegistryHiveKind.CurrentUser, StartupApproved.UserApprovedRun, "Discord", RegistryValueData.Binary(previous));
        var service = _h.Get<IStartupService>();
        var approved = StartupApproved.ApprovedLocation(StartupLocation.RegistryRunUser)!;

        var result = await service.SetEnabledAsync(await Entry("Discord"), enabled: false);

        Assert.True(result.Success);
        var written = (byte[])_h.Registry.GetValue(approved, "Discord")!.Value;
        Assert.Equal(12, written.Length);
        Assert.Equal(0x03, written[0]);
        Assert.Equal(_h.Clock.UtcNow.ToFileTime(), BinaryPrimitives.ReadInt64LittleEndian(written.AsSpan(4)));
        Assert.False((await Entry("Discord")).IsEnabled);

        var rollback = _h.Get<IRollbackManager>();
        var session = (await rollback.GetHistoryAsync()).Single();
        Assert.Equal(SessionType.Startup, session.Type);
        var change = session.Changes.Single();
        Assert.Equal(ChangeKinds.RegistryValue, change.Kind);
        Assert.Equal(Convert.ToBase64String(previous), ChangeStateSerializer.Deserialize<RegistryValueState>(change.BeforeState)!.BinaryBase64);
        Assert.Equal(Convert.ToBase64String(written), ChangeStateSerializer.Deserialize<RegistryValueState>(change.AfterState)!.BinaryBase64);

        var restore = await rollback.RestoreSessionAsync(session.Id);
        Assert.True(restore.Success);
        Assert.Equal(previous, (byte[])_h.Registry.GetValue(approved, "Discord")!.Value);
    }

    [Fact]
    public async Task Disable_ThenRestore_RemovesValueThatDidNotExist()
    {
        AddRunEntry("Discord", @"C:\Apps\Discord\Discord.exe", "Discord Inc.");
        var approved = StartupApproved.ApprovedLocation(StartupLocation.RegistryRunUser)!;

        await _h.Get<IStartupService>().SetEnabledAsync(await Entry("Discord"), false);
        Assert.NotNull(_h.Registry.GetValue(approved, "Discord"));
        var session = (await _h.Get<IRollbackManager>().GetHistoryAsync()).Single();
        await _h.Get<IRollbackManager>().RestoreSessionAsync(session.Id);

        Assert.Null(_h.Registry.GetValue(approved, "Discord"));
        Assert.True((await Entry("Discord")).IsEnabled);
    }

    [Fact]
    public async Task Enable_WritesTaskManagerEnabledValue()
    {
        AddRunEntry("Discord", @"C:\Apps\Discord\Discord.exe", "Discord Inc.");
        var approved = StartupApproved.ApprovedLocation(StartupLocation.RegistryRunUser)!;
        _h.Registry.Set(RegistryHiveKind.CurrentUser, StartupApproved.UserApprovedRun, "Discord", RegistryValueData.Binary(StartupApproved.DisabledValue(_h.Clock.UtcNow)));

        var result = await _h.Get<IStartupService>().SetEnabledAsync(await Entry("Discord"), true);

        Assert.True(result.Success);
        Assert.Equal(StartupApproved.EnabledValue(), (byte[])_h.Registry.GetValue(approved, "Discord")!.Value);
    }

    [Fact]
    public async Task Disable_MachineEntryWithoutAdminRights_UsesElevatedRegistrySet()
    {
        AddRunEntry("Vendor", @"C:\Program Files\Vendor\vendor.exe", "Vendor Ltd", RegistryHiveKind.LocalMachine);
        _h.Registry.LocalMachineWritable = false;

        var result = await _h.Get<IStartupService>().SetEnabledAsync(await Entry("Vendor"), false);

        Assert.True(result.Success);
        var request = Assert.Single(_h.Elevation.Requests);
        Assert.Equal(ElevatedOperations.RegistrySetValue, request.Operation);
        Assert.Equal(StartupApproved.MachineApprovedRun, request.Parameters["path"]);
        Assert.Equal("Vendor", request.Parameters["name"]);
        Assert.Equal(nameof(RegistryViewKind.Registry64), request.Parameters["view"]);
        var bytes = Convert.FromBase64String(request.Parameters["valueBase64"]);
        Assert.Equal(12, bytes.Length);
        Assert.Equal(0x03, bytes[0]);
    }

    [Fact]
    public async Task Disable_MachineEntry_UacRefused_ReportsCancelledAndChangeFailed()
    {
        AddRunEntry("Vendor", @"C:\Program Files\Vendor\vendor.exe", "Vendor Ltd", RegistryHiveKind.LocalMachine);
        _h.Registry.LocalMachineWritable = false;
        _h.Elevation.UserCancels = true;

        var result = await _h.Get<IStartupService>().SetEnabledAsync(await Entry("Vendor"), false);

        Assert.False(result.Success);
        Assert.Equal(OperationErrorKind.ElevationCancelled, result.Error);
        var session = (await _h.Get<IRollbackManager>().GetHistoryAsync()).Single();
        Assert.Equal(ChangeStatus.Failed, session.Changes.Single().Status);
        Assert.Equal(SessionStatus.Failed, session.Status);
    }

    [Fact]
    public async Task Disable_SecuritySoftware_IsRefused()
    {
        AddRunEntry("SecurityHealth", @"C:\Windows\System32\SecurityHealthSystray.exe", "Microsoft Windows", RegistryHiveKind.LocalMachine);

        var result = await _h.Get<IStartupService>().SetEnabledAsync(await Entry("SecurityHealth"), false);

        Assert.Equal(OperationErrorKind.Blocked, result.Error);
        Assert.Equal(0, _h.Registry.WriteCount);
    }

    [Fact]
    public async Task Disable_ScheduledTask_TogglesTaskAndRestores()
    {
        var exe = _h.AddSignedExe(@"C:\Program Files\Vendor\up.exe", "Vendor Ltd");
        _h.FakeTasks.Tasks.Add(new ScheduledTaskInfo(@"\Vendor\Updater", "Updater", "Vendor", exe, null, true, false));
        var entry = (await _h.Get<IStartupService>().GetEntriesAsync()).Single(e => e.Location == StartupLocation.ScheduledTaskLogon);

        var result = await _h.Get<IStartupService>().SetEnabledAsync(entry, false);

        Assert.True(result.Success);
        Assert.True(_h.FakeTasks.IsEnabled(@"\Vendor\Updater") == false);
        var session = (await _h.Get<IRollbackManager>().GetHistoryAsync()).Single();
        Assert.Equal(ChangeKinds.ScheduledTask, session.Changes.Single().Kind);
        await _h.Get<IRollbackManager>().RestoreSessionAsync(session.Id);
        Assert.True(_h.FakeTasks.IsEnabled(@"\Vendor\Updater") == true);
    }
}
