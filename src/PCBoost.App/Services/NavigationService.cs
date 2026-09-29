using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using PCBoost.App.Views;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Navigation;

namespace PCBoost.App.Services;

/// <summary>Navigation par clé de page (PageKeys) dans le cadre principal de la fenêtre.</summary>
public sealed class NavigationService : INavigationService
{
    private static readonly Dictionary<string, (Type Page, object? ForcedParameter)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [PageKeys.Welcome] = (typeof(WelcomePage), null),
        [PageKeys.Home] = (typeof(HomePage), null),
        [PageKeys.Analysis] = (typeof(AnalysisPage), null),
        [PageKeys.Diagnosis] = (typeof(DiagnosisPage), null),
        [PageKeys.Optimization] = (typeof(OptimizationPage), null),
        [PageRegistry.Profiles] = (typeof(OptimizationPage), "profiles"),
        [PageKeys.OldPc] = (typeof(OptimizationPage), "oldpc"),
        [PageKeys.Cleanup] = (typeof(CleanupPage), null),
        [PageKeys.Startup] = (typeof(StartupPage), null),
        [PageKeys.Processes] = (typeof(ProcessesPage), null),
        [PageKeys.Gaming] = (typeof(GamingPage), null),
        [PageKeys.Benchmark] = (typeof(BenchmarkPage), null),
        [PageKeys.Performance] = (typeof(PerformancePage), null),
        [PageKeys.History] = (typeof(HistoryPage), null),
        [PageKeys.Journal] = (typeof(HistoryPage), PageKeys.Journal),
        [PageKeys.Settings] = (typeof(SettingsPage), null),
        [PageKeys.Privacy] = (typeof(PrivacyPage), null),
        [PageKeys.About] = (typeof(AboutPage), null),
        [PageKeys.Storage] = (typeof(StoragePage), null),
        [PageKeys.Expert] = (typeof(ExpertPage), null),
    };

    private readonly ILogger<NavigationService> _logger;
    private Frame? _frame;
    private object? _lastParameter;

    public NavigationService(ILogger<NavigationService> logger) => _logger = logger;

    public string? CurrentPageKey { get; private set; }

    public bool CanGoBack => _frame?.CanGoBack == true;

    public event EventHandler<string>? Navigated;

    public void Attach(Frame frame)
    {
        _frame = frame;
        _frame.Navigated += (_, e) =>
        {
            var key = KeyFor(e.SourcePageType, e.Parameter);
            if (key is null) return;
            CurrentPageKey = key;
            Navigated?.Invoke(this, key);
        };
    }

    public static bool IsKnown(string pageKey) => Map.ContainsKey(pageKey);

    public bool Navigate(string pageKey, object? parameter = null)
    {
        if (_frame is null || string.IsNullOrEmpty(pageKey)) return false;
        if (!Map.TryGetValue(pageKey, out var entry))
        {
            _logger.LogWarning("Page inconnue : {PageKey}", pageKey);
            return false;
        }

        var effective = parameter ?? entry.ForcedParameter;
        if (_frame.Content?.GetType() == entry.Page && Equals(_lastParameter, effective) && parameter is null)
            return true;

        _lastParameter = effective;
        return _frame.Navigate(entry.Page, effective, new EntranceNavigationTransitionInfo());
    }

    /// <summary>Recrée la page courante (changement de langue : les ViewModels sont transitoires).</summary>
    public void ReloadCurrent()
    {
        if (_frame?.Content is null) return;
        var type = _frame.Content.GetType();
        _frame.Navigate(type, _lastParameter, new SuppressNavigationTransitionInfo());
        if (_frame.BackStack.Count > 0) _frame.BackStack.RemoveAt(_frame.BackStack.Count - 1);
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true) _frame.GoBack();
    }

    public void ClearHistory() => _frame?.BackStack.Clear();

    private static string? KeyFor(Type pageType, object? parameter)
    {
        if (parameter is string p && Map.TryGetValue(p, out var forced) && forced.Page == pageType)
            return p;
        foreach (var (key, value) in Map)
        {
            if (value.Page == pageType && value.ForcedParameter is null) return key;
        }
        return null;
    }
}
