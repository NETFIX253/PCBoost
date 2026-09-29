using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Programs;
using PCBoost.Optimization.Programs;
using PCBoost.TestUtilities;

namespace PCBoost.Optimization.Tests;

public sealed class ProgramClassifierTests
{
    private static UninstallRegistryEntry E(string? name, string? publisher = "Éditeur", string? uninstall = @"""C:\App\uninstall.exe""",
        int? system = null, string? parent = null, string? release = null, int? noRemove = null)
        => new(new RegistryLocation(RegistryHiveKind.LocalMachine, "k"), name, publisher, "1.0", null, null, null, null, uninstall, system, parent, release, noRemove);

    [Fact]
    public void Updates_system_components_and_non_removable_entries_are_not_candidates()
    {
        Assert.True(ProgramClassifier.IsCandidate(E("VLC media player")));
        Assert.False(ProgramClassifier.IsCandidate(E(null)));
        Assert.False(ProgramClassifier.IsCandidate(E("App", uninstall: null)));
        Assert.False(ProgramClassifier.IsCandidate(E("App", system: 1)));
        Assert.False(ProgramClassifier.IsCandidate(E("App", noRemove: 1)));
        Assert.False(ProgramClassifier.IsCandidate(E("App", parent: "Office")));
        Assert.False(ProgramClassifier.IsCandidate(E("App", release: "Security Update")));
        Assert.False(ProgramClassifier.IsCandidate(E("Security Update for Microsoft Office (KB5002345)")));
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "Microsoft Corporation")]
    [InlineData("Microsoft .NET Runtime - 8.0.8 (x64)", "Microsoft Corporation")]
    [InlineData("Microsoft Windows Desktop Runtime - 8.0.8 (x64)", "Microsoft Corporation")]
    [InlineData("Microsoft Edge WebView2 Runtime", "Microsoft Corporation")]
    [InlineData("Windows App Runtime 1.6", "Microsoft Corporation")]
    [InlineData("Realtek High Definition Audio Driver", "Realtek Semiconductor Corp.")]
    [InlineData("AMD Chipset Software", "Advanced Micro Devices, Inc.")]
    [InlineData("NVIDIA Graphics Driver 560.94", "NVIDIA Corporation")]
    [InlineData("Malwarebytes version 5.1", "Malwarebytes")]
    [InlineData("Kaspersky Standard", "Kaspersky")]
    [InlineData("PCBoost", "Mohamed ABDOURAHMAN (DSI)")]
    public void Runtimes_drivers_security_and_pcboost_are_protected(string name, string publisher)
        => Assert.True(ProgramClassifier.IsProtected(E(name, publisher), "PCBoost"));

    [Theory]
    [InlineData("VLC media player", "VideoLAN")]
    [InlineData("7-Zip 24.08 (x64)", "Igor Pavlov")]
    [InlineData("Steam", "Valve Corporation")]
    [InlineData("Discord", "Discord Inc.")]
    public void Ordinary_applications_are_offered(string name, string publisher)
        => Assert.False(ProgramClassifier.IsProtected(E(name, publisher), "PCBoost"));

    [Theory]
    [InlineData(@"""C:\Program Files\VLC\vlc.exe"",0", @"C:\Program Files\VLC\vlc.exe")]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.ico", null)]
    [InlineData(@"%ProgramFiles%\App\app.exe", null)]
    [InlineData(null, null)]
    public void Icon_path_gives_main_executable(string? icon, string? expected) => Assert.Equal(expected, ProgramClassifier.ExecutableFromIcon(icon));

    [Theory]
    [InlineData("unins000.exe", true)]
    [InlineData("Uninstall.exe", true)]
    [InlineData("Update.exe", true)]
    [InlineData("vlc.exe", false)]
    [InlineData("steam.exe", false)]
    public void Helper_executables_are_recognized(string name, bool helper) => Assert.Equal(helper, ProgramClassifier.IsHelperExecutable(name));

    [Theory]
    [InlineData(@"C:\Program Files\VLC", true)]
    [InlineData(@"C:\Program Files", false)]
    [InlineData(@"C:\", false)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData(@"C:\WindowsApps\x", true)]
    [InlineData(@"relative\path", false)]
    public void Install_location_must_be_a_real_program_folder(string location, bool usable)
        => Assert.Equal(usable, ProgramClassifier.IsUsableInstallLocation(location, @"C:\Windows", [@"C:\Program Files", @"C:\Program Files (x86)", @"C:\Users\Test"]));

    [Fact]
    public void Install_date_is_parsed_strictly()
    {
        Assert.Equal(new DateOnly(2024, 3, 17), ProgramClassifier.ParseInstallDate("20240317"));
        Assert.Null(ProgramClassifier.ParseInstallDate("2024-03-17"));
        Assert.Null(ProgramClassifier.ParseInstallDate("20241317"));
    }
}

public sealed class ProgramInventoryServiceTests
{
    private const string Key = ProgramInventoryService.UninstallKey;
    private readonly InMemoryRegistryProvider _registry = new();
    private readonly InMemoryFileSystemProvider _files = new();
    private readonly FakeUninstallerLauncher _launcher = new();
    private readonly FakeElevationService _elevation = new();
    private readonly InMemoryKeyValueStore _store = new();
    private readonly FakeActivityJournal _journal = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    private ProgramInventoryService Create() => new(_registry, _files, _launcher, _elevation, _store, _journal, new TestAppInfo(), _clock);

    private void AddProgram(string subKey, string name, RegistryHiveKind hive = RegistryHiveKind.LocalMachine, RegistryViewKind view = RegistryViewKind.Registry64,
        string? uninstall = null, int? sizeKb = null, string? location = null, string? icon = null, string? publisher = "Éditeur", int? system = null)
    {
        void S(string n, string v) => _registry.Set(hive, $@"{Key}\{subKey}", n, RegistryValueData.String(v), view);
        S("DisplayName", name);
        if (publisher is not null) S("Publisher", publisher);
        S("DisplayVersion", "1.0");
        S("InstallDate", "20240101");
        S("UninstallString", uninstall ?? $@"""C:\Program Files\{name}\uninstall.exe""");
        if (location is not null) S("InstallLocation", location);
        if (icon is not null) S("DisplayIcon", icon);
        if (sizeKb is { } kb) _registry.Set(hive, $@"{Key}\{subKey}", "EstimatedSize", RegistryValueData.DWord(kb), view);
        if (system is { } sc) _registry.Set(hive, $@"{Key}\{subKey}", "SystemComponent", RegistryValueData.DWord(sc), view);
    }

    [Fact]
    public async Task Inventory_lists_offered_programs_with_size_and_hides_protected_ones()
    {
        AddProgram("VLC", "VLC media player", sizeKb: 40_000);
        AddProgram("VLC32", "VLC media player", view: RegistryViewKind.Registry32, sizeKb: 40_000);
        AddProgram("{VC}", "Microsoft Visual C++ 2015-2022 Redistributable (x64)", publisher: "Microsoft Corporation");
        AddProgram("Hidden", "Composant", system: 1);
        AddProgram("Discord", "Discord", hive: RegistryHiveKind.CurrentUser, view: RegistryViewKind.Default,
            uninstall: @"C:\Users\Test\AppData\Local\Discord\Update.exe --uninstall");

        var inventory = await Create().GetInventoryAsync();

        Assert.Equal(["Discord", "VLC media player"], inventory.Programs.Select(p => p.Name));
        Assert.Equal(1, inventory.HiddenCount);
        var vlc = inventory.Programs.Single(p => p.Name.StartsWith("VLC", StringComparison.Ordinal));
        Assert.Equal(40_000L * 1024, vlc.SizeBytes);
        Assert.True(vlc.SizeFromRegistry);
        Assert.Equal(new DateOnly(2024, 1, 1), vlc.InstallDate);
        Assert.Equal(ProgramScope.User, inventory.Programs.Single(p => p.Name == "Discord").Scope);
        Assert.True(vlc.CanUninstall);
    }

    [Fact]
    public async Task Size_is_measured_from_install_folder_when_not_declared()
    {
        AddProgram("Game", "Jeu", location: @"C:\Games\Jeu");
        _files.AddFile(@"C:\Games\Jeu\jeu.exe", 50_000_000);
        _files.AddFile(@"C:\Games\Jeu\data\pack.bin", 950_000_000);

        var program = (await Create().GetInventoryAsync()).Programs.Single();

        Assert.Equal(1_000_000_000, program.SizeBytes);
        Assert.False(program.SizeFromRegistry);
        Assert.Equal(@"C:\Games\Jeu\jeu.exe", program.MainExecutable);
    }

    [Fact]
    public async Task Last_use_prefers_windows_records_then_file_access()
    {
        AddProgram("A", "Alpha", location: @"C:\Apps\Alpha");
        AddProgram("B", "Beta", location: @"C:\Apps\Beta", icon: @"""C:\Apps\Beta\beta.exe"",0");
        _files.AddFile(@"C:\Apps\Alpha\alpha.exe", 1000).LastAccessUtc = _clock.UtcNow.AddDays(-200);
        _files.AddFile(@"C:\Apps\Alpha\unins000.exe", 5000).LastAccessUtc = _clock.UtcNow;
        _files.AddFile(@"C:\Apps\Beta\beta.exe", 1000).LastAccessUtc = _clock.UtcNow.AddDays(-2);
        await _store.SetAsync(ProgramInventoryService.LastRunKey,
            new ProgramInventoryService.LastRunCache(_clock.UtcNow, new Dictionary<string, DateTimeOffset> { ["beta.exe"] = _clock.UtcNow.AddDays(-150) }));

        var inventory = await Create().GetInventoryAsync();

        var alpha = inventory.Programs.Single(p => p.Name == "Alpha");
        Assert.Equal(LastUseSource.FileAccess, alpha.LastUseSource);
        Assert.Equal(_clock.UtcNow.AddDays(-200), alpha.LastUsed);
        Assert.True(alpha.IsRarelyUsed(_clock.UtcNow));
        var beta = inventory.Programs.Single(p => p.Name == "Beta");
        Assert.Equal(LastUseSource.WindowsPrefetch, beta.LastUseSource);
        Assert.Equal(_clock.UtcNow.AddDays(-150), beta.LastUsed);
        Assert.Equal(_clock.UtcNow, inventory.LastRunReadAt);
    }

    [Fact]
    public async Task Reading_last_run_uses_one_parameterless_elevated_request()
    {
        _elevation.Handler = _ => new ElevatedResponse(OperationResult.Ok(), HealthElevatedData.EncodeLastRun(new Dictionary<string, DateTimeOffset> { ["vlc.exe"] = _clock.UtcNow }));
        var result = await Create().ReadLastRunAsync();
        Assert.True(result.Success);
        var request = Assert.Single(_elevation.Requests);
        Assert.Equal(ElevatedHealthOperations.AppsLastRun, request.Operation);
        Assert.Empty(request.Parameters);
        Assert.NotNull(await _store.GetAsync<ProgramInventoryService.LastRunCache>(ProgramInventoryService.LastRunKey));
    }

    [Fact]
    public async Task Uninstall_runs_official_command_and_reports_removal()
    {
        AddProgram("VLC", "VLC media player");
        var service = Create();
        var program = (await service.GetInventoryAsync()).Programs.Single();
        _launcher.OnRun = _ => _registry.RemoveKey(new RegistryLocation(RegistryHiveKind.LocalMachine, $@"{Key}\VLC", RegistryViewKind.Registry64));

        var result = await service.UninstallAsync(program);

        Assert.Equal(UninstallOutcome.Removed, result.Outcome);
        Assert.Equal(@"C:\Program Files\VLC media player\uninstall.exe", Assert.Single(_launcher.Commands).FileName);
        Assert.Equal(["Programs_Journal_Started", "Programs_Journal_Removed"], _journal.Entries.Select(e => e.Message.Key));
        Assert.All(_journal.Entries, e => Assert.Equal(ActivityKind.Cleanup, e.Kind));
    }

    [Fact]
    public async Task Changed_command_is_not_run()
    {
        AddProgram("VLC", "VLC media player");
        var service = Create();
        var program = (await service.GetInventoryAsync()).Programs.Single();
        _registry.Set(RegistryHiveKind.LocalMachine, $@"{Key}\VLC", "UninstallString", RegistryValueData.String(@"""C:\Autre\x.exe"""), RegistryViewKind.Registry64);

        var result = await service.UninstallAsync(program);

        Assert.Equal(UninstallOutcome.Failed, result.Outcome);
        Assert.Equal(OperationErrorKind.Blocked, result.Error!.Error);
        Assert.Empty(_launcher.Commands);
    }

    [Fact]
    public async Task Declined_uninstaller_is_reported_and_program_stays()
    {
        AddProgram("VLC", "VLC media player");
        var service = Create();
        var program = (await service.GetInventoryAsync()).Programs.Single();
        _launcher.Result = OperationResult<int>.Fail(OperationErrorKind.ElevationCancelled);

        var result = await service.UninstallAsync(program);

        Assert.Equal(UninstallOutcome.Failed, result.Outcome);
        Assert.Equal(OperationErrorKind.ElevationCancelled, result.Error!.Error);
    }

    [Fact]
    public async Task Stopping_the_wait_leaves_the_program_listed_as_still_installed()
    {
        AddProgram("VLC", "VLC media player");
        var service = Create();
        var program = (await service.GetInventoryAsync()).Programs.Single();
        using var cts = new CancellationTokenSource();
        _launcher.OnRun = _ => cts.CancelAfter(50);

        var result = await service.UninstallAsync(program, cts.Token);

        Assert.Equal(UninstallOutcome.StillInstalled, result.Outcome);
        Assert.Equal(["Programs_Journal_Started"], _journal.Entries.Select(e => e.Message.Key));
    }

    [Fact]
    public async Task Program_already_gone_is_reported_as_removed_without_running_anything()
    {
        AddProgram("VLC", "VLC media player");
        var service = Create();
        var program = (await service.GetInventoryAsync()).Programs.Single();
        _registry.RemoveKey(new RegistryLocation(RegistryHiveKind.LocalMachine, $@"{Key}\VLC", RegistryViewKind.Registry64));
        Assert.Equal(UninstallOutcome.Removed, (await service.UninstallAsync(program)).Outcome);
        Assert.Empty(_launcher.Commands);
    }

    [Theory]
    [InlineData("LocalMachine|Registry64|VLC", true)]
    [InlineData("CurrentUser|Default|{GUID}", true)]
    [InlineData(@"LocalMachine|Registry64|..\..\Run", false)]
    [InlineData("Other|Registry64|VLC", false)]
    [InlineData("LocalMachine|Registry64|", false)]
    public void Program_ids_are_validated(string id, bool valid) => Assert.Equal(valid, ProgramInventoryService.ParseId(id) is not null);
}
