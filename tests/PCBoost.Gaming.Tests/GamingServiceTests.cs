using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;

namespace PCBoost.Gaming.Tests;

public sealed class GamingServiceTests
{
    private const double Frame60 = 1000d / 60d;

    [Fact]
    public async Task Activation_persists_session_and_active_gaming_session_before_any_change()
    {
        await using var h = new GamingHarness();
        // Au moment de chaque modification : état du dépôt (instantané, le dépôt en mémoire répond immédiatement)
        // et sessions de restauration ouvertes.
        var snapshots = new List<(Task<IReadOnlyList<GamingSession>> Active, IReadOnlyList<OptimizationSession> Rollback)>();
        h.Rollback.BeforeApply = _ => snapshots.Add((h.Sessions.GetByStatusAsync(GamingSessionStatus.Active), h.Rollback.Sessions));

        var report = await h.Service.ActivateAsync(h.Game);

        Assert.True(report.Outcome.Success);
        Assert.Equal(GamingState.Active, h.Service.State);
        Assert.True(snapshots.Count >= 3); // alimentation + priorité + arrière-plan
        foreach (var (activeTask, rollbackSessions) in snapshots)
        {
            var session = Assert.Single(await activeTask);
            Assert.NotNull(session.OptimizationSessionId);
            Assert.Contains(rollbackSessions, s => s.Id == session.OptimizationSessionId && s.Type == SessionType.Gaming);
        }
        Assert.Equal(PowerScheme.HighPerformance, h.Power.ActiveId);
        Assert.Equal(ProcessPriority.AboveNormal, h.Processes.GetProcess(GamingHarness.GamePid)!.Priority);
        Assert.True(h.Processes.GetEfficiencyMode(100).Value);   // OneDrive ralenti
        Assert.False(h.Processes.GetEfficiencyMode(101).Value);  // Discord jamais
        var saved = await h.Sessions.GetAsync(report.SessionId!.Value);
        Assert.Equal(GamingSessionStatus.Active, saved!.Status);
        Assert.Equal("steam:730", saved.GameId);
        Assert.Equal(GamingHarness.GamePid, saved.ProcessId);
        Assert.Equal(3, saved.Optimizations.Count);
        Assert.All(saved.Optimizations, o => Assert.True(o.Applied));
        // La session de restauration reste « en cours » pendant la partie (récupération après plantage).
        Assert.Equal(SessionStatus.InProgress, h.Rollback.Sessions.Single().Status);
        Assert.Contains(h.Journal.Entries, e => e.Kind == ActivityKind.Gaming);
    }

    [Fact]
    public async Task Activation_follows_settings_and_uses_detection_when_no_game_is_given()
    {
        await using var h = new GamingHarness(x =>
        {
            x.Settings.Current.Gaming.SwitchPowerPlan = false;
            x.Settings.Current.Gaming.ThrottleBackgroundApps = false;
        });

        var report = await h.Service.ActivateAsync(null);

        Assert.Equal(h.Game, report.Game);
        Assert.Equal(PowerScheme.Balanced, h.Power.ActiveId);
        Assert.False(h.Processes.GetEfficiencyMode(100).Value);
        var power = Assert.Single(report.Optimizations, o => o.OptimizationId == GamingOptimizationIds.Power);
        Assert.False(power.Applied);
        Assert.Equal("Game_Opt_DisabledInSettings", power.Detail!.Key);
        Assert.True(Assert.Single(report.Optimizations, o => o.OptimizationId == GamingOptimizationIds.Priority).Applied);
        Assert.DoesNotContain(report.Optimizations, o => o.OptimizationId == GamingOptimizationIds.GpuPreference);
    }

    [Fact]
    public async Task Preview_changes_nothing()
    {
        await using var h = new GamingHarness();

        var preview = await h.Service.PreviewAsync(h.Game);

        Assert.Equal(3, preview.Count);
        Assert.All(preview, p => Assert.True(p.Applied));
        Assert.Equal(PowerScheme.Balanced, h.Power.ActiveId);
        Assert.Equal(ProcessPriority.Normal, h.Processes.GetProcess(GamingHarness.GamePid)!.Priority);
        Assert.Empty(h.Rollback.Sessions);
        Assert.Equal(GamingState.Inactive, h.Service.State);
    }

