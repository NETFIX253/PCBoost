using PCBoost.Core.Common;
using PCBoost.Core.Drivers;
using PCBoost.Core.Models.Drivers;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;
using PCBoost.TestUtilities;

namespace PCBoost.Presentation.Tests;

public sealed class DriversViewModelTests
{
    private const string Gpu = "0c3a9a55-5b0f-4f1d-9b3e-2a6b7f0c1d2e";
    private const string Wifi = "6f1e2d3c-4b5a-4968-8776-5a4b3c2d1e0f";
    private const string Bios = "11111111-2222-4333-8444-555555555555";

    private readonly TestUi _ui = new();
    private readonly FakeDrivers _drivers = new();
    private readonly FakeNetworkCostProvider _network = new();
    private readonly FakeRollbackManager _rollback = new();
    private readonly FakeShellService _shell = new();

    private sealed class FakeDrivers : IDriverUpdateService
    {
        public DriverScanResult? Latest { get; set; }
        public DriverScanResult Next { get; set; } = Scan();
        public int ScanCount { get; private set; }
        public List<(IReadOnlyList<DriverUpdateCandidate> Updates, bool Protection)> Installs { get; } = [];
        public Func<IReadOnlyList<DriverUpdateCandidate>, DriverInstallResult>? InstallResult { get; set; }
        public List<DriverInstallProgress> ProgressToReport { get; } = [];
        public List<Guid> RolledBack { get; } = [];
        public OperationResult RollbackResult { get; set; } = OperationResult.Ok(TextRef.Of("Drv_Rollback_Done"));

        public Task<DriverScanResult> ScanAsync(CancellationToken cancellationToken = default)
        {
            ScanCount++;
            Latest = Next;
            return Task.FromResult(Next);
        }

        public Task<DriverInstallResult> InstallAsync(IReadOnlyList<DriverUpdateCandidate> updates, bool enableSystemProtection,
            IProgress<DriverInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Installs.Add((updates, enableSystemProtection));
            foreach (var p in ProgressToReport) progress?.Report(p);
            return Task.FromResult(InstallResult?.Invoke(updates) ?? DriverInstallResult.NotStarted(OperationResult.Fail(OperationErrorKind.Failed)));
        }

        public Task<OperationResult> RollbackAsync(Guid changeId, CancellationToken cancellationToken = default)
        {
            RolledBack.Add(changeId);
            return Task.FromResult(RollbackResult);
        }

        public DriverRestorePointResult RestorePointResult { get; set; } = new(OperationResult.Ok(), RestorePointStatus.Created, Now, false);
        public List<bool> RestorePoints { get; } = [];

