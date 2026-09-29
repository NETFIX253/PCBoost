using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.Startup;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;
using PCBoost.TestUtilities;

namespace PCBoost.Presentation.Tests;

public sealed class StartupViewModelTests
{
    private readonly TestUi _ui = new();
    private readonly FakeStartupService _service = new();

    private static StartupEntry Entry(string id, bool enabled, StartupRecommendation rec = StartupRecommendation.Optional, bool security = false)
        => new()
        {
            Id = id, Name = id, Location = StartupLocation.RegistryRunUser, SourcePath = @"HKCU\Run", ItemName = id,
            IsEnabled = enabled, Recommendation = rec, IsSecuritySoftware = security,
            Impact = StartupImpact.High, Evidence = new StartupImpactEvidence(true, 180L * ByteSize.MiB, TimeSpan.FromSeconds(12), null),
        };

    [Fact]
    public async Task Toggle_Failure_ElevationCancelled_RevertsToRealState()
    {
        _service.Entries.Add(Entry("Discord", true, StartupRecommendation.CanDisable));
        _service.NextResult = OperationResult.Fail(OperationErrorKind.ElevationCancelled);
        var vm = new StartupViewModel(_ui.Context, _service);
        await vm.OnNavigatedToAsync(null);
        var item = Assert.Single(vm.Items);
        Assert.Equal("Mesuré : 180\u00a0Mo de mémoire, 12\u00a0s de processeur", item.EvidenceText);
        Assert.Equal("Registre (utilisateur)", item.LocationText);

        item.IsEnabled = false;
        await Task.Yield();

        Assert.Equal(("Discord", false), Assert.Single(_service.Calls));
        Assert.True(item.IsEnabled);
        Assert.Contains("refusée", vm.ErrorText);
        Assert.False(item.IsToggling);
    }

    [Fact]
    public async Task Toggle_Success_UpdatesStateAndSummary()
    {
        _service.Entries.Add(Entry("Discord", true, StartupRecommendation.CanDisable));
        _service.Entries.Add(Entry("OneDrive", true));
        var vm = new StartupViewModel(_ui.Context, _service);
        await vm.OnNavigatedToAsync(null);
        Assert.Equal("2 programmes sur 2 se lancent au démarrage.", vm.SummaryText);
        Assert.Equal(1, vm.RecommendedCount);

        vm.Items.First(i => i.Name == "Discord").IsEnabled = false;
        await Task.Yield();

        Assert.Equal("1 programme sur 2 se lance au démarrage.", vm.SummaryText);
        Assert.Equal("Discord ne se lancera plus au démarrage.", vm.StatusMessage);
    }

    [Fact]
    public async Task SecuritySoftware_IsNeverDisabled()
    {
        _service.Entries.Add(Entry("Antivirus", true, security: true));
        var vm = new StartupViewModel(_ui.Context, _service);
        await vm.OnNavigatedToAsync(null);
        var item = vm.Items.Single();
        Assert.False(item.CanToggle);
        item.IsEnabled = false;
        await Task.Yield();
        Assert.Empty(_service.Calls);
        Assert.True(item.IsEnabled);
    }

    [Fact]
    public async Task Filters_And_Search()
    {
        _service.Entries.Add(Entry("Discord", true, StartupRecommendation.CanDisable));
        _service.Entries.Add(Entry("Steam", false));
        _service.Entries.Add(Entry("OneDrive", true));
        var vm = new StartupViewModel(_ui.Context, _service);
        await vm.OnNavigatedToAsync(null);
        Assert.Equal(3, vm.Items.Count);

        vm.SelectedFilterIndex = (int)StartupFilter.Enabled;
        Assert.Equal(["Discord", "OneDrive"], vm.Items.Select(i => i.Name).ToArray());

        vm.SelectedFilterIndex = (int)StartupFilter.Recommended;
        Assert.Equal(["Discord"], vm.Items.Select(i => i.Name).ToArray());

        vm.SelectedFilterIndex = (int)StartupFilter.All;
        vm.SearchText = "one";
        Assert.Equal(["OneDrive"], vm.Items.Select(i => i.Name).ToArray());

        vm.SearchText = "zzz";
        Assert.True(vm.ShowEmpty);
        Assert.Equal("Aucun programme ne correspond à ce filtre.", vm.EmptyText);
    }
}

