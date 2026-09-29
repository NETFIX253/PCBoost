using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Settings;
using PCBoost.Gaming.Services;
using PCBoost.Gaming.Tests.Fakes;
using PCBoost.TestUtilities;

namespace PCBoost.Gaming.Tests;

public sealed class AutoGamingModeTests
{
    private readonly FakeGameDetectionService _detection = new();
    private readonly FakeGamingService _gaming = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeNotificationService _notifications = new();
    private readonly DetectedGameProcess _game = new(
        new GameInfo { Id = "sig:eldenring.exe", Name = "ELDEN RING", Source = GameSource.Signature }, 4242, @"D:\Jeux\eldenring.exe", DateTimeOffset.UtcNow);

    private AutoGamingMode Create(AutoGamingBehavior behavior)
    {
        _settings.Current.Gaming.AutoActivation = behavior;
        return new AutoGamingMode(_detection, _gaming, _settings, _notifications);
    }

    [Fact]
    public void Off_does_nothing_and_does_not_watch()
    {
        using var auto = Create(AutoGamingBehavior.Off);
        var suggested = 0;
        auto.ActivationSuggested += (_, _) => suggested++;

        auto.Start();
        _detection.RaiseStarted(_game);

        Assert.False(_detection.IsWatching);
        Assert.Equal(0, suggested);
        Assert.Empty(_gaming.Activations);
        Assert.Empty(_notifications.Shown);
    }

    [Fact]
    public void Ask_suggests_activation_with_a_notification_action()
    {
        using var auto = Create(AutoGamingBehavior.Ask);
        DetectedGameProcess? suggested = null;
        auto.ActivationSuggested += (_, g) => suggested = g;

        auto.Start();
        _detection.RaiseStarted(_game);

        Assert.True(_detection.IsWatching);
        Assert.Equal(_game, suggested);
        Assert.Empty(_gaming.Activations);
        var n = Assert.Single(_notifications.Shown);
        Assert.Equal("Game_Detected_Title", n.Title.Key);
        Assert.Equal(GamingActions.Activate, n.ActionId);
        Assert.Equal("gaming.activate", n.ActionId);
    }

    [Fact]
    public async Task Ask_then_notification_action_activates_the_suggested_game()
    {
        using var auto = Create(AutoGamingBehavior.Ask);
        auto.Start();
        _detection.RaiseStarted(_game);

        _notifications.Invoke(GamingActions.Activate);
        await auto.LastActivation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(_game, Assert.Single(_gaming.Activations));
    }

    [Fact]
    public async Task Automatic_activates_and_notifies()
    {
        using var auto = Create(AutoGamingBehavior.Automatic);
        auto.Start();

        _detection.RaiseStarted(_game);
        await auto.LastActivation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(_game, Assert.Single(_gaming.Activations));
        Assert.Equal("Game_AutoActivated_Title", Assert.Single(_notifications.Shown).Title.Key);
    }

    [Fact]
    public async Task Nothing_happens_while_a_session_is_already_active()
    {
        using var auto = Create(AutoGamingBehavior.Automatic);
        auto.Start();
        _gaming.State = GamingState.Active;

        _detection.RaiseStarted(_game);
        await auto.LastActivation;

        Assert.Empty(_gaming.Activations);
    }

    [Fact]
    public async Task Switching_to_off_stops_watching_and_stop_unsubscribes()
    {
        using var auto = Create(AutoGamingBehavior.Ask);
        auto.Start();
        Assert.True(_detection.IsWatching);

        var off = _settings.Current.Clone();
        off.Gaming.AutoActivation = AutoGamingBehavior.Off;
        await _settings.SaveAsync(off);
        Assert.False(_detection.IsWatching);

        var auto2 = _settings.Current.Clone();
        auto2.Gaming.AutoActivation = AutoGamingBehavior.Automatic;
        await _settings.SaveAsync(auto2);
        Assert.True(_detection.IsWatching);

        auto.Stop();
        Assert.False(_detection.IsWatching);
        _detection.RaiseStarted(_game);
        Assert.Empty(_gaming.Activations);
    }
}

public sealed class GamingSessionReconcilerTests
{
    [Fact]
    public async Task Active_sessions_left_by_a_crash_become_interrupted()
    {
        var repo = new InMemoryGamingSessionRepository();
        var crashed = new GamingSession { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow.AddHours(-3), Status = GamingSessionStatus.Active, OptimizationSessionId = Guid.NewGuid() };
        var restored = new GamingSession { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow.AddDays(-1), Status = GamingSessionStatus.Restored };
        var current = new GamingSession { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, Status = GamingSessionStatus.Active };
        await repo.SaveAsync(crashed);
        await repo.SaveAsync(restored);
        await repo.SaveAsync(current);
        var gaming = new FakeGamingService { CurrentSession = current };

        var result = await new GamingSessionReconciler(repo, gaming).ReconcileAsync();

        var interrupted = Assert.Single(result);
        Assert.Equal(crashed.Id, interrupted.Id);
        Assert.Equal(GamingSessionStatus.Interrupted, (await repo.GetAsync(crashed.Id))!.Status);
        Assert.Null((await repo.GetAsync(crashed.Id))!.EndedAt);
        Assert.Equal(crashed.OptimizationSessionId, (await repo.GetAsync(crashed.Id))!.OptimizationSessionId);
        Assert.Equal(GamingSessionStatus.Restored, (await repo.GetAsync(restored.Id))!.Status);
        Assert.Equal(GamingSessionStatus.Active, (await repo.GetAsync(current.Id))!.Status);
    }
}
