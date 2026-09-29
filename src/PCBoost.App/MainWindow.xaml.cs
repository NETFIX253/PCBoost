using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PCBoost.App.Helpers;
using PCBoost.App.Interop;
using PCBoost.App.Services;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App;

/// <summary>
/// Fenêtre principale : navigation, bandeaux (récupération, jeu détecté), zone de notification,
/// fermeture vers la zone de notification, visibilité → surveillance adaptative.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int TrayOpen = 1, TrayGaming = 2, TrayOptimize = 3, TrayPause = 4, TraySettings = 5, TrayExit = 6;

    private readonly NavigationService _navigation;
    private readonly ISettingsService _settings;
    private readonly IPerformanceMonitor _monitor;
    private readonly AppLifecycle _lifecycle;
    private readonly AppNotificationService _notifications;
    private readonly ILogger<MainWindow> _logger;
    private TrayIcon? _tray;
    private bool _exiting;
    private bool _syncingSelection;
    private bool _trayHintShown;
    private static readonly int[] TitleLogoSizes = [16, 20, 24, 32];
    private double _titleLogoScale;
    private bool _titleLogoTracked;
    private MonitoringMode _modeBeforePause = MonitoringMode.Background;

    public MainWindow()
    {
        // Le répartiteur UI doit être créé sur le thread UI avant tout ViewModel.
        App.GetService<IUiDispatcher>();
        Shell = App.GetService<ShellViewModel>();
        _navigation = App.GetService<NavigationService>();
        _settings = App.GetService<ISettingsService>();
        _monitor = App.GetService<IPerformanceMonitor>();
        _lifecycle = App.GetService<AppLifecycle>();
        _notifications = App.GetService<AppNotificationService>();
        _logger = App.GetService<ILogger<MainWindow>>();
        InitializeComponent();

        // Thème appliqué avant le premier rendu : tous les éléments (volet de navigation compris) résolvent
        // leurs ressources de thème une seule fois, avec le bon thème.
        var theme = App.GetService<ThemeService>();
        theme.Attach(this);
        theme.Apply(Program.ThemeOverride?.ToLowerInvariant() switch
        {
            "light" => Core.Settings.ThemePreference.Light,
            "dark" => Core.Settings.ThemePreference.Dark,
            "system" => Core.Settings.ThemePreference.System,
            _ => _settings.Current.Theme,
        });
    }

    public ShellViewModel Shell { get; }

    public void Initialize(bool showWindow)
    {
        Title = Shell.ProductName.Length > 0 ? Shell.ProductName : Str.Get("App_WindowTitle");
        ConfigureWindowChrome();

        _navigation.Attach(ContentFrame);
        _navigation.Navigated += OnNavigated;
        App.GetService<DialogService>().Attach(() => Content?.XamlRoot);
        _lifecycle.Attach(ShowAndActivate, () => _ = ExitAsync());

        BuildNavigationItems();
        TitleLogo.Loaded += OnTitleLogoLoaded;
        Shell.PropertyChanged += OnShellPropertyChanged;
        Shell.PageReloadRequested += (_, _) =>
        {
            BuildNavigationItems();
            _navigation.ReloadCurrent();
            UpdateTrayToolTip();
        };

        _notifications.Register();
        _notifications.NotificationActivated += (_, _) => DispatcherQueue.TryEnqueue(ShowAndActivate);
        _notifications.ActionInvoked += OnNotificationAction;

        CreateTrayIcon();
        AppWindow.Closing += OnClosing;
        AppWindow.Changed += OnAppWindowChanged;
        Activated += (_, e) => _lifecycle.SetVisible(AppWindow.IsVisible);

        Bindings.Update();
        _ = InitializeShellAsync();

        if (Program.StartPage is { } startPage && NavigationService.IsKnown(startPage))
        {
            if (startPage == PageKeys.Welcome) ShowWelcome();
            else _navigation.Navigate(startPage);
        }
        else if (_settings.Current.FirstRunCompleted)
        {
            _navigation.Navigate(PageKeys.Home);
        }
        else
        {
            ShowWelcome();
        }

        if (showWindow)
        {
            Activate();
            _lifecycle.SetVisible(true);
        }
        else
        {
            _lifecycle.SetVisible(false);
        }

        if (Program.CaptureDirectory is { } captureDirectory)
        {
            _ = App.GetService<DevCaptureService>().RunAsync(RootGrid, captureDirectory, Program.CapturePages, ExitAsync);
        }
    }

    public void ShowAndActivate()
    {
        if (!AppWindow.IsVisible) AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        _lifecycle.SetVisible(true);
    }

    private async Task InitializeShellAsync()
    {
        try
        {
            await Shell.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Initialisation du shell : {Message}", ex.Message);
        }
    }

    private void ApplyFallbackBackground()
        => RootGrid.Background = new SolidColorBrush(RootGrid.ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));

    private void ConfigureWindowChrome()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            CaptionButtonsColumn.Width = new GridLength(Math.Max(138, AppWindow.TitleBar.RightInset / RootScale()));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Barre de titre personnalisée indisponible : {Message}", ex.Message);
        }

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "app.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }
        else
        {
            // Sans Mica (Windows 10) : fond uni qui suit le thème effectif de la fenêtre.
            ApplyFallbackBackground();
            RootGrid.ActualThemeChanged += (_, _) => ApplyFallbackBackground();
        }

        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = (int)(820 * WindowScale());
                presenter.PreferredMinimumHeight = (int)(600 * WindowScale());
            }
            // Tailles en unités indépendantes de la résolution (DIP), converties en pixels selon la mise à l'échelle de l'écran.
            var scale = WindowScale();
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var width = Math.Min((int)(1320 * scale), (int)(area.Width * 0.9));
            var height = Math.Min((int)(900 * scale), (int)(area.Height * 0.9));
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Dimensionnement de la fenêtre : {Message}", ex.Message);
        }
    }

    /// <summary>Mise à l'échelle de l'écran de la fenêtre (1,0 à 96 ppp), disponible avant le premier rendu.</summary>
    private double WindowScale()
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        return dpi > 0 ? dpi / 96d : 1.0;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    private double RootScale() => Content?.XamlRoot?.RasterizationScale is double s && s > 0 ? s : 1.0;

    private void OnTitleLogoLoaded(object sender, RoutedEventArgs e)
    {
        UpdateTitleLogo();
        if (_titleLogoTracked || TitleLogo.XamlRoot is null) return;
        _titleLogoTracked = true;
        TitleLogo.XamlRoot.Changed += (_, _) => UpdateTitleLogo();
    }

    /// <summary>
    /// Logo de la barre de titre (16 DIP) : image dessinée pour la taille exacte en pixels à la mise à l'échelle courante
    /// (16, 20, 24 ou 32 px), plutôt qu'une image réduite et floue.
    /// </summary>
    private void UpdateTitleLogo()
    {
        var scale = TitleLogo.XamlRoot?.RasterizationScale is double s && s > 0 ? s : 1.0;
        if (Math.Abs(scale - _titleLogoScale) < 0.001) return;
        _titleLogoScale = scale;
        var pixels = (int)Math.Ceiling(16 * scale - 0.01);
        var size = TitleLogoSizes.FirstOrDefault(n => n >= pixels, TitleLogoSizes[^1]);
        TitleLogo.Source = new BitmapImage(new Uri($"ms-appx:///Assets/Branding/logo-{size}.png"));
    }

    private void BuildNavigationItems()
    {
        _syncingSelection = true;
        NavView.MenuItems.Clear();
        foreach (var item in Shell.NavigationItems)
            NavView.MenuItems.Add(CreateItem(item));

        NavView.FooterMenuItems.Clear();
        if (Shell.IsExpertModeVisible) NavView.FooterMenuItems.Add(CreateItem(Shell.ExpertItem));
        NavView.FooterMenuItems.Add(CreateItem(Shell.SettingsItem));
        _syncingSelection = false;
        SelectCurrent(_navigation.CurrentPageKey);
    }

    private static NavigationViewItem CreateItem(NavigationItemViewModel item)
    {
        var nav = new NavigationViewItem
        {
            Content = item.Label,
            Tag = item.PageKey,
            Icon = new FontIcon { Glyph = item.IconGlyph },
        };
        ToolTipService.SetToolTip(nav, item.ToolTip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nav, item.AccessibleName);
        return nav;
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection) return;
        if (args.SelectedItem is NavigationViewItem { Tag: string key })
            _navigation.Navigate(key);
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => _navigation.GoBack();

    private void OnNavigated(object? sender, string key)
    {
        NavView.IsBackEnabled = _navigation.CanGoBack;
        var isWelcome = key == PageKeys.Welcome;
        NavView.IsPaneVisible = !isWelcome;
        if (!isWelcome && !_settings.Current.FirstRunCompleted) _navigation.ClearHistory();
        SelectCurrent(key);
    }

    private void SelectCurrent(string? key)
    {
        if (key is null) return;
        var menuKey = key switch
        {
            PageKeys.Diagnosis or PageKeys.Storage => PageKeys.Analysis,
            PageKeys.OldPc or PageRegistry.Profiles => PageKeys.Optimization,
            PageKeys.Benchmark => PageKeys.Gaming,
            PageKeys.Journal => PageKeys.History,
            PageKeys.Privacy or PageKeys.About => PageKeys.Settings,
            _ => key,
        };
        var target = NavView.MenuItems.Concat(NavView.FooterMenuItems).OfType<NavigationViewItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, menuKey, StringComparison.OrdinalIgnoreCase));
        _syncingSelection = true;
        NavView.SelectedItem = target;
        _syncingSelection = false;
    }

    private void ShowWelcome()
    {
        NavView.IsPaneVisible = false;
        _navigation.Navigate(PageKeys.Welcome);
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsExpertModeVisible))
            DispatcherQueue.TryEnqueue(BuildNavigationItems);
    }

    private void OnNotificationAction(object? sender, string action)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            const string prefix = "navigate:";
            if (action.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                ShowAndActivate();
                _navigation.Navigate(action[prefix.Length..]);
            }
            else if (action.StartsWith("gaming.", StringComparison.OrdinalIgnoreCase))
            {
                ShowAndActivate();
                _navigation.Navigate(PageKeys.Gaming);
            }
        });
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange) _lifecycle.SetVisible(sender.IsVisible);
        if (args.DidPresenterChange && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            _lifecycle.SetVisible(false);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        if (_settings.Current.MinimizeToTray && _tray is not null)
        {
            args.Cancel = true;
            AppWindow.Hide();
            _lifecycle.SetVisible(false);
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _notifications.Show(new NotificationRequest(TextRef.Of("App_Notify_StillRunning_Title"), TextRef.Of("App_Notify_StillRunning_Body"), Tag: "tray-hint"));
            }
            return;
        }
        args.Cancel = true;
        await ExitAsync().ConfigureAwait(true);
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        AppWindow.Hide();
        _tray?.Dispose();
        _tray = null;
        _notifications.Dispose();
        if (Application.Current is App app) await app.ShutdownAsync().ConfigureAwait(true);

        // Tout est enregistré et libéré (base de données, journaux, icône de la zone de notification, notifications) :
        // fin immédiate du processus, sans démontage de XAML. Pendant ce démontage, le finaliseur .NET libérait parfois
        // des objets WinRT déjà détruits (plantage 0xC0000005 dans ComWrappers.NativeObjectWrapper.Finalize à la fermeture).
        Environment.Exit(0);
    }

    private void CreateTrayIcon()
    {
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "app.ico");
        try
        {
            _tray = new TrayIcon(icon, Str.Get("App_Tray_ToolTip"));
            _notifications.SetFallback((title, body) => _tray?.ShowBalloon(title, body) ?? false);
            _tray.Activated += (_, _) => DispatcherQueue.TryEnqueue(ShowAndActivate);
            _tray.MenuProvider = () =>
            [
                new(TrayOpen, Str.Get("App_Tray_Open")),
                new(0, string.Empty, IsSeparator: true),
                new(TrayGaming, Str.Get("App_Tray_Gaming")),
                new(TrayOptimize, Str.Get("App_Tray_Optimize")),
                new(TrayPause, Str.Get(_monitor.Mode == MonitoringMode.Paused ? "App_Tray_ResumeMonitoring" : "App_Tray_PauseMonitoring")),
                new(TraySettings, Str.Get("App_Tray_Settings")),
                new(0, string.Empty, IsSeparator: true),
                new(TrayExit, Str.Get("App_Tray_Exit")),
            ];
            _tray.MenuItemSelected += (_, id) => DispatcherQueue.TryEnqueue(() => OnTrayCommand(id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Icône de la zone de notification indisponible : {Message}", ex.Message);
            _tray = null;
        }
    }

    private void OnTrayCommand(int id)
    {
        switch (id)
        {
            case TrayOpen:
                ShowAndActivate();
                break;
            case TrayGaming:
                ShowAndActivate();
                _navigation.Navigate(PageKeys.Gaming);
                break;
            case TrayOptimize:
                ShowAndActivate();
                _navigation.Navigate(PageKeys.Optimization);
                break;
            case TraySettings:
                ShowAndActivate();
                _navigation.Navigate(PageKeys.Settings);
                break;
            case TrayPause:
                if (_monitor.Mode == MonitoringMode.Paused)
                {
                    _monitor.SetMode(_modeBeforePause == MonitoringMode.Paused ? MonitoringMode.Background : _modeBeforePause);
                }
                else
                {
                    _modeBeforePause = _monitor.Mode;
                    _monitor.SetMode(MonitoringMode.Paused);
                }
                UpdateTrayToolTip();
                break;
            case TrayExit:
                _ = ExitAsync();
                break;
        }
    }

    private void UpdateTrayToolTip()
        => _tray?.SetToolTip(Str.Get(_monitor.Mode == MonitoringMode.Paused ? "App_Tray_ToolTipPaused" : "App_Tray_ToolTip"));
}
