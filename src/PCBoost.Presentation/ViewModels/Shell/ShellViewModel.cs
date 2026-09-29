using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Branding;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

/// <summary>
/// Fenêtre principale : navigation, pied (version, protection, liens), bandeaux (récupération après interruption,
/// jeu détecté), indicateurs (mode Gaming, profil actif, mode Expert). Singleton : appeler <see cref="InitializeAsync"/> une fois.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly IAppInfo _appInfo;
    private readonly IRollbackManager _rollback;
    private readonly IRecoveryManager _recovery;
    private readonly IAutoGamingMode _autoGaming;
    private readonly IGamingService _gaming;
    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;
    private readonly IShellService _shell;
    private readonly BrandingOptions _branding;
    private IReadOnlyList<OptimizationSession> _interrupted = [];
    private DetectedGameProcess? _suggestedGame;
    private bool _initialized;
    private bool _disposed;

    public ShellViewModel(
        ViewModelContext context,
        IAppInfo appInfo,
        IRollbackManager rollback,
        IRecoveryManager recovery,
        IAutoGamingMode autoGaming,
        IGamingService gaming,
        IProfileService profiles,
        ISettingsService settings,
        IShellService shell,
        BrandingOptions? branding = null)
        : base(context)
    {
        _appInfo = appInfo;
        _rollback = rollback;
        _recovery = recovery;
        _autoGaming = autoGaming;
        _gaming = gaming;
        _profiles = profiles;
        _settings = settings;
        _shell = shell;
        _branding = branding ?? new BrandingOptions();

        NavigationItems =
        [
            new(PageKeys.Home, "Nav_Home", "Nav_Home_ToolTip", Glyphs.Home),
            new(PageKeys.Analysis, "Nav_Analysis", "Nav_Analysis_ToolTip", Glyphs.Analysis),
            new(PageKeys.Optimization, "Nav_Optimization", "Nav_Optimization_ToolTip", Glyphs.Optimization),
            new(PageKeys.Cleanup, "Nav_Cleanup", "Nav_Cleanup_ToolTip", Glyphs.Cleanup),
            new(PageKeys.Startup, "Nav_Startup", "Nav_Startup_ToolTip", Glyphs.Startup),
            new(PageKeys.Processes, "Nav_Processes", "Nav_Processes_ToolTip", Glyphs.Processes),
            new(PageKeys.Gaming, "Nav_Gaming", "Nav_Gaming_ToolTip", Glyphs.Gaming),
            new(PageKeys.Performance, "Nav_Performance", "Nav_Performance_ToolTip", Glyphs.Performance),
            new(PageKeys.History, "Nav_History", "Nav_History_ToolTip", Glyphs.History),
        ];
        SettingsItem = new(PageKeys.Settings, "Nav_Settings", "Nav_Settings_ToolTip", Glyphs.Settings);
        ExpertItem = new(PageKeys.Expert, "Nav_Expert", "Nav_Expert_ToolTip", Glyphs.Expert);
        RefreshTexts();
    }

    /// <summary>Menu principal : Accueil, Analyse, Optimisation, Nettoyage, Démarrage, Processus, Gaming, Performances, Historique.</summary>
    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    /// <summary>Élément de pied de menu « Paramètres ».</summary>
    public NavigationItemViewModel SettingsItem { get; }

    /// <summary>Élément de pied de menu « Mode Expert » (visible si <see cref="IsExpertModeVisible"/>).</summary>
    public NavigationItemViewModel ExpertItem { get; }

    /// <summary>Levé après un changement de langue : l'App doit recharger la page courante (ViewModels Transient).</summary>
    public event EventHandler? PageReloadRequested;

    [ObservableProperty]
    public partial string? CurrentPageKey { get; private set; }

    [ObservableProperty]
    public partial string CurrentPageTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ProductName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Slogan { get; private set; } = string.Empty;

    /// <summary>« Version 1.0.0 ».</summary>
    [ObservableProperty]
    public partial string VersionText { get; private set; } = string.Empty;

    /// <summary>« Restauration activée ».</summary>
    [ObservableProperty]
    public partial string ProtectionStatusText { get; private set; } = string.Empty;

    /// <summary>« 3 modifications réversibles en vigueur » / « Aucune modification en vigueur ».</summary>
    [ObservableProperty]
    public partial string ProtectionDetailText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int ReversibleChangeCount { get; private set; }

    public string ProtectionGlyph => Glyphs.Shield;

    public bool HasSupportLink => Uri.TryCreate(_branding.SupportUrl, UriKind.Absolute, out _);

    [ObservableProperty]
    public partial bool IsExpertModeVisible { get; private set; }

    // --- Bandeau de récupération ---
    [ObservableProperty]
    public partial bool IsRecoveryBannerVisible { get; private set; }

    /// <summary>« Une optimisation n'a pas été terminée. Restaurer les paramètres précédents ? »</summary>
    [ObservableProperty]
    public partial string RecoveryMessage { get; private set; } = string.Empty;

    /// <summary>Détail : date et titre de la ou des sessions interrompues.</summary>
    [ObservableProperty]
    public partial string RecoveryDetail { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecoverCommand), nameof(KeepChangesCommand))]
    public partial bool IsRecovering { get; private set; }

    // --- Bandeau « Jeu détecté » ---
    [ObservableProperty]
    public partial bool IsGameBannerVisible { get; private set; }

    /// <summary>« Jeu détecté : {nom} ».</summary>
    [ObservableProperty]
    public partial string GameBannerTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string GameBannerMessage { get; private set; } = string.Empty;

    // --- Indicateurs ---
    [ObservableProperty]
    public partial bool IsGamingModeActive { get; private set; }

    /// <summary>« Mode Gaming actif » (+ nom du jeu) ; vide si inactif.</summary>
    [ObservableProperty]
    public partial string GamingStatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasActiveProfile { get; private set; }

    /// <summary>« Profil : Gaming ».</summary>
    [ObservableProperty]
    public partial string ActiveProfileText { get; private set; } = string.Empty;

    public string GamingGlyph => Glyphs.Gaming;

    /// <summary>À appeler une fois au démarrage de la fenêtre principale.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        Navigation.Navigated += OnNavigated;
        Localizer.LanguageChanged += OnLanguageChanged;
        _settings.SettingsChanged += OnSettingsChanged;
        _rollback.SessionChanged += OnSessionChanged;
        _autoGaming.ActivationSuggested += OnActivationSuggested;
        _gaming.StateChanged += OnGamingStateChanged;
        _profiles.StateChanged += OnProfileStateChanged;

        IsExpertModeVisible = _settings.Current.ExpertMode;
        UpdateGamingIndicator();
        UpdateProfileIndicator();
        if (Navigation.CurrentPageKey is { } current) SelectPage(current);

        await RunSafeAsync(async ct =>
        {
            await RefreshProtectionAsync(ct).ConfigureAwait(true);
            _interrupted = await _recovery.FindInterruptedSessionsAsync(ct).ConfigureAwait(true);
            UpdateRecoveryBanner();
        }, trackBusy: false, linkToPage: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Navigate(string? pageKey)
    {
        if (string.IsNullOrWhiteSpace(pageKey)) return;
        if (Navigation.Navigate(pageKey)) SelectPage(pageKey);
    }

    [RelayCommand]
    private void OpenJournal()
    {
        if (Navigation.Navigate(PageKeys.History, PageKeys.Journal)) SelectPage(PageKeys.History);
    }

    [RelayCommand]
    private void OpenPrivacy() => Navigate(PageKeys.Privacy);

    [RelayCommand]
    private void OpenAbout() => Navigate(PageKeys.About);

    [RelayCommand]
    private void OpenSupport()
    {
        if (Uri.TryCreate(_branding.SupportUrl, UriKind.Absolute, out var uri)) CheckResult(_shell.OpenUri(uri));
    }

    [RelayCommand(CanExecute = nameof(CanUseRecoveryBanner))]
    private async Task RecoverAsync()
    {
        IsRecovering = true;
        try
        {
            await RunSafeAsync(async ct =>
            {
                int restored = 0, failed = 0;
                foreach (var session in _interrupted)
                {
                    var result = await _recovery.RecoverAsync(session.Id, ct).ConfigureAwait(true);
                    restored += result.Restored;
                    failed += result.Failed;
                }

                _interrupted = [];
                UpdateRecoveryBanner();
                StatusMessage = failed == 0
                    ? T("Shell_Recovery_Done", restored)
                    : T("Shell_Recovery_Partial", restored, failed);
                await RefreshProtectionAsync(ct).ConfigureAwait(true);
            }, linkToPage: false).ConfigureAwait(true);
        }
        finally
        {
            IsRecovering = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseRecoveryBanner))]
    private async Task KeepChangesAsync()
    {
        IsRecovering = true;
        try
        {
            await RunSafeAsync(async ct =>
            {
                foreach (var session in _interrupted)
                    await _recovery.DismissAsync(session.Id, ct).ConfigureAwait(true);
                _interrupted = [];
                UpdateRecoveryBanner();
                StatusMessage = T("Shell_Recovery_Kept");
            }, linkToPage: false).ConfigureAwait(true);
        }
        finally
        {
            IsRecovering = false;
        }
    }

    private bool CanUseRecoveryBanner() => !IsRecovering;

    [RelayCommand]
    private async Task ActivateSuggestedGamingAsync()
    {
        var game = _suggestedGame;
        IsGameBannerVisible = false;
        _suggestedGame = null;
        await RunSafeAsync(async ct =>
        {
            var report = await _gaming.ActivateAsync(game, ct).ConfigureAwait(true);
            if (CheckResult(report.Outcome)) StatusMessage = T("Shell_GameBanner_Activated");
            UpdateGamingIndicator();
        }, linkToPage: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private void IgnoreSuggestedGame()
    {
        IsGameBannerVisible = false;
        _suggestedGame = null;
    }

    [RelayCommand]
    private void OpenGaming() => Navigate(PageKeys.Gaming);

    private void OnNavigated(object? sender, string pageKey) => OnUi(() => SelectPage(pageKey));

    private void SelectPage(string pageKey)
    {
        // Pages secondaires rattachées à une entrée du menu.
        var menuKey = pageKey switch
        {
            PageKeys.Diagnosis or PageKeys.Storage => PageKeys.Analysis,
            PageKeys.OldPc or PageRegistry.Profiles => PageKeys.Optimization,
            PageKeys.Benchmark => PageKeys.Gaming,
            PageKeys.Journal => PageKeys.History,
            PageKeys.Privacy or PageKeys.About => PageKeys.Settings,
            _ => pageKey,
        };

        CurrentPageKey = pageKey;
        foreach (var item in NavigationItems) item.IsSelected = item.PageKey == menuKey;
        SettingsItem.IsSelected = SettingsItem.PageKey == menuKey;
        ExpertItem.IsSelected = ExpertItem.PageKey == menuKey;
        var titleKey = PageRegistry.TitleKeyFor(pageKey);
        CurrentPageTitle = titleKey is null ? string.Empty : T(titleKey);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        RefreshTexts();
        UpdateGamingIndicator();
        UpdateProfileIndicator();
        UpdateRecoveryBanner();
        UpdateProtectionTexts();
        if (CurrentPageKey is { } key) SelectPage(key);
        PageReloadRequested?.Invoke(this, EventArgs.Empty);
    });

    private void OnSettingsChanged(object? sender, AppSettings settings) => OnUi(() =>
    {
        IsExpertModeVisible = settings.ExpertMode;
    });

    private void OnSessionChanged(object? sender, Guid sessionId)
        => OnUi(() => _ = RunSafeAsync(RefreshProtectionAsync, trackBusy: false, linkToPage: false));

    private void OnActivationSuggested(object? sender, DetectedGameProcess game) => OnUi(() =>
    {
        if (_gaming.State != GamingState.Inactive) return;
        _suggestedGame = game;
        GameBannerTitle = T("Shell_GameBanner_Title", game.Game.Name);
        GameBannerMessage = T("Shell_GameBanner_Message");
        IsGameBannerVisible = true;
    });

    private void OnGamingStateChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        UpdateGamingIndicator();
        UpdateProtectionTexts();
        // La fin d'une session Gaming modifie l'historique restaurable.
        if (_gaming.State == GamingState.Inactive) _ = RunSafeAsync(RefreshProtectionAsync, trackBusy: false, linkToPage: false);
    });

    private void OnProfileStateChanged(object? sender, EventArgs e) => OnUi(UpdateProfileIndicator);

    private void RefreshTexts()
    {
        foreach (var item in NavigationItems) item.Refresh(T);
        SettingsItem.Refresh(T);
        ExpertItem.Refresh(T);
        ProductName = string.IsNullOrWhiteSpace(_appInfo.ProductName) ? _branding.ProductName : _appInfo.ProductName;
        Slogan = _branding.GetSlogan(Localizer.Culture.TwoLetterISOLanguageName);
        VersionText = T("Shell_Version", VersionFormat.Short(_appInfo.Version));
        ProtectionStatusText = T("Shell_Protection_Enabled");
    }

    private async Task RefreshProtectionAsync(CancellationToken cancellationToken)
    {
        var sessions = await _rollback.GetHistoryAsync(100, cancellationToken).ConfigureAwait(true);
        ReversibleChangeCount = sessions
            .Where(s => s.Status is SessionStatus.Completed or SessionStatus.PartiallyCompleted or SessionStatus.PartiallyRolledBack or SessionStatus.Dismissed)
            .Where(_rollback.CanRollback)
            .Sum(s => s.ReversibleChangeCount);
        UpdateProtectionTexts();
    }

    private void UpdateProtectionTexts()
    {
        // Pendant une session Gaming, ses réglages temporaires sont en vigueur (restaurés à la désactivation).
        if (_gaming.State is GamingState.Active or GamingState.Activating)
        {
            ProtectionDetailText = T("Shell_Protection_GamingActive");
            return;
        }
        ProtectionDetailText = ReversibleChangeCount switch
        {
            0 => T("Shell_Protection_NoChanges"),
            1 => T("Shell_Protection_OneChange"),
            var n => T("Shell_Protection_Changes", n),
        };
    }

    private void UpdateRecoveryBanner()
    {
        IsRecoveryBannerVisible = _interrupted.Count > 0;
        if (_interrupted.Count == 0)
        {
            RecoveryMessage = string.Empty;
            RecoveryDetail = string.Empty;
            return;
        }

        RecoveryMessage = T("Shell_Recovery_Message");
        RecoveryDetail = string.Join(Environment.NewLine, _interrupted.Select(s => T("Shell_Recovery_SessionLine",
            Formatter.DateTimeFull(s.StartedAt),
            s.Title is null ? T($"History_Type_{s.Type}") : T(s.Title))));
    }

    private void UpdateGamingIndicator()
    {
        var state = _gaming.State;
        IsGamingModeActive = state is GamingState.Active or GamingState.Activating;
        if (!IsGamingModeActive)
        {
            GamingStatusText = string.Empty;
            return;
        }

        var game = _gaming.CurrentGame?.Game.Name ?? _gaming.CurrentSession?.GameName;
        GamingStatusText = game is null ? T("Shell_Gaming_Active") : T("Shell_Gaming_ActiveWithGame", game);
        if (IsGameBannerVisible) IsGameBannerVisible = false;
    }

    private void UpdateProfileIndicator()
    {
        var activeId = _profiles.State.ActiveProfileId;
        var profile = activeId is null ? null : _profiles.GetProfiles().FirstOrDefault(p => p.Id == activeId);
        HasActiveProfile = profile is not null;
        ActiveProfileText = profile is null ? string.Empty : T("Shell_ActiveProfile", T(profile.Name));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_initialized) return;
        Navigation.Navigated -= OnNavigated;
        Localizer.LanguageChanged -= OnLanguageChanged;
        _settings.SettingsChanged -= OnSettingsChanged;
        _rollback.SessionChanged -= OnSessionChanged;
        _autoGaming.ActivationSuggested -= OnActivationSuggested;
        _gaming.StateChanged -= OnGamingStateChanged;
        _profiles.StateChanged -= OnProfileStateChanged;
    }
}