        public Task<DriverRestorePointResult> CreateRestorePointAsync(bool enableSystemProtection, CancellationToken cancellationToken = default)
        {
            RestorePoints.Add(enableSystemProtection);
            return Task.FromResult(RestorePointResult);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static DriverScanResult Scan(DriverScanState state = DriverScanState.Ready, SystemProtectionState protection = SystemProtectionState.Enabled,
        bool reboot = false, bool managed = false, OperationResult? error = null, bool withUpdates = true)
    {
        var devices = new[]
        {
            DriverSamples.Device(),
            DriverSamples.Device(instanceId: @"PCI\VEN_8086&DEV_24FD\4&2", hardwareId: @"PCI\VEN_8086&DEV_24FD", name: "Wi-Fi", deviceClass: "Net",
                version: "20.70.0.5", provider: "Intel", inf: "oem33.inf"),
        };
        var offers = withUpdates
            ? new[]
            {
                DriverSamples.Offer(updateId: Gpu, size: 300_000_000),
                DriverSamples.Offer(updateId: Wifi, hardwareId: @"PCI\VEN_8086&DEV_24FD", driverClass: "Net", version: "22.200.0.6", provider: "Intel",
                    optional: true, size: 20_000_000, reboot: DriverRebootBehavior.Always),
                DriverSamples.Offer(updateId: Bios, driverClass: "Firmware", title: "Intel - Firmware - 2.0.0.1", userInput: true),
            }
            : [];
        var candidates = offers.Select(o => DriverUpdatePolicy.Evaluate(o, devices, Now)).OrderBy(c => c.Tier).ToList();
        return new DriverScanResult(state, state == DriverScanState.Ready ? candidates : [], Now, reboot, false, managed, protection, error);
    }

    private DriversViewModel Create() => new(_ui.Context, _drivers, _network, _rollback, _shell);

    private async Task<DriversViewModel> LoadAsync()
    {
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        return vm;
    }

    [Fact]
    public async Task Opening_the_page_scans_once_then_reuses_the_latest_result()
    {
        var vm = await LoadAsync();
        Assert.Equal(1, _drivers.ScanCount);

        var again = Create();
        await again.OnNavigatedToAsync(null);
        Assert.Equal(1, _drivers.ScanCount);
        Assert.Single(again.Recommended);
        Assert.True(vm.HasScanned);
    }

    [Fact]
    public async Task Updates_are_grouped_and_only_recommended_ones_are_preselected()
    {
        var vm = await LoadAsync();

        Assert.Equal(["Intel(R) UHD Graphics 620"], vm.Recommended.Select(i => i.DeviceName));
        Assert.Equal(["Wi-Fi"], vm.Review.Select(i => i.DeviceName));
        Assert.Single(vm.Excluded);
        Assert.True(vm.Recommended[0].IsSelected);
        Assert.False(vm.Review[0].IsSelected);
        Assert.False(vm.Excluded[0].IsSelectable);
        Assert.Equal("Recommandées (1)", vm.RecommendedHeader);
        Assert.Equal("À examiner (1)", vm.ReviewHeader);
        Assert.Equal("Non proposées (1)", vm.ExcludedHeader);
        Assert.StartsWith("1 mise à jour sélectionnée · ", vm.SelectionText);
        Assert.True(vm.ShowList);
        Assert.False(vm.ShowUpToDate);
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Item_texts_describe_installed_and_offered_drivers_and_reasons()
    {
        var vm = await LoadAsync();
        var gpu = vm.Recommended[0];
        var wifi = vm.Review[0];
        var bios = vm.Excluded[0];

        Assert.Equal("Carte graphique", gpu.ClassText);
        Assert.Equal("Installé : 27.20.100.8681 du 12/03/2021 (Intel Corporation)", gpu.CurrentText);
        Assert.Equal("Proposé : 31.0.101.2125 du 15/05/2023 (Intel Corporation)", gpu.ProposedText);
        Assert.StartsWith("Windows Update (recommandée) · publiée le ", gpu.DetailsText);
        Assert.False(gpu.HasReasons);
        Assert.Contains("redémarrage nécessaire", wifi.DetailsText);
        Assert.Equal(["Mise à jour facultative de Windows : utile surtout pour corriger un problème précis."], wifi.Reasons);
        Assert.Equal(["Microprogramme (BIOS, UEFI ou firmware) : PCBoost ne le modifie jamais."], bios.Reasons);
        Assert.False(bios.CanOpenWindowsUpdate);
    }

    [Fact]
    public async Task Excluded_items_cannot_be_selected()
    {
        var vm = await LoadAsync();
        vm.Excluded[0].IsSelected = true;

        Assert.False(vm.Excluded[0].IsSelected);
        Assert.Equal(1, vm.SelectedCount);
    }

    [Fact]
    public async Task Selection_commands_update_the_summary()
    {
        var vm = await LoadAsync();

        vm.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.Equal("Aucune mise à jour sélectionnée", vm.SelectionText);
        Assert.False(vm.InstallCommand.CanExecute(null));

        vm.Review[0].IsSelected = true;
        vm.Recommended[0].IsSelected = true;
        Assert.Equal(2, vm.SelectedCount);
        Assert.StartsWith("2 mises à jour sélectionnées · ", vm.SelectionText);

        vm.SelectRecommendedCommand.Execute(null);
        Assert.Equal(1, vm.SelectedCount);
        Assert.False(vm.Review[0].IsSelected);
    }

    [Fact]
    public async Task Up_to_date_state_is_shown_when_nothing_is_installable()
    {
        _drivers.Next = Scan(withUpdates: false);

        var vm = await LoadAsync();

        Assert.True(vm.ShowUpToDate);
        Assert.False(vm.ShowList);
    }

    [Theory]
    [InlineData(DriverScanState.ExcludedByPolicy, NoticeSeverity.Informational, "Pilotes gérés par votre organisation")]
    [InlineData(DriverScanState.UpdateServiceDisabled, NoticeSeverity.Warning, "Service Windows Update désactivé")]
    [InlineData(DriverScanState.SearchFailed, NoticeSeverity.Error, "Recherche impossible")]
    public async Task Windows_update_states_are_explained(DriverScanState state, NoticeSeverity severity, string title)
    {
        _drivers.Next = Scan(state, error: OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drivers_Notice_Busy")));

        var vm = await LoadAsync();

        var notice = Assert.Single(vm.Notices);
        Assert.Equal(severity, notice.Severity);
        Assert.Equal(title, notice.Title);
        Assert.False(vm.ShowUpToDate);
        Assert.False(vm.ShowList);
    }

    [Fact]
    public async Task Pending_reboot_blocks_installation_with_an_explanation()
    {
        _drivers.Next = Scan(reboot: true, managed: true);

        var vm = await LoadAsync();

        Assert.Equal(["Redémarrage en attente", "Mises à jour gérées par votre organisation"], vm.Notices.Select(n => n.Title));
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Equal("Redémarrez le PC avant d'installer des pilotes.", vm.InstallBlockedText);
    }

    [Fact]
    public async Task Disabled_protection_requires_explicit_consent_before_installing()
    {
        _drivers.Next = Scan(protection: SystemProtectionState.Disabled);

        var vm = await LoadAsync();

        Assert.True(vm.ShowProtectionChoice);
        Assert.False(vm.EnableProtection);
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.True(vm.HasInstallBlockedText);

        vm.EnableProtection = true;

        Assert.True(vm.InstallCommand.CanExecute(null));
        Assert.False(vm.HasInstallBlockedText);
    }

    [Fact]
    public async Task Protection_disabled_by_policy_blocks_installation()
    {
        _drivers.Next = Scan(protection: SystemProtectionState.DisabledByPolicy);

        var vm = await LoadAsync();

        Assert.False(vm.ShowProtectionChoice);
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Contains(vm.Notices, n => n.Severity == NoticeSeverity.Error);
    }

    [Fact]
    public async Task Metered_connection_is_announced_with_the_download_size()
    {
        _network.Metered = true;

        var vm = await LoadAsync();

        Assert.True(vm.IsMetered);
        Assert.StartsWith("Le téléchargement (", vm.MeteredText);
    }

    [Fact]
    public async Task Preview_lists_changes_and_safeguards_and_cancel_changes_nothing()
    {
        var vm = await LoadAsync();
        vm.Review[0].IsSelected = true;
        _ui.Dialogs.NextResult = DialogResultKind.Cancel;

        await vm.InstallCommand.ExecuteAsync(null);

        var confirmation = Assert.Single(_ui.Dialogs.Confirmations);
        Assert.Equal("Drivers_Confirm_Title", confirmation.Title.Key);
        Assert.Equal(2, confirmation.Title.Args[0]);
        var details = confirmation.Details!.Select(d => d.Key).ToList();
        Assert.Equal(["Drivers_Confirm_Item", "Drivers_Confirm_Item", "Drivers_Confirm_RestorePoint", "Drivers_Confirm_Admin", "Drivers_Confirm_Restart", "Drivers_Confirm_Rollback"], details);
        Assert.Equal(["Intel(R) UHD Graphics 620", "27.20.100.8681", "31.0.101.2125"], confirmation.Details![0].Args);
        Assert.Empty(_drivers.Installs);
    }

    [Fact]
    public async Task Installation_shows_progress_and_per_driver_results_with_rollback()
    {
        var sessionId = Guid.NewGuid();
        var changeId = Guid.NewGuid();
        var vm = await LoadAsync();
        var gpuCandidate = vm.Recommended[0].Candidate;
        _rollback.Sessions.Add(new OptimizationSession
        {
            Id = sessionId,
            Type = SessionType.DriverUpdate,
            StartedAt = Now,
            Status = SessionStatus.Completed,
            Changes =
            [
                new ChangeRecord
                {
                    Id = changeId, SessionId = sessionId, OptimizationId = "drivers", Kind = ChangeKinds.DriverUpdate, Target = "driver:GPU",
                    Description = TextRef.Of("Drv_Change_Description", "GPU", "1", "2"), Reversible = true, Status = ChangeStatus.Applied, RecordedAt = Now,
                    BeforeState = ChangeStateSerializer.Serialize(new DriverUpdateState(Gpu, "t", "GPU", "Display", "31.0.101.2125",
                        [new DriverDeviceState(gpuCandidate.Device!.InstanceId, "27.20.100.8681", null, null, "oem12.inf")])),
                },
            ],
        });
        var stepsSeen = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DriversViewModel.InstallStepText)) stepsSeen.Add(vm.InstallStepText);
        };
        _drivers.ProgressToReport.AddRange([new DriverInstallProgress(DriverInstallStage.RestorePoint, 0, 1), new DriverInstallProgress(DriverInstallStage.Installing, 1, 1)]);
        _drivers.InstallResult = _ => new DriverInstallResult(OperationResult.Ok(), DriverInstallStop.None, RestorePointStatus.Created, Now, false,
            [new DriverInstallOutcome(Gpu, DriverInstallStatus.Installed, 0, true, "31.0.101.2125", 0)], true, sessionId);

        await vm.InstallCommand.ExecuteAsync(null);

        var install = Assert.Single(_drivers.Installs);
        Assert.Equal([Gpu], install.Updates.Select(u => u.Offer.UpdateId));
        Assert.False(install.Protection);
        Assert.Contains("Création du point de restauration Windows…", stepsSeen);
        Assert.Contains("Installation 1 sur 1 : Intel(R) UHD Graphics 620", stepsSeen);
        Assert.False(vm.IsInstalling);
        Assert.True(vm.HasResult);
        Assert.Equal(NoticeSeverity.Success, vm.ResultSeverity);
        Assert.Equal("1 pilote installé sur 1", vm.ResultTitle);
        Assert.StartsWith("Point de restauration créé le ", vm.RestorePointText);
        Assert.True(vm.ShowRestartNotice);
        var result = Assert.Single(vm.Results);
        Assert.Equal("Installé · actif après redémarrage", result.OutcomeText);
        Assert.Equal("27.20.100.8681 → 31.0.101.2125", result.VersionsText);
        Assert.True(result.CanRollback);
        Assert.Equal(2, _drivers.ScanCount); // liste rafraîchie après l'installation

        await result.RollbackCommand.ExecuteAsync(null);

        Assert.Equal([changeId], _drivers.RolledBack);
        Assert.True(result.IsRolledBack);
        Assert.False(result.CanRollback);
        Assert.Equal("[Drv_Rollback_Done]", result.OutcomeText);
    }

