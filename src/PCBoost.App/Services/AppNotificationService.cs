using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using PCBoost.Core.Localization;
using PCBoost.Core.Services;

namespace PCBoost.App.Services;

/// <summary>
/// Notifications Windows via le Windows App SDK (AppNotificationManager, compatible application non empaquetée).
/// Si leur inscription échoue sur ce PC, repli sur la notification de la zone de notification (sans boutons ;
/// un clic ramène la fenêtre). Respecte le réglage « Notifications ». Un clic sur un bouton relaie son identifiant d'action.
/// </summary>
public sealed class AppNotificationService : INotificationService, IDisposable
{
    private const string ActionArgument = "action";
    private readonly ILocalizer _localizer;
    private readonly ISettingsService _settings;
    private readonly ILogger<AppNotificationService> _logger;
    private bool _registered;
    private Func<string, string, bool>? _fallback;

    public AppNotificationService(ILocalizer localizer, ISettingsService settings, ILogger<AppNotificationService> logger)
    {
        _localizer = localizer;
        _settings = settings;
        _logger = logger;
    }

    public event EventHandler<string>? ActionInvoked;

    /// <summary>Clic sur la notification elle-même (sans bouton) : ramener la fenêtre.</summary>
    public event EventHandler? NotificationActivated;

    public void Register()
    {
        if (_registered) return;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Notifications du Windows App SDK indisponibles ({Message}) : repli sur la zone de notification.", ex.Message);
        }
    }

    /// <summary>Affichage de repli (titre, texte) utilisé quand les notifications du Windows App SDK sont indisponibles.</summary>
    public void SetFallback(Func<string, string, bool> fallback) => _fallback = fallback;

    public void Show(NotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_settings.Current.NotificationsEnabled) return;
        if (!_registered)
        {
            ShowFallback(request);
            return;
        }
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(_localizer.Format(request.Title))
                .AddText(_localizer.Format(request.Body));
            if (!string.IsNullOrEmpty(request.ActionId) && request.ActionLabel is not null)
            {
                builder.AddButton(new AppNotificationButton(_localizer.Format(request.ActionLabel))
                    .AddArgument(ActionArgument, request.ActionId));
            }
            var notification = builder.BuildNotification();
            if (!string.IsNullOrEmpty(request.Tag)) notification.Tag = request.Tag;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Échec d'affichage d'une notification : {Message}", ex.Message);
        }
    }

    private void ShowFallback(NotificationRequest request)
    {
        if (_fallback is null) return;
        try
        {
            if (!_fallback(_localizer.Format(request.Title), _localizer.Format(request.Body)))
                _logger.LogDebug("Notification de repli non affichée (zone de notification indisponible).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Échec d'affichage d'une notification : {Message}", ex.Message);
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue(ActionArgument, out var action) && !string.IsNullOrEmpty(action))
            ActionInvoked?.Invoke(this, action);
        else
            NotificationActivated?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (!_registered) return;
        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Désinscription des notifications : {Message}", ex.Message);
        }
        _registered = false;
    }
}