    [Fact]
    public async Task Game_exit_triggers_automatic_restore_and_notification()
    {
        await using var h = new GamingHarness();
        await h.Service.ActivateAsync(h.Game);
        var optSession = h.OptimizationSessionId;
        var gamingSessionId = h.Service.CurrentSession!.Id;

        h.Processes.Remove(GamingHarness.GamePid);
        await h.Service.TickAsync();

        Assert.Equal(GamingState.Inactive, h.Service.State);
        Assert.Null(h.Service.CurrentSession);
        Assert.Contains(optSession, h.Rollback.RestoredSessions);
        Assert.Equal(PowerScheme.Balanced, h.Power.ActiveId);
        Assert.False(h.Processes.GetEfficiencyMode(100).Value);
        var saved = await h.Sessions.GetAsync(gamingSessionId);
        Assert.Equal(GamingSessionStatus.Restored, saved!.Status);
        Assert.NotNull(saved.EndedAt);
        var notification = Assert.Single(h.Notifications.Shown);
        Assert.Equal("Game_Deactivated_Title", notification.Title.Key);
        Assert.Equal("Game_Deactivated_Body", notification.Body.Key);
    }

    [Fact]
    public async Task GameExited_event_from_detection_also_restores()
    {
        await using var h = new GamingHarness();
        await h.Service.ActivateAsync(h.Game);

        h.Detection.RaiseExited(h.Game);
        await h.Service.AutoRestoreTask.WaitAsync(TimeSpan.FromSeconds(10));
        h.Processes.Remove(GamingHarness.GamePid);
        await h.Service.TickAsync();

        Assert.Equal(GamingState.Inactive, h.Service.State);
        Assert.Single(h.Rollback.RestoredSessions);
        Assert.Single(h.Notifications.Shown);
    }

    [Fact]
    public async Task Without_auto_restore_the_session_stays_active_after_game_exit()
    {
        await using var h = new GamingHarness(x => x.Settings.Current.Gaming.AutoRestore = false);
        await h.Service.ActivateAsync(h.Game);

        h.Processes.Remove(GamingHarness.GamePid);
        await h.Service.TickAsync();

        Assert.Equal(GamingState.Active, h.Service.State);
        Assert.Empty(h.Rollback.RestoredSessions);
    }

    [Fact]
    public async Task Deactivation_is_idempotent_and_activation_too()
    {
        await using var h = new GamingHarness();
        var first = await h.Service.ActivateAsync(h.Game);
        var second = await h.Service.ActivateAsync(h.Game);

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal("Game_AlreadyActive", second.Outcome.Message!.Key);
        Assert.Single(h.Rollback.Sessions);

        var restore = await h.Service.DeactivateAsync();
        var again = await h.Service.DeactivateAsync();

        Assert.True(restore.Success);
        Assert.Equal(first.SessionId, restore.SessionId);
        Assert.True(restore.Restored >= 3);
        Assert.Null(again.SessionId);
        Assert.Equal(0, again.Restored);
        Assert.Single(h.Rollback.RestoredSessions);
        Assert.Single(h.Notifications.Shown);
        Assert.Equal(ProcessPriority.Normal, h.Processes.GetProcess(GamingHarness.GamePid)!.Priority);
    }

    [Fact]
    public async Task State_machine_transitions_are_reported()
    {
        await using var h = new GamingHarness();
        var states = new List<GamingState>();
        h.Service.StateChanged += (_, _) => states.Add(h.Service.State);

        await h.Service.ActivateAsync(h.Game);
        await h.Service.DeactivateAsync();

        Assert.Equal([GamingState.Activating, GamingState.Active, GamingState.Restoring, GamingState.Inactive], states);
    }