    private void AddSession(Guid sessionId, Guid changeId, string instanceId, bool reversible, ChangeStatus status)
        => _rollback.Sessions.Add(new OptimizationSession
        {
            Id = sessionId,
            Type = SessionType.DriverUpdate,
            StartedAt = Now,
            Status = SessionStatus.Completed,
            Changes =
            [
                new ChangeRecord
                {
                    Id = changeId, SessionId = sessionId, OptimizationId = "drivers", Kind = ChangeKinds.DriverUpdate, Target = "driver:GPU",
                    Description = TextRef.Of("Drv_Change_Description", "GPU", "1", "2"), Reversible = reversible, Status = status, RecordedAt = Now,
                    BeforeState = ChangeStateSerializer.Serialize(new DriverUpdateState(Gpu, "t", "GPU", "Display", "31.0.101.2125",
                        [new DriverDeviceState(instanceId, "27.20.100.8681", null, null, null)])),
                },
            ],
        });

    [Fact]
    public async Task Unknown_result_keeps_the_rollback_available()
    {
        var sessionId = Guid.NewGuid();
        var changeId = Guid.NewGuid();
        var vm = await LoadAsync();
        AddSession(sessionId, changeId, vm.Recommended[0].Candidate.Device!.InstanceId, reversible: true, ChangeStatus.Pending);
        _drivers.InstallResult = _ => new DriverInstallResult(OperationResult.Fail(OperationErrorKind.Timeout, TextRef.Of("Drv_Install_Unknown")),
            DriverInstallStop.None, RestorePointStatus.Failed, null, false, [new DriverInstallOutcome(Gpu, DriverInstallStatus.Unknown, 0, false, null, null)], false, sessionId);

        await vm.InstallCommand.ExecuteAsync(null);

        var result = Assert.Single(vm.Results);
        Assert.Equal("[Drv_Outcome_Unknown]", result.OutcomeText);
        Assert.True(result.CanRollback);
        Assert.Equal(changeId, result.ChangeId);
    }

