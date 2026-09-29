using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;

namespace PCBoost.Gaming.Services;

/// <summary>
/// Mode Gaming automatique (§19). Réagit à <see cref="IGameDetectionService.GameStarted"/> selon
/// <see cref="GamingSettings.AutoActivation"/> : Off → rien ; Ask → <see cref="ActivationSuggested"/> + notification avec
/// l'action <c>gaming.activate</c> ; Automatic → activation + notification. La restauration à la fermeture du jeu est assurée
/// par <see cref="IGamingService"/> (<see cref="GamingSettings.AutoRestore"/>). Démarre et arrête la surveillance de détection.
/// </summary>
public sealed class AutoGamingMode : IAutoGamingMode
{
    private readonly IGameDetectionService _detection;
    private readonly IGamingService _gaming;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly ILogger<AutoGamingMode> _logger;
    private readonly Lock _sync = new();
    private bool _started;
    private bool _startedWatching;
    private DetectedGameProcess? _pendingSuggestion;
    private Task _lastActivation = Task.CompletedTask;
    private bool _disposed;

    public AutoGamingMode(IGameDetectionService detection, IGamingService gaming, ISettingsService settings, INotificationService notifications, ILogger<AutoGamingMode>? logger = null)
    {
        _detection = detection ?? throw new ArgumentNullException(nameof(detection));
        _gaming = gaming ?? throw new ArgumentNullException(nameof(gaming));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? NullLogger<AutoGamingMode>.Instance;
    }

    public event EventHandler<DetectedGameProcess>? ActivationSuggested;

    /// <summary>Dernière activation déclenchée (automatique ou depuis la notification).</summary>
    internal Task LastActivation
    {
        get { lock (_sync) return _lastActivation; }
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
        }
        _detection.GameStarted += OnGameStarted;
        _settings.SettingsChanged += OnSettingsChanged;
        _notifications.ActionInvoked += OnNotificationAction;
        ApplyWatchState(_settings.Current.Gaming.AutoActivation);
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_started) return;
            _started = false;
            _pendingSuggestion = null;
        }
        _detection.GameStarted -= OnGameStarted;
        _settings.SettingsChanged -= OnSettingsChanged;
        _notifications.ActionInvoked -= OnNotificationAction;
        StopWatchingIfOwned();
    }

    public void Dispose()
    {
        Stop();
        lock (_sync) _disposed = true;
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => ApplyWatchState(settings.Gaming.AutoActivation);

    /// <summary>La surveillance ne tourne que si l'activation automatique n'est pas désactivée.</summary>
    private void ApplyWatchState(AutoGamingBehavior behavior)
    {
        if (behavior == AutoGamingBehavior.Off)
        {
            StopWatchingIfOwned();
            return;
        }
        lock (_sync)
        {
            if (!_started || _startedWatching || _detection.IsWatching) return;
            _startedWatching = true;
        }
        _detection.StartWatching();
    }

    private void StopWatchingIfOwned()
    {
        bool owned;
        lock (_sync)
        {
            owned = _startedWatching;
            _startedWatching = false;
        }
        if (owned) _detection.StopWatching();
    }

    private void OnGameStarted(object? sender, DetectedGameProcess game)
    {
        var behavior = _settings.Current.Gaming.AutoActivation;
        switch (behavior)
        {
            case AutoGamingBehavior.Off:
                return;
            case AutoGamingBehavior.Ask:
                if (_gaming.State != GamingState.Inactive) return;
                lock (_sync) _pendingSuggestion = game;
                _logger.LogInformation("Jeu détecté ({Game}) : activation du mode Gaming proposée", game.Game.Name);
                try
                {
                    ActivationSuggested?.Invoke(this, game);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "Abonné ActivationSuggested en erreur");
                }
                _notifications.Show(new NotificationRequest(
                    TextRef.Of("Game_Detected_Title"),
                    TextRef.Of("Game_Detected_Body", game.Game.Name),
                    GamingActions.Activate,
                    TextRef.Of("Game_Detected_Action"),
                    GamingActions.NotificationTag));
                return;
            case AutoGamingBehavior.Automatic:
                if (_gaming.State != GamingState.Inactive) return;
                StartActivation(game, notify: true);
                return;
        }
    }

    private void OnNotificationAction(object? sender, string actionId)
    {
        if (!string.Equals(actionId, GamingActions.Activate, StringComparison.Ordinal)) return;
        DetectedGameProcess? game;
        lock (_sync)
        {
            game = _pendingSuggestion;
            _pendingSuggestion = null;
        }
        if (game is null || _gaming.State != GamingState.Inactive) return;
        StartActivation(game, notify: false);
    }

    private void StartActivation(DetectedGameProcess game, bool notify)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                var report = await _gaming.ActivateAsync(game, CancellationToken.None).ConfigureAwait(false);
                if (notify && report.Outcome.Success && report.SessionId is not null)
                {
                    _notifications.Show(new NotificationRequest(
                        TextRef.Of("Game_AutoActivated_Title"),
                        TextRef.Of("Game_AutoActivated_Body", game.Game.Name),
                        Tag: GamingActions.NotificationTag));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Activation automatique du mode Gaming en erreur");
            }
        });
        lock (_sync) _lastActivation = task;
    }
}
