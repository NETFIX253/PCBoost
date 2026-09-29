using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;
using PCBoost.TestUtilities;

namespace PCBoost.Presentation.Tests;

public sealed class GameProfileViewModelTests
{
    private readonly TestUi _ui = new();
    private readonly FakeSettingsService _settings = new();
    private readonly InMemoryGamingSessionRepository _sessions = new();

    private sealed class Games : IGameDetectionService
    {
        public bool IsWatching => false;
        public event EventHandler<DetectedGameProcess>? GameStarted { add { } remove { } }
        public event EventHandler<DetectedGameProcess>? GameExited { add { } remove { } }
        public Task<IReadOnlyList<GameInfo>> GetInstalledGamesAsync(bool refresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInfo>>(
            [
                new GameInfo { Id = "steam:730", Name = "Counter-Strike 2", Source = GameSource.Steam },
                new GameInfo { Id = "epic:fn", Name = "Alpha Game", Source = GameSource.EpicGames },
            ]);
        public Task<DetectedGameProcess?> DetectRunningGameAsync(CancellationToken cancellationToken = default) => Task.FromResult<DetectedGameProcess?>(null);
        public void StartWatching() { }
        public void StopWatching() { }
        public void Dispose() { }
    }

    private GameProfileViewModel Create() => new(_ui.Context, _settings, new Games(), _sessions);

    private DateTimeOffset Now => _ui.Clock.UtcNow;

    [Fact]
    public async Task Opens_requested_game_with_general_settings_and_history()
    {
        _settings.Current.Gaming.SwitchPowerPlan = true;
        await _sessions.SaveAsync(new GamingSession
        {
            Id = Guid.NewGuid(), StartedAt = Now.AddDays(-1), EndedAt = Now.AddDays(-1).AddMinutes(45), Status = GamingSessionStatus.Restored, GameId = "steam:730",
            FrameStats = new FrameStats(144, 98, 70, 6.9, 12, 380_000, TimeSpan.FromMinutes(44)),
        });
        await _sessions.SaveAsync(new GamingSession { Id = Guid.NewGuid(), StartedAt = Now.AddDays(-2), EndedAt = Now.AddDays(-2).AddMinutes(20), Status = GamingSessionStatus.Restored, GameId = "steam:730" });
        await _sessions.SaveAsync(new GamingSession { Id = Guid.NewGuid(), StartedAt = Now.AddDays(-3), Status = GamingSessionStatus.Restored, GameId = "epic:fn" });
        var vm = Create();

        await vm.OnNavigatedToAsync("steam:730");

        Assert.Equal(["Alpha Game", "Counter-Strike 2"], vm.Games.Select(g => g.Label));
        Assert.Equal("Counter-Strike 2", vm.GameName);
        Assert.Equal(4, vm.Settings.Count);
        Assert.All(vm.Settings, s => Assert.Equal(0, s.SelectedIndex));
        Assert.Equal("Réglage général (activé)", vm.Settings[0].Options[0].Label);
        Assert.False(vm.HasCustomSettings);
        Assert.Equal(2, vm.History.Count);
        Assert.Equal("144 FPS", vm.History[0].FpsText);
        Assert.Equal("Non mesuré", vm.History[1].FpsText);
        Assert.False(vm.History[1].IsMeasured);
        Assert.Equal("Moyenne des 1 session mesurée : 144 FPS (1 % low : 98 FPS).", vm.HistorySummary);
    }

    [Fact]
    public async Task Changing_a_setting_saves_a_profile_for_this_game_only()
    {
        var vm = Create();
        await vm.OnNavigatedToAsync("steam:730");

        vm.Settings.Single(s => s.Key == nameof(GameProfile.SwitchPowerPlan)).SelectedIndex = 2;
        await Task.Delay(50);

        var profile = _settings.Current.Gaming.GameProfiles["steam:730"];
        Assert.False(profile.SwitchPowerPlan);
        Assert.Null(profile.RaiseGamePriority);
        Assert.True(vm.HasCustomSettings);
        Assert.False(_settings.Current.Gaming.GameProfiles.ContainsKey("epic:fn"));

        vm.ResetToGeneralCommand.Execute(null);
        await Task.Delay(50);
        Assert.False(_settings.Current.Gaming.GameProfiles.ContainsKey("steam:730"));
        Assert.False(vm.HasCustomSettings);
    }

    [Fact]
    public async Task Existing_profile_is_shown_and_switching_game_reloads()
    {
        _settings.Current.Gaming.GameProfiles["epic:fn"] = new GameProfile { MeasureFrameRate = true };
        var vm = Create();
        await vm.OnNavigatedToAsync("epic:fn");
        Assert.Equal(1, vm.Settings.Single(s => s.Key == nameof(GameProfile.MeasureFrameRate)).SelectedIndex);
        Assert.True(vm.HasCustomSettings);
        Assert.Equal("Aucune session de mode Gaming enregistrée pour ce jeu.", vm.HistorySummary);

        vm.SelectedGameIndex = 1;
        await Task.Delay(50);
        Assert.Equal("Counter-Strike 2", vm.GameName);
        Assert.False(vm.HasCustomSettings);
    }

    [Fact]
    public async Task Without_parameter_the_preferred_or_first_game_is_opened()
    {
        _settings.Current.Gaming.PreferredGameId = "steam:730";
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Equal("Counter-Strike 2", vm.GameName);
    }
}
