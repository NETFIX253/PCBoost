using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;
using PCBoost.TestUtilities;

namespace PCBoost.Presentation.Tests;

public sealed class SettingsViewModelTests
{
    private sealed class FakeAutoStart : IAutoStartRegistration
    {
        public bool Enabled { get; set; }

        public OperationResult Next { get; set; } = OperationResult.Ok();

        public bool IsEnabled() => Enabled;

        public OperationResult SetEnabled(bool enabled)
        {
            if (Next.Success) Enabled = enabled;
            return Next;
        }
    }

    private sealed class FakeTheme : IThemeService
    {
        public ThemePreference Current { get; private set; }

        public void Apply(ThemePreference theme) => Current = theme;
    }

    private sealed class FakeUpdates : IUpdateService
    {
        public UpdateCheckResult Result { get; set; } = new(UpdateCheckStatus.NotConfigured, null, null);

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result);

        public Task<UpdateDownloadResult> DownloadAndVerifyAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateDownloadResult(OperationResult.Ok(), "/tmp/pkg.msi"));

        public OperationResult LaunchInstaller(string verifiedPackagePath) => OperationResult.Ok();
    }

    private sealed class FakeGames : IGameDetectionService
    {
        public bool IsWatching => false;

        public event EventHandler<DetectedGameProcess>? GameStarted
        {
            add { }
            remove { }
        }

        public event EventHandler<DetectedGameProcess>? GameExited
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<GameInfo>> GetInstalledGamesAsync(bool refresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInfo>>([new GameInfo { Id = "steam:1", Name = "Hades", Source = GameSource.Steam }]);

        public Task<DetectedGameProcess?> DetectRunningGameAsync(CancellationToken cancellationToken = default) => Task.FromResult<DetectedGameProcess?>(null);

        public void StartWatching()
        {
        }

        public void StopWatching()
        {
        }

        public void Dispose()
        {
        }
    }

    private readonly TestUi _ui = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeAutoStart _autoStart = new();
    private readonly FakeTheme _theme = new();
    private readonly FakeUpdates _updates = new();
    private readonly FakeShellService _shell = new();

    private SettingsViewModel Create() => new(_ui.Context, _settings, _autoStart, _theme, _updates, _shell, new FakeAppInfo(), new FakeGames());

    [Fact]
    public async Task Changes_AreSavedImmediately()
    {
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        vm.ExpertMode = true;
        vm.GamingMeasureFrameRate = true;
        vm.SelectedAutoGamingIndex = (int)AutoGamingBehavior.Automatic;
        vm.SelectedRetentionIndex = 3;
        vm.SelectedThemeIndex = (int)ThemePreference.Dark;
        vm.SelectedPreferredGameIndex = 1;
        await Task.Yield();

        Assert.True(_settings.Current.ExpertMode);
        Assert.True(_settings.Current.Gaming.MeasureFrameRate);
        Assert.Equal(AutoGamingBehavior.Automatic, _settings.Current.Gaming.AutoActivation);
        Assert.Equal(365, _settings.Current.HistoryRetentionDays);
        Assert.Equal(ThemePreference.Dark, _settings.Current.Theme);
        Assert.Equal(ThemePreference.Dark, _theme.Current);
        Assert.Equal("steam:1", _settings.Current.Gaming.PreferredGameId);
    }

    [Fact]
    public async Task Language_AppliesToLocalizer_AndSaves()
    {
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Equal(["system", "fr", "en"], vm.LanguageOptions.Select(o => o.Key).ToArray());
        vm.SelectedLanguageIndex = 2;
        await Task.Yield();
        Assert.Equal("en", _settings.Current.Language);
        Assert.Equal("en", _ui.Localizer.Culture.TwoLetterISOLanguageName);
    }

    [Fact]
    public async Task LaunchAtStartup_Failure_RevertsToggle()
    {
        _autoStart.Next = OperationResult.Fail(OperationErrorKind.AccessDenied);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        vm.LaunchAtStartup = true;
        Assert.False(vm.LaunchAtStartup);
        Assert.False(_settings.Current.LaunchAtStartup);
        Assert.Equal("Accès refusé.", vm.ErrorText);
    }

    [Fact]
    public async Task Updates_NotConfigured_IsShownHonestly()
    {
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.IsUpdateNotConfigured);
        Assert.False(vm.IsUpdateAvailable);
        Assert.Equal("Les mises à jour automatiques ne sont pas configurées pour cette version.", vm.UpdateStatusText);
    }

    [Fact]
    public async Task Updates_Available_DownloadThenInstall()
    {
        _updates.Result = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, new UpdateInfo(new Version(1, 3, 0), "https://example.invalid/p.msi", "abc", "Notes", null), null);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal("La version 1.3.0 est disponible.", vm.UpdateStatusText);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);
        Assert.True(vm.IsUpdateReadyToInstall);
        Assert.True(vm.InstallUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task DismissedRecommendations_CanBeRestored_AndLogFolderOpens()
    {
        _settings.Current.DismissedRecommendations.AddRange(["a", "b"]);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Equal("2 recommandations masquées", vm.DismissedRecommendationsText);
        await vm.RestoreDismissedRecommendationsCommand.ExecuteAsync(null);
        Assert.Empty(_settings.Current.DismissedRecommendations);
        vm.OpenLogFolderCommand.Execute(null);
        Assert.Contains("folder:/tmp/pcboost-logs", _shell.Calls);
    }
}