    [Fact]
    public async Task Installed_driver_without_targeted_rollback_points_to_the_restore_point()
    {
        var sessionId = Guid.NewGuid();
        var vm = await LoadAsync();
        AddSession(sessionId, Guid.NewGuid(), vm.Recommended[0].Candidate.Device!.InstanceId, reversible: false, ChangeStatus.Applied);
        _drivers.InstallResult = _ => new DriverInstallResult(OperationResult.Ok(), DriverInstallStop.None, RestorePointStatus.Created, Now, false,
            [new DriverInstallOutcome(Gpu, DriverInstallStatus.Installed, 0, false, "31.0.101.2125", null)], false, sessionId);

        await vm.InstallCommand.ExecuteAsync(null);

        var result = Assert.Single(vm.Results);
        Assert.Equal("Installé · retour avec le point de restauration", result.OutcomeText);
        Assert.False(result.CanRollback);
    }

    [Fact]
    public async Task Protection_is_enabled_only_when_the_choice_is_shown_and_checked()
    {
        _drivers.Next = Scan(protection: SystemProtectionState.Disabled);
        var vm = await LoadAsync();
        vm.EnableProtection = true;
        // Nouvelle recherche : la protection a été réactivée entre-temps, le choix n'est plus proposé.
        _drivers.Next = Scan(protection: SystemProtectionState.Enabled);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.False(vm.ShowProtectionChoice);
        Assert.True(vm.EnableProtection);

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.False(Assert.Single(_drivers.Installs).Protection);
    }