public sealed class ProcessesViewModelTests
{
    private readonly TestUi _ui = new();
    private readonly FakeProcessService _service = new();
    private readonly FakeShellService _shell = new();

    [Fact]
    public async Task CriticalProcess_TerminationIsBlockedAndExplained()
    {
        _service.Processes.Add(FakeProcessService.Create(4, "csrss.exe", 0.1, 5L * ByteSize.MiB, ProtectionLevel.Critical, hasWindow: false));
        var vm = new ProcessesViewModel(_ui.Context, _service, _shell);
        await vm.OnNavigatedToAsync(null);
        vm.SelectedProcess = vm.Items.Single();

        Assert.False(vm.CloseApplicationCommand.CanExecute(null));
        await vm.TerminateCommand.ExecuteAsync(null);

        Assert.Empty(_service.Terminated);
        Assert.Empty(_ui.Dialogs.Confirmations);
        Assert.Equal("Processes_Critical_Title", Assert.Single(_ui.Dialogs.Messages).Title.Key);
        vm.OnNavigatedFrom();
    }

    [Fact]
    public async Task Terminate_RequiresConfirmation()
    {
        _service.Processes.Add(FakeProcessService.Create(100, "app.exe", 12.5, 300L * ByteSize.MiB));
        var vm = new ProcessesViewModel(_ui.Context, _service, _shell);
        await vm.OnNavigatedToAsync(null);
        vm.SelectedProcess = vm.Items.Single();

        _ui.Dialogs.NextResult = DialogResultKind.Cancel;
        await vm.TerminateCommand.ExecuteAsync(null);
        Assert.Empty(_service.Terminated);

        _ui.Dialogs.NextResult = DialogResultKind.Primary;
        await vm.TerminateCommand.ExecuteAsync(null);
        Assert.Equal([100], _service.Terminated);
        Assert.True(_ui.Dialogs.Confirmations.All(c => c.IsDestructive));
        vm.OnNavigatedFrom();
    }

    [Fact]
    public async Task Refresh_UpdatesItemsInPlace_AndSortsByCpu()
    {
        _service.Processes.Add(FakeProcessService.Create(1, "a.exe", 1, 10L * ByteSize.MiB));
        _service.Processes.Add(FakeProcessService.Create(2, "b.exe", 50, 20L * ByteSize.MiB));
        var vm = new ProcessesViewModel(_ui.Context, _service, _shell);
        await vm.OnNavigatedToAsync(null);
        Assert.Equal(["b.exe", "a.exe"], vm.Items.Select(i => i.Name).ToArray());
        var itemA = vm.Items.Single(i => i.ProcessId == 1);
        vm.SelectedProcess = itemA;
        Assert.Equal("Signé par Contoso", itemA.TrustText);

        _service.Processes[0] = FakeProcessService.Create(1, "a.exe", 80, 10L * ByteSize.MiB);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Same(itemA, vm.Items[0]);
        Assert.Same(itemA, vm.SelectedProcess);
        Assert.Equal("80,0\u00a0%", itemA.CpuText);

        vm.SortCommand.Execute(nameof(ProcessSortColumn.Name));
        Assert.Equal(["a.exe", "b.exe"], vm.Items.Select(i => i.Name).ToArray());

        vm.SearchText = "b.";
        Assert.Equal(["b.exe"], vm.Items.Select(i => i.Name).ToArray());

        vm.OpenLocationCommand.Execute(null);
        vm.SearchOnlineCommand.Execute(null);
        vm.OnNavigatedFrom();
    }
}

public sealed class CleanupViewModelTests
{
    private readonly TestUi _ui = new();
    private readonly FakeCleanupService _service = new();