    [Fact]
    public async Task Refused_frame_capture_leaves_fps_unavailable()
    {
        await using var h = new GamingHarness(x =>
        {
            x.Settings.Current.Gaming.MeasureFrameRate = true;
            x.Frames.Grant = false;
        });

        var report = await h.Service.ActivateAsync(h.Game);
        await h.Service.TickAsync();

        Assert.Equal(FrameCaptureAvailability.RequiresElevation, report.FrameCapture);
        Assert.Equal([GamingHarness.GamePid], h.Frames.StartRequests);
        var live = h.Service.LiveMetrics;
        Assert.NotNull(live);
        Assert.False(live.Frames.HasData);
        Assert.Null(live.Frames.AverageFps);
        Assert.Null(live.Frames.OnePercentLowFps);
        Assert.Equal(FrameCaptureAvailability.RequiresElevation, live.FrameCapture);
        Assert.Equal(45, live.CpuPercent);
        Assert.Equal("Sys_FrameCaptureCancelled", h.Service.FrameCaptureError!.Key);

        await h.Service.DeactivateAsync();
        var saved = (await h.Sessions.GetRecentAsync(1)).Single();
        Assert.Null(saved.FrameStats);
    }

    [Fact]
    public async Task Frame_capture_disabled_in_settings_is_never_started()
    {
        await using var h = new GamingHarness();

        var report = await h.Service.ActivateAsync(h.Game);

        Assert.Equal(FrameCaptureAvailability.Disabled, report.FrameCapture);
        Assert.Empty(h.Frames.StartRequests);
    }

    [Fact]
    public async Task Unsupported_frame_capture_is_reported_without_prompting()
    {
        await using var h = new GamingHarness(x =>
        {
            x.Settings.Current.Gaming.MeasureFrameRate = true;
            x.Frames.Availability = FrameCaptureAvailability.NotSupported;
        });

        var report = await h.Service.ActivateAsync(h.Game);

        Assert.Equal(FrameCaptureAvailability.NotSupported, report.FrameCapture);
        Assert.Empty(h.Frames.StartRequests);
    }

    [Fact]
    public async Task Live_metrics_use_measured_frames_and_session_stats_are_saved()
    {
        await using var h = new GamingHarness(x => x.Settings.Current.Gaming.MeasureFrameRate = true);
        var updates = new List<GamingLiveMetrics>();
        h.Service.LiveMetricsUpdated += (_, m) => updates.Add(m);
        await h.Service.ActivateAsync(h.Game);
        var capture = h.Frames.LastSession!;

        capture.Push(Enumerable.Range(0, 601).Select(i => i * Frame60).ToArray());
        await h.Service.TickAsync();

        var live = Assert.Single(updates);
        Assert.Equal(FrameCaptureAvailability.Available, live.FrameCapture);
        Assert.Equal(60, live.Frames.AverageFps!.Value, 3);
        Assert.Equal(80, live.GpuPercent);

        // Jeu en pause : plus d'images récentes → FPS en direct « non disponibles ».
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await h.Service.TickAsync();
        Assert.False(h.Service.LiveMetrics!.Frames.HasData);

        await h.Service.DeactivateAsync();
        Assert.True(capture.Disposed);
        var saved = (await h.Sessions.GetRecentAsync(1)).Single();
        Assert.Equal(60, saved.FrameStats!.AverageFps!.Value, 3);
        Assert.Equal(601, saved.FrameStats.FrameCount);
    }

    [Fact]
    public async Task No_monitor_sample_means_no_invented_live_metrics()
    {
        await using var h = new GamingHarness(x => x.Monitor.Latest = null);
        await h.Service.ActivateAsync(h.Game);

        await h.Service.TickAsync();

        Assert.Null(h.Service.LiveMetrics);
    }

    [Fact]
    public async Task Monitor_is_switched_to_active_then_given_back()
    {
        await using var h = new GamingHarness();

        await h.Service.ActivateAsync(h.Game);
        Assert.Equal(MonitoringMode.Active, h.Monitor.Mode);

        await h.Service.DeactivateAsync();
        Assert.Equal(MonitoringMode.Background, h.Monitor.Mode);
        Assert.Equal(0, h.Monitor.StopCalls);
    }

    [Fact]
    public async Task Monitor_mode_is_left_alone_when_the_caller_already_uses_active_mode()
    {
        await using var h = new GamingHarness(x => x.Monitor.SetRunning(true, MonitoringMode.Active));

        await h.Service.ActivateAsync(h.Game);
        await h.Service.DeactivateAsync();

        Assert.Empty(h.Monitor.ModeChanges);
        Assert.Equal(MonitoringMode.Active, h.Monitor.Mode);
    }

