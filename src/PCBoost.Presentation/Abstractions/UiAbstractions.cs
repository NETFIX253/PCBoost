using PCBoost.Core.Common;
using PCBoost.Core.Settings;

namespace PCBoost.Presentation.Abstractions;

public interface INavigationService
{
    string? CurrentPageKey { get; }

    bool Navigate(string pageKey, object? parameter = null);

    bool CanGoBack { get; }

    void GoBack();

    event EventHandler<string>? Navigated;
}

public enum DialogResultKind { Primary = 0, Secondary, Cancel }

public sealed record ConfirmationRequest(
    TextRef Title,
    TextRef Message,
    TextRef PrimaryButton,
    TextRef? SecondaryButton = null,
    TextRef? CloseButton = null,
    bool IsDestructive = false,
    IReadOnlyList<TextRef>? Details = null);

public interface IDialogService
{
    Task<DialogResultKind> ConfirmAsync(ConfirmationRequest request);

    /// <summary>Affiche une erreur en langage humain ; le détail technique n'apparaît qu'en mode Expert.</summary>
    Task ShowErrorAsync(OperationResult result, TextRef? context = null);

    Task ShowMessageAsync(TextRef title, TextRef message);
}

/// <summary>Exécution sur le thread UI (les événements des services arrivent sur des threads d'arrière-plan).</summary>
public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    void Post(Action action);

    Task InvokeAsync(Func<Task> action);
}

public interface IThemeService
{
    ThemePreference Current { get; }

    void Apply(ThemePreference theme);
}

/// <summary>Événements de cycle de vie de la fenêtre (visibilité → mode de surveillance adaptatif).</summary>
public interface IAppLifecycle
{
    bool IsWindowVisible { get; }

    event EventHandler<bool>? WindowVisibilityChanged;

    void ShowMainWindow();

    void Exit();
}

/// <summary>Formatage localisé (tailles, pourcentages, durées). "Non disponible" pour les valeurs nulles.</summary>
public interface IValueFormatter
{
    /// <summary>Octets en o / Ko / Mo (sans décimale) puis Go / To (1 décimale).</summary>
    string Bytes(long? bytes);

    string Percent(double? value, int decimals = 0);

    /// <summary>« 62 °C ».</summary>
    string Temperature(double? celsius);

    /// <summary>Durée lisible (« 45 s », « 12 min », « 3 h 05 min », « 4 j 2 h »).</summary>
    string Duration(TimeSpan? duration);

    /// <summary>Date relative courte (« à l'instant », « il y a 5 min », « hier à 14:05 », sinon date courte).</summary>
    string DateTime(DateTimeOffset? value);

    /// <summary>Date et heure complètes, heure locale (« 28/09/2026 14:05 »).</summary>
    string DateTimeFull(DateTimeOffset? value);

    /// <summary>Heure locale seule (« 14:05:12 »).</summary>
    string Time(DateTimeOffset? value);

    string Number(double? value, int decimals = 0);

    /// <summary>Débit (« 1,2 Mo/s »).</summary>
    string Rate(double? bytesPerSecond);

    /// <summary>Fréquence (« 800 MHz », « 3,6 GHz »).</summary>
    string Frequency(double? megahertz);

    /// <summary>Images par seconde (« 60 FPS »).</summary>
    string Fps(double? fps);

    /// <summary>Millisecondes (« 16,7 ms »).</summary>
    string Milliseconds(double? milliseconds);

    string NotAvailable { get; }
}