public sealed class HistoryViewModelTests
{
    private readonly TestUi _ui = new();
    private readonly FakeRollbackManager _rollback = new();
    private readonly FakeActivityJournal _journal = new();

    private OptimizationSession Session(SessionStatus status)
    {
        var id = Guid.NewGuid();
        return new OptimizationSession
        {
            Id = id, Type = SessionType.Cleanup, StartedAt = _ui.Clock.UtcNow.AddHours(-1), Status = status, BytesFreed = 2L * ByteSize.GiB,
            Changes =
            [
                new ChangeRecord { Id = Guid.NewGuid(), SessionId = id, OptimizationId = "o", Kind = "power.scheme", Target = "Plan", Description = TextRef.Literal("Plan"), Reversible = true, Status = ChangeStatus.Applied, RecordedAt = _ui.Clock.UtcNow },
                new ChangeRecord { Id = Guid.NewGuid(), SessionId = id, OptimizationId = "t", Kind = "file.delete", Target = "%TEMP%", Description = TextRef.Literal("Temp"), Reversible = false, Status = ChangeStatus.Irreversible, RecordedAt = _ui.Clock.UtcNow },
            ],
        };
    }

    [Fact]
    public async Task Sessions_ShowDetails_RestoreAfterConfirmation()
    {
        var session = Session(SessionStatus.Completed);
        _rollback.Sessions.Add(session);
        var vm = new HistoryViewModel(_ui.Context, _rollback, new JournalViewModel(_ui.Context, _journal));
        await vm.OnNavigatedToAsync(null);

        var item = Assert.Single(vm.Sessions);
        Assert.Equal("Nettoyage", item.Title);
        Assert.Equal("2 modifications", item.ChangeCountText);
        Assert.Equal("Terminée", item.StatusText);
        Assert.Equal("2\u00a0Go libérés", item.BytesFreedText);
        Assert.True(item.CanRestore);
        Assert.True(item.Changes[0].CanUndo);
        Assert.False(item.Changes[1].CanUndo);
        Assert.Equal("Irréversible", item.Changes[1].StatusText);

        _ui.Dialogs.NextResult = DialogResultKind.Cancel;
        await item.RestoreCommand.ExecuteAsync(null);
        Assert.Empty(_rollback.Restored);

        _ui.Dialogs.NextResult = DialogResultKind.Primary;
        await item.RestoreCommand.ExecuteAsync(null);
        Assert.Equal([session.Id], _rollback.Restored);
        Assert.Equal("3 modifications restaurées. 1 actions irréversibles (fichiers supprimés) restent inchangées.", vm.StatusMessage);
        vm.OnNavigatedFrom();
    }

    [Fact]
    public async Task JournalParameter_SelectsJournalTab_AndLiveEntriesAppear()
    {
        var vm = new HistoryViewModel(_ui.Context, _rollback, new JournalViewModel(_ui.Context, _journal));
        await vm.OnNavigatedToAsync(PCBoost.Presentation.Navigation.PageKeys.Journal);
        Assert.Equal(1, vm.SelectedTabIndex);
        Assert.True(vm.ShowEmpty);
        Assert.True(vm.Journal.ShowEmpty);

        await _journal.LogAsync(PCBoost.Core.Models.Activity.ActivityKind.Cleanup, TextRef.Literal("Nettoyage effectué"));
        var entry = Assert.Single(vm.Journal.Entries);
        Assert.Equal("Nettoyage effectué", entry.Message);
        Assert.Equal("Nettoyage", entry.KindText);

        vm.OnNavigatedFrom();
        await _journal.LogAsync(PCBoost.Core.Models.Activity.ActivityKind.Info, TextRef.Literal("après"));
        Assert.Single(vm.Journal.Entries);
    }
}