    [Fact]
    public async Task Selecting_two_updates_for_the_same_device_blocks_installation()
    {
        var vm = await LoadAsync();
        var gpu = vm.Recommended[0].Candidate;
        var extension = Core.Drivers.DriverUpdatePolicy.Evaluate(DriverSamples.Offer(updateId: Wifi, driverClass: "Extension", version: "1.0.0.7"),
            [gpu.Device!], Now);
        vm.Apply(Scan() with { Candidates = [gpu, extension] });
        foreach (var item in vm.Review) item.IsSelected = true;

        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Equal("Deux mises à jour sélectionnées concernent le même périphérique : installez-les l'une après l'autre.", vm.InstallBlockedText);

        foreach (var item in vm.Review) item.IsSelected = false;
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Unreadable_devices_are_announced_and_block_installation()
    {
        _drivers.Next = Scan(error: OperationResult.Fail(OperationErrorKind.Failed));

        var vm = await LoadAsync();

        Assert.Contains(vm.Notices, n => n.Title == "Périphériques non lus");
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Equal("Relancez la recherche : la liste des périphériques n'a pas pu être lue.", vm.InstallBlockedText);
    }

    [Fact]
    public async Task Protection_choice_is_offered_for_the_on_demand_restore_point_even_without_updates()
    {
        _drivers.Next = Scan(protection: SystemProtectionState.Disabled, withUpdates: false);

        var vm = await LoadAsync();
        vm.EnableProtection = true;
        await vm.CreateRestorePointCommand.ExecuteAsync(null);

        Assert.True(vm.ShowProtectionChoice);
        Assert.Equal([true], _drivers.RestorePoints);
    }

    [Fact]
    public async Task Failed_installation_reports_the_reason_without_rollback()
    {
        var vm = await LoadAsync();
        _drivers.InstallResult = updates => new DriverInstallResult(
            OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drivers_Blocked_Reboot")), DriverInstallStop.RebootPending,
            RestorePointStatus.Failed, null, false, updates.Select(u => new DriverInstallOutcome(u.Offer.UpdateId, DriverInstallStatus.NotRun, 0, false, null, null)).ToList(),
            false, Guid.NewGuid());

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(NoticeSeverity.Error, vm.ResultSeverity);
        Assert.Equal("Aucun pilote installé", vm.ResultTitle);
        Assert.Equal("Redémarrez le PC avant d'installer des pilotes.", vm.ResultMessage);
        Assert.Empty(vm.RestorePointText);
        Assert.False(Assert.Single(vm.Results).CanRollback);
    }

    [Theory]
    [InlineData(DriverInstallStage.Searching, 0, 4, 5)]
    [InlineData(DriverInstallStage.RestorePoint, 0, 4, 10)]
    [InlineData(DriverInstallStage.Downloading, 1, 4, 12.25)]
    [InlineData(DriverInstallStage.Installing, 4, 4, 86.5)]
    [InlineData(DriverInstallStage.Verifying, 4, 4, 97.75)]
    public void Progress_percent_follows_the_stages(DriverInstallStage stage, int index, int total, double expected)
        => Assert.Equal(expected, DriversViewModel.ProgressPercent(new DriverInstallProgress(stage, index, total)), 3);

    [Fact]
    public async Task Links_open_history_and_system_restore()
    {
        var vm = await LoadAsync();

        vm.OpenHistoryCommand.Execute(null);
        vm.OpenSystemRestoreCommand.Execute(null);

        Assert.Equal(PageKeys.History, _ui.Navigation.Navigations.Last().Key);
        Assert.Equal("rstrui", _shell.Calls.Last());
    }

    [Fact]
    public void Every_known_device_class_has_a_label()
    {
        var localizer = new TestLocalizer();
        foreach (var suffix in DriverClassLabels.Known.Values.Append("Unknown"))
            Assert.DoesNotContain("[", localizer.Get("Drivers_Class_" + suffix), StringComparison.Ordinal);
        Assert.Equal("Périphérique", DriverClassLabels.Describe(null, localizer));
        Assert.Equal("Infrared", DriverClassLabels.Describe("Infrared", localizer));
    }

    [Fact]
    public void Every_reason_tier_and_step_has_a_text()
    {
        var localizer = new TestLocalizer();
        var keys = Enum.GetNames<DriverUpdateReason>().Select(r => "Drivers_Reason_" + r)
            .Concat(Enum.GetNames<DriverUpdateTier>().Select(t => "Drivers_Tier_" + t))
            .Concat(new[] { DriverInstallStage.Downloading, DriverInstallStage.Installing, DriverInstallStage.Verifying }.Select(s => "Drivers_Step_" + s));
        foreach (var key in keys)
        {
            Assert.DoesNotContain("[", localizer.Get(key), StringComparison.Ordinal);
            Assert.DoesNotContain("[", new TestLocalizer("en-US").Get(key), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Refusal_for_disabled_protection_offers_the_consent_even_when_the_state_was_unknown()
    {
        _drivers.Next = Scan(protection: SystemProtectionState.Unknown);
        var vm = await LoadAsync();
        Assert.False(vm.ShowProtectionChoice);
        _drivers.InstallResult = updates => new DriverInstallResult(
            OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drv_Stop_ProtectionDisabled")), DriverInstallStop.ProtectionDisabled,
            RestorePointStatus.Disabled, null, false, updates.Select(u => new DriverInstallOutcome(u.Offer.UpdateId, DriverInstallStatus.NotRun, 0, false, null, null)).ToList(),
            false, Guid.NewGuid());

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.True(vm.ShowProtectionChoice);
        Assert.False(vm.InstallCommand.CanExecute(null));
        vm.EnableProtection = true;
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Official_sources_are_listed_and_open_their_https_site()
    {
        var computer = new ComputerIdentity("HP", "Victus 15", "HP");
        _drivers.Next = Scan() with
        {
            Computer = computer,
            OfficialSources = OfficialDriverSources.For(computer, [DriverSamples.Device(), DriverSamples.Device(instanceId: @"PCI\X\2", name: "NVIDIA GeForce RTX 4050", provider: "NVIDIA")]),
        };

        var vm = await LoadAsync();

        Assert.True(vm.ShowSources);
        Assert.True(vm.HasSources);
        Assert.Equal("Votre PC : HP · Victus 15", vm.ComputerText);
        Assert.Equal(["HP — HP Support Assistant", "Intel — Intel Driver & Support Assistant", "NVIDIA — NVIDIA App"], vm.Sources.Select(s => s.Title));
        Assert.Equal("Pilotes et logiciels validés par le fabricant pour votre modèle (Victus 15).", vm.Sources[0].Description);
        Assert.Equal("support.hp.com", vm.Sources[0].HostText);
        vm.Sources[2].OpenCommand.Execute(null);
        Assert.Equal("uri:https://www.nvidia.com/en-us/drivers/", Assert.Single(_shell.Calls));
    }

    [Fact]
    public async Task Sources_section_is_hidden_when_drivers_are_managed_by_the_organization()
    {
        _drivers.Next = Scan(DriverScanState.ExcludedByPolicy);

        var vm = await LoadAsync();

        Assert.False(vm.ShowSources);
    }

    [Fact]
    public async Task Restore_point_can_be_created_on_demand()
    {
        var vm = await LoadAsync();

        await vm.CreateRestorePointCommand.ExecuteAsync(null);

        Assert.Equal([false], _drivers.RestorePoints);
        Assert.StartsWith("Point de restauration créé le ", vm.RestorePointNowText);
        Assert.False(vm.IsCreatingRestorePoint);
    }

    [Fact]
    public async Task Restore_point_refused_for_disabled_protection_offers_the_consent()
    {
        _drivers.Next = Scan(protection: SystemProtectionState.Unknown);
        _drivers.RestorePointResult = new DriverRestorePointResult(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drivers_Blocked_Protection")),
            RestorePointStatus.Disabled, null, false);
        var vm = await LoadAsync();

        await vm.CreateRestorePointCommand.ExecuteAsync(null);

        Assert.True(vm.ShowProtectionChoice);
        Assert.Equal("Cochez l'activation de la protection du système pour pouvoir installer.", vm.ErrorText);
        Assert.Empty(vm.RestorePointNowText);
    }

    [Fact]
    public async Task Restore_point_is_unavailable_when_the_organization_disabled_system_restore()
    {
        _drivers.Next = Scan(protection: SystemProtectionState.DisabledByPolicy);

        var vm = await LoadAsync();

        Assert.False(vm.CreateRestorePointCommand.CanExecute(null));
    }

    [Fact]
    public void Update_needing_user_input_points_to_windows_update()
    {
        var candidate = DriverUpdatePolicy.Evaluate(DriverSamples.Offer(userInput: true), [DriverSamples.Device()], Now);
        var item = new DriverUpdateItemViewModel(candidate, _ui.Localizer, _ui.Formatter, () => { }, () => _shell.OpenWindowsSettings("windowsupdate-optionalupdates"));

        Assert.True(item.CanOpenWindowsUpdate);
        item.OpenWindowsUpdateCommand.Execute(null);
        Assert.Equal("settings:windowsupdate-optionalupdates", Assert.Single(_shell.Calls));
    }
}