    [Fact]
    public async Task SafeSelectedByDefault_UnavailableExplained_CleanRequiresConfirmation()
    {
        var vm = new CleanupViewModel(_ui.Context, _service);
        await vm.OnNavigatedToAsync(null);

        Assert.True(vm.IsReady);
        var temp = vm.Categories.Single(c => c.Id == "user-temp");
        var bin = vm.Categories.Single(c => c.Id == "recycle-bin");
        var winTemp = vm.Categories.Single(c => c.Id == "windows-temp");
        Assert.True(temp.IsSelected);
        Assert.Equal("Sûr", temp.SafetyText);
        Assert.False(bin.IsSelected);
        Assert.Equal("Prudence", bin.SafetyText);
        Assert.False(winTemp.IsAvailable);
        Assert.False(winTemp.IsSelected);
        Assert.Equal("Nécessite une autorisation", winTemp.UnavailableReason);
        Assert.Equal(1, vm.SelectedCount);
        Assert.Equal("300\u00a0Mo", vm.SelectedTotalText);

        _ui.Dialogs.NextResult = DialogResultKind.Cancel;
        await vm.CleanCommand.ExecuteAsync(null);
        Assert.Empty(_service.Cleaned);
        Assert.True(Assert.Single(_ui.Dialogs.Confirmations).IsDestructive);

        _ui.Dialogs.NextResult = DialogResultKind.Primary;
        await vm.CleanCommand.ExecuteAsync(null);
        Assert.Equal(["user-temp"], _service.Cleaned.Single().ToArray());
        Assert.True(vm.IsDone);
        Assert.Equal("290\u00a0Mo récupérés", vm.ResultTitle);
        Assert.Equal("10 fichiers ignorés car utilisés par une application.", vm.ResultSkippedText);
        Assert.Equal(100, vm.CleanProgressPercent);
    }
    [Fact]
    public async Task SystemCategoryUnreadableWithoutAdmin_ShowsUnknownSize_NotZero()
    {
        _service.ScanResults[2] = new("windows-temp", 0, 0, true, null, true);
        var vm = new CleanupViewModel(_ui.Context, _service);
        await vm.OnNavigatedToAsync(null);

        var winTemp = vm.Categories.Single(c => c.Id == "windows-temp");
        Assert.True(winTemp.IsAvailable);
        Assert.True(winTemp.IsSizeUnknown);
        Assert.Equal("Non mesurable sans droits administrateur", winTemp.SizeText);
        Assert.Equal(string.Empty, winTemp.FileCountText);
    }
}

public sealed class ShellViewModelTests
{
    private readonly TestUi _ui = new();
    private readonly FakeRollbackManager _rollback = new();
    private readonly FakeRecoveryManager _recovery = new();
    private readonly FakeAutoGamingMode _autoGaming = new();
    private readonly FakeGamingService _gaming = new();
    private readonly FakeProfileService _profiles = new();
    private readonly FakeSettingsService _settings = new();

    private ShellViewModel Create() => new(_ui.Context, new FakeAppInfo(), _rollback, _recovery, _autoGaming, _gaming, _profiles, _settings, new FakeShellService());

    [Fact]
    public async Task ProtectionStatus_ReflectsActiveGamingSession()
    {
        var vm = Create();
        await vm.InitializeAsync();
        Assert.Equal("Aucune modification en vigueur", vm.ProtectionDetailText);

        await _gaming.ActivateAsync(null);
        Assert.Equal("Réglages temporaires du mode Gaming en vigueur", vm.ProtectionDetailText);

        await _gaming.DeactivateAsync();
        Assert.Equal("Aucune modification en vigueur", vm.ProtectionDetailText);
    }

    [Fact]
    public async Task Navigation_OrderAndSelection()
    {
        var vm = Create();
        await vm.InitializeAsync();
        Assert.Equal(
            [PageKeys.Home, PageKeys.Analysis, PageKeys.Optimization, PageKeys.Cleanup, PageKeys.Startup, PageKeys.Processes, PageKeys.Gaming, PageKeys.Performance, PageKeys.History],
            vm.NavigationItems.Select(i => i.PageKey).ToArray());
        Assert.Equal("Accueil", vm.NavigationItems[0].Label);
        Assert.Equal("Version 1.2.3", vm.VersionText);
        Assert.Equal("Restauration activée", vm.ProtectionStatusText);
        Assert.Equal("Aucune modification en vigueur", vm.ProtectionDetailText);

        vm.NavigateCommand.Execute(PageKeys.Storage);
        Assert.True(vm.NavigationItems.Single(i => i.PageKey == PageKeys.Analysis).IsSelected);
        Assert.Equal("Stockage", vm.CurrentPageTitle);
        vm.Dispose();
    }