    [Fact]
    public async Task Stopped_monitor_is_started_for_the_session_and_stopped_afterwards()
    {
        await using var h = new GamingHarness(x => x.Monitor.SetRunning(false, MonitoringMode.Background));

        await h.Service.ActivateAsync(h.Game);
        Assert.True(h.Monitor.IsRunning);
        await h.Service.DeactivateAsync();

        Assert.False(h.Monitor.IsRunning);
        Assert.Equal(1, h.Monitor.StopCalls);
    }

    [Fact]
    public async Task Failed_restore_is_reported_honestly()
    {
        await using var h = new GamingHarness(x => x.Rollback.FailRestore = true);
        await h.Service.ActivateAsync(h.Game);

        var report = await h.Service.DeactivateAsync();

        Assert.False(report.Success);
        Assert.Equal(1, report.Failed);
        var saved = (await h.Sessions.GetRecentAsync(1)).Single();
        Assert.Equal(GamingSessionStatus.RestoreFailed, saved.Status);
        Assert.Equal("Game_Deactivated_PartialBody", h.Notifications.Shown.Single().Body.Key);
        Assert.Equal(GamingState.Inactive, h.Service.State);
    }

    [Fact]
    public async Task Activation_for_a_game_that_already_exited_changes_nothing()
    {
        await using var h = new GamingHarness();
        h.Processes.Remove(GamingHarness.GamePid);

        var report = await h.Service.ActivateAsync(h.Game);

        Assert.False(report.Outcome.Success);
        Assert.Equal(OperationErrorKind.NotFound, report.Outcome.Error);
        Assert.Empty(h.Rollback.Sessions);
        Assert.Equal(GamingState.Inactive, h.Service.State);
    }

    [Fact]
    public async Task Cancelled_activation_restores_what_was_applied()
    {
        await using var h = new GamingHarness();
        using var cts = new CancellationTokenSource();
        h.Rollback.BeforeApply = change =>
        {
            if (change.OptimizationId == GamingOptimizationIds.Priority) cts.Cancel();
        };

        var report = await h.Service.ActivateAsync(h.Game, cts.Token);

        Assert.Equal(OperationErrorKind.Cancelled, report.Outcome.Error);
        Assert.Equal(GamingState.Inactive, h.Service.State);
        Assert.Single(h.Rollback.RestoredSessions);
        Assert.Equal(PowerScheme.Balanced, h.Power.ActiveId);
        var saved = (await h.Sessions.GetRecentAsync(1)).Single();
        Assert.NotEqual(GamingSessionStatus.Active, saved.Status);
    }

    [Fact]
    public async Task Protected_game_priority_is_skipped_without_failing_activation()
    {
        await using var h = new GamingHarness(x => x.Processes.AccessDeniedProcessIds.Add(GamingHarness.GamePid));

        var report = await h.Service.ActivateAsync(h.Game);

        Assert.True(report.Outcome.Success);
        var priority = Assert.Single(report.Optimizations, o => o.OptimizationId == GamingOptimizationIds.Priority);
        Assert.False(priority.Applied);
        Assert.Equal("Game_Priority_Protected", priority.Detail!.Key);
    }

    [Fact]
    public async Task DisposeAsync_restores_an_active_session()
    {
        var h = new GamingHarness();
        await h.Service.ActivateAsync(h.Game);

        await h.DisposeAsync();

        Assert.Equal(PowerScheme.Balanced, h.Power.ActiveId);
        Assert.Single(h.Rollback.RestoredSessions);
    }

    [Fact]
    public async Task Windows_settings_checks_are_exposed()
    {
        await using var h = new GamingHarness();

        var checks = h.Service.CheckWindowsGameSettings(h.Game);

        Assert.Contains(checks, c => c.Id == GameSettingCheckIds.GameMode);
        Assert.Contains(checks, c => c.Id == GameSettingCheckIds.HardwareGpuScheduling);
        Assert.Contains(checks, c => c.Id == GameSettingCheckIds.GpuPreference);
    }
}