    [Fact]
    public async Task RecoveryBanner_RestoreRecoversEachInterruptedSession()
    {
        var session = new OptimizationSession { Id = Guid.NewGuid(), Type = SessionType.OneClick, StartedAt = _ui.Clock.UtcNow, Status = SessionStatus.Interrupted };
        _recovery.Interrupted.Add(session);
        var vm = Create();
        await vm.InitializeAsync();

        Assert.True(vm.IsRecoveryBannerVisible);
        Assert.Equal("Une optimisation n'a pas été terminée. Restaurer les paramètres précédents ?", vm.RecoveryMessage);

        await vm.RecoverCommand.ExecuteAsync(null);
        Assert.Equal([session.Id], _recovery.Recovered);
        Assert.False(vm.IsRecoveryBannerVisible);
        vm.Dispose();
    }

    [Fact]
    public async Task RecoveryBanner_KeepDismisses()
    {
        var session = new OptimizationSession { Id = Guid.NewGuid(), Type = SessionType.Gaming, StartedAt = _ui.Clock.UtcNow, Status = SessionStatus.Interrupted };
        _recovery.Interrupted.Add(session);
        var vm = Create();
        await vm.InitializeAsync();
        await vm.KeepChangesCommand.ExecuteAsync(null);
        Assert.Equal([session.Id], _recovery.Dismissed);
        Assert.Empty(_recovery.Recovered);
        vm.Dispose();
    }

    [Fact]
    public async Task GameBanner_ActivatesGamingForSuggestedGame()
    {
        var vm = Create();
        await vm.InitializeAsync();
        var game = new DetectedGameProcess(new GameInfo { Id = "g", Name = "Hades", Source = GameSource.Steam }, 42, null, _ui.Clock.UtcNow);
        _autoGaming.Suggest(game);

        Assert.True(vm.IsGameBannerVisible);
        Assert.Equal("Jeu détecté : Hades", vm.GameBannerTitle);

        await vm.ActivateSuggestedGamingCommand.ExecuteAsync(null);
        Assert.Same(game, Assert.Single(_gaming.Activations));
        Assert.True(vm.IsGamingModeActive);
        Assert.Equal("Mode Gaming actif — Hades", vm.GamingStatusText);
        Assert.False(vm.IsGameBannerVisible);
        vm.Dispose();
    }

    [Fact]
    public async Task ProtectionCount_And_ExpertVisibility_FollowServices()
    {
        _rollback.Sessions.Add(new OptimizationSession
        {
            Id = Guid.NewGuid(), Type = SessionType.OneClick, StartedAt = _ui.Clock.UtcNow, Status = SessionStatus.Completed,
            Changes =
            [
                new ChangeRecord { Id = Guid.NewGuid(), SessionId = Guid.Empty, OptimizationId = "o", Kind = "k", Target = "t", Description = TextRef.Literal("d"), Reversible = true, Status = ChangeStatus.Applied, RecordedAt = _ui.Clock.UtcNow },
                new ChangeRecord { Id = Guid.NewGuid(), SessionId = Guid.Empty, OptimizationId = "o", Kind = "k", Target = "t2", Description = TextRef.Literal("d"), Reversible = true, Status = ChangeStatus.Applied, RecordedAt = _ui.Clock.UtcNow },
            ],
        });
        var vm = Create();
        await vm.InitializeAsync();
        Assert.Equal(2, vm.ReversibleChangeCount);
        Assert.Equal("2 modifications réversibles en vigueur", vm.ProtectionDetailText);
        Assert.False(vm.IsExpertModeVisible);

        var s = _settings.Current.Clone();
        s.ExpertMode = true;
        await _settings.SaveAsync(s);
        Assert.True(vm.IsExpertModeVisible);
        vm.Dispose();
    }

    [Fact]
    public async Task LanguageChange_RefreshesLabels_AndRequestsReload()
    {
        var vm = Create();
        await vm.InitializeAsync();
        var reloads = 0;
        vm.PageReloadRequested += (_, _) => reloads++;
        _ui.Localizer.SetLanguage("en");
        Assert.Equal("Home", vm.NavigationItems[0].Label);
        Assert.Equal(1, reloads);
        vm.Dispose();
    }
}
