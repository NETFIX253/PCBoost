using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

/// <summary>
/// Paramètres (§44–46, §30, §34) : Général, Gaming, Sécurité, Diagnostic, Mises à jour.
/// Chaque modification est enregistrée immédiatement via <see cref="ISettingsService"/>.
/// Les listes de choix se lient par index (SelectedIndex, TwoWay).
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private static readonly int[] RetentionDays = [30, 90, 180, 365];
    private readonly ISettingsService _settings;
    private readonly IAutoStartRegistration _autoStart;
    private readonly IThemeService _theme;
    private readonly IUpdateService _updates;
    private readonly IShellService _shell;
    private readonly IAppInfo _appInfo;
    private readonly IGameDetectionService _games;
    private readonly List<string?> _preferredGameIds = [null];
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private UpdateInfo? _availableUpdate;
    private string? _downloadedPackage;
    private bool _loading;

    public SettingsViewModel(
        ViewModelContext context,
        ISettingsService settings,
        IAutoStartRegistration autoStart,
        IThemeService theme,
        IUpdateService updates,
        IShellService shell,
        IAppInfo appInfo,
        IGameDetectionService games)
        : base(context)
    {
        _settings = settings;
        _autoStart = autoStart;
        _theme = theme;
        _updates = updates;
        _shell = shell;
        _appInfo = appInfo;
        _games = games;

        var languages = new List<OptionItem> { new("system", T("Settings_Language_System")) };
        var available = Localizer.AvailableLanguages.Where(l => !string.Equals(l.Code, "system", StringComparison.OrdinalIgnoreCase)).ToList();
        if (available.Count == 0) available = [new("fr", "Français"), new("en", "English")];
        languages.AddRange(available.Select(l => new OptionItem(l.Code, l.NativeName)));
        LanguageOptions = languages;
        ThemeOptions =
        [
            new OptionItem(nameof(ThemePreference.System), T("Settings_Theme_System")),
            new OptionItem(nameof(ThemePreference.Light), T("Settings_Theme_Light")),
            new OptionItem(nameof(ThemePreference.Dark), T("Settings_Theme_Dark")),
        ];
        AutoGamingOptions =
        [
            new OptionItem(nameof(AutoGamingBehavior.Off), T("Settings_AutoGaming_Off")),
            new OptionItem(nameof(AutoGamingBehavior.Ask), T("Settings_AutoGaming_Ask")),
            new OptionItem(nameof(AutoGamingBehavior.Automatic), T("Settings_AutoGaming_Automatic")),
        ];
        RetentionOptions = RetentionDays.Select(d => new OptionItem(d.ToString(Localizer.Culture), T("Settings_Retention_Days", d))).ToList();
        PreferredGameOptions = [new OptionItem(string.Empty, T("Settings_PreferredGame_None"))];
        LogDirectory = appInfo.LogDirectory;
        CurrentVersionText = T("Settings_Update_CurrentVersion", VersionFormat.Short(appInfo.Version));
        UpdateStatusText = T("Settings_Update_NotChecked");
    }

    // --- Général ---
    public IReadOnlyList<OptionItem> LanguageOptions { get; }

    [ObservableProperty]
    public partial int SelectedLanguageIndex { get; set; }

    [ObservableProperty]
    public partial bool LaunchAtStartup { get; set; }

    public IReadOnlyList<OptionItem> ThemeOptions { get; }

    [ObservableProperty]
    public partial int SelectedThemeIndex { get; set; }

    [ObservableProperty]
    public partial bool NotificationsEnabled { get; set; }

    [ObservableProperty]
    public partial bool MonitoringEnabled { get; set; }

    [ObservableProperty]
    public partial bool AllowAutomaticSafeOptimizations { get; set; }

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    [ObservableProperty]
    public partial bool ExpertMode { get; set; }

    // --- Gaming ---
    public IReadOnlyList<OptionItem> AutoGamingOptions { get; }

    [ObservableProperty]
    public partial int SelectedAutoGamingIndex { get; set; }

    [ObservableProperty]
    public partial bool GamingAutoRestore { get; set; }

    [ObservableProperty]
    public partial bool GamingMonitorDuringSession { get; set; }

    /// <summary>Mesure des FPS (autorisation administrateur ponctuelle au démarrage de la mesure).</summary>
    [ObservableProperty]
    public partial bool GamingMeasureFrameRate { get; set; }

    [ObservableProperty]
    public partial bool GamingSwitchPowerPlan { get; set; }

    [ObservableProperty]
    public partial bool GamingRaisePriority { get; set; }

    [ObservableProperty]
    public partial bool GamingThrottleBackgroundApps { get; set; }

    /// <summary>« Aucun » puis les jeux détectés.</summary>
    public ObservableCollection<OptionItem> PreferredGameOptions { get; }

    [ObservableProperty]
    public partial int SelectedPreferredGameIndex { get; set; }

    // --- Sécurité ---
    [ObservableProperty]
    public partial bool ConfirmSensitiveOperations { get; set; }

    [ObservableProperty]
    public partial bool VerboseLogging { get; set; }

    public IReadOnlyList<OptionItem> RetentionOptions { get; }

    [ObservableProperty]
    public partial int SelectedRetentionIndex { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreDismissedRecommendationsCommand))]
    public partial int DismissedRecommendationCount { get; private set; }

    /// <summary>« 2 recommandations masquées » / « Aucune recommandation masquée ».</summary>
    [ObservableProperty]
    public partial string DismissedRecommendationsText { get; private set; } = string.Empty;

    // --- Diagnostic ---
    public string LogDirectory { get; }

    // --- Mises à jour ---
    public string CurrentVersionText { get; }

    [ObservableProperty]
    public partial string UpdateStatusText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(DownloadUpdateCommand))]
    public partial bool IsCheckingUpdates { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    public partial bool IsUpdateAvailable { get; private set; }

    /// <summary>Mises à jour non configurées pour cette version (affiché honnêtement).</summary>
    [ObservableProperty]
    public partial bool IsUpdateNotConfigured { get; private set; }

    [ObservableProperty]
    public partial string ReleaseNotes { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(DownloadUpdateCommand))]
    public partial bool IsDownloadingUpdate { get; private set; }

    [ObservableProperty]
    public partial double DownloadProgressPercent { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    public partial bool IsUpdateReadyToInstall { get; private set; }

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        LoadFromSettings(_settings.Current);
        await LoadGamesAsync(cancellationToken).ConfigureAwait(true);
    }

    private void LoadFromSettings(AppSettings s)
    {
        _loading = true;
        try
        {
            var languageIndex = LanguageOptions.ToList().FindIndex(o => string.Equals(o.Key, s.Language, StringComparison.OrdinalIgnoreCase));
            SelectedLanguageIndex = Math.Max(0, languageIndex);
            bool autoStartActual;
            try
            {
                autoStartActual = _autoStart.IsEnabled();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                autoStartActual = s.LaunchAtStartup;
            }

            LaunchAtStartup = autoStartActual;
            SelectedThemeIndex = (int)s.Theme;
            NotificationsEnabled = s.NotificationsEnabled;
            MonitoringEnabled = s.MonitoringEnabled;
            AllowAutomaticSafeOptimizations = s.AllowAutomaticSafeOptimizations;
            MinimizeToTray = s.MinimizeToTray;
            ExpertMode = s.ExpertMode;
            SelectedAutoGamingIndex = (int)s.Gaming.AutoActivation;
            GamingAutoRestore = s.Gaming.AutoRestore;
            GamingMonitorDuringSession = s.Gaming.MonitorDuringSession;
            GamingMeasureFrameRate = s.Gaming.MeasureFrameRate;
            GamingSwitchPowerPlan = s.Gaming.SwitchPowerPlan;
            GamingRaisePriority = s.Gaming.RaiseGamePriority;
            GamingThrottleBackgroundApps = s.Gaming.ThrottleBackgroundApps;
            ConfirmSensitiveOperations = s.ConfirmSensitiveOperations;
            VerboseLogging = s.VerboseLogging;
            var retention = Array.IndexOf(RetentionDays, s.HistoryRetentionDays);
            SelectedRetentionIndex = retention >= 0 ? retention : 1;
            UpdateDismissed(s);
            SelectedPreferredGameIndex = Math.Max(0, _preferredGameIds.IndexOf(s.Gaming.PreferredGameId));
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadGamesAsync(CancellationToken cancellationToken)
    {
        await RunSafeAsync(async ct =>
        {
            var games = await _games.GetInstalledGamesAsync(false, ct).ConfigureAwait(true);
            _loading = true;
            try
            {
                while (PreferredGameOptions.Count > 1) PreferredGameOptions.RemoveAt(PreferredGameOptions.Count - 1);
                _preferredGameIds.RemoveRange(1, _preferredGameIds.Count - 1);
                foreach (var g in games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    PreferredGameOptions.Add(new OptionItem(g.Id, g.Name));
                    _preferredGameIds.Add(g.Id);
                }

                SelectedPreferredGameIndex = Math.Max(0, _preferredGameIds.IndexOf(_settings.Current.Gaming.PreferredGameId));
            }
            finally
            {
                _loading = false;
            }
        }, cancellationToken, trackBusy: false).ConfigureAwait(true);
    }

    /// <summary>Enregistrement immédiat, sérialisé (chaque modification part de l'état le plus récent).</summary>
    private void Save(Action<AppSettings> mutate)
    {
        if (_loading) return;
        _ = RunSafeAsync(async ct =>
        {
            await _saveLock.WaitAsync(ct).ConfigureAwait(true);
            try
            {
                await _settings.UpdateAsync(mutate, ct).ConfigureAwait(true);
            }
            finally
            {
                _saveLock.Release();
            }
        }, trackBusy: false, linkToPage: false);
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (_loading || value < 0 || value >= LanguageOptions.Count) return;
        var code = LanguageOptions[value].Key;
        Save(s => s.Language = code);
        Localizer.SetLanguage(code);
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_loading) return;
        var result = _autoStart.SetEnabled(value);
        if (!CheckResult(result))
        {
            _loading = true;
            LaunchAtStartup = !value;
            _loading = false;
            return;
        }

        Save(s => s.LaunchAtStartup = value);
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (_loading || value < 0 || value > 2) return;
        var theme = (ThemePreference)value;
        _theme.Apply(theme);
        Save(s => s.Theme = theme);
    }

    partial void OnNotificationsEnabledChanged(bool value) => Save(s => s.NotificationsEnabled = value);

    partial void OnMonitoringEnabledChanged(bool value) => Save(s => s.MonitoringEnabled = value);

    partial void OnAllowAutomaticSafeOptimizationsChanged(bool value) => Save(s => s.AllowAutomaticSafeOptimizations = value);

    partial void OnMinimizeToTrayChanged(bool value) => Save(s => s.MinimizeToTray = value);

    partial void OnExpertModeChanged(bool value) => Save(s => s.ExpertMode = value);

    partial void OnSelectedAutoGamingIndexChanged(int value)
    {
        if (value < 0 || value > 2) return;
        Save(s => s.Gaming.AutoActivation = (AutoGamingBehavior)value);
    }

    partial void OnGamingAutoRestoreChanged(bool value) => Save(s => s.Gaming.AutoRestore = value);

    partial void OnGamingMonitorDuringSessionChanged(bool value) => Save(s => s.Gaming.MonitorDuringSession = value);

    partial void OnGamingMeasureFrameRateChanged(bool value) => Save(s => s.Gaming.MeasureFrameRate = value);

    partial void OnGamingSwitchPowerPlanChanged(bool value) => Save(s => s.Gaming.SwitchPowerPlan = value);

    partial void OnGamingRaisePriorityChanged(bool value) => Save(s => s.Gaming.RaiseGamePriority = value);

    partial void OnGamingThrottleBackgroundAppsChanged(bool value) => Save(s => s.Gaming.ThrottleBackgroundApps = value);

    partial void OnSelectedPreferredGameIndexChanged(int value)
    {
        if (value < 0 || value >= _preferredGameIds.Count) return;
        var id = _preferredGameIds[value];
        Save(s => s.Gaming.PreferredGameId = id);
    }

    partial void OnConfirmSensitiveOperationsChanged(bool value) => Save(s => s.ConfirmSensitiveOperations = value);

    partial void OnVerboseLoggingChanged(bool value) => Save(s => s.VerboseLogging = value);

    partial void OnSelectedRetentionIndexChanged(int value)
    {
        if (value < 0 || value >= RetentionDays.Length) return;
        var days = RetentionDays[value];
        Save(s => s.HistoryRetentionDays = days);
    }

    /// <summary>Réaffiche les recommandations masquées.</summary>
    [RelayCommand(CanExecute = nameof(HasDismissedRecommendations))]
    private async Task RestoreDismissedRecommendationsAsync()
    {
        var ok = await RunSafeAsync(async ct =>
        {
            await _saveLock.WaitAsync(ct).ConfigureAwait(true);
            try
            {
                await _settings.UpdateAsync(s => s.DismissedRecommendations.Clear(), ct).ConfigureAwait(true);
            }
            finally
            {
                _saveLock.Release();
            }
        }, linkToPage: false).ConfigureAwait(true);
        if (!ok) return;
        UpdateDismissed(_settings.Current);
        StatusMessage = T("Settings_Recommendations_Restored");
    }

    private bool HasDismissedRecommendations() => DismissedRecommendationCount > 0;

    [RelayCommand]
    private void OpenLogFolder() => CheckResult(_shell.OpenFolder(_appInfo.LogDirectory));

    [RelayCommand]
    private void OpenPrivacy() => Navigation.Navigate(PageKeys.Privacy);

    [RelayCommand]
    private void OpenAbout() => Navigation.Navigate(PageKeys.About);

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        IsCheckingUpdates = true;
        UpdateStatusText = T("Settings_Update_Checking");
        try
        {
            var ok = await RunSafeAsync(async ct =>
            {
                var result = await _updates.CheckForUpdatesAsync(ct).ConfigureAwait(true);
                ApplyUpdateResult(result);
            }, cancellationToken, trackBusy: false).ConfigureAwait(true);
            if (!ok) UpdateStatusText = T("Settings_Update_Failed");
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    private bool CanCheckForUpdates() => !IsCheckingUpdates && !IsDownloadingUpdate;

    internal void ApplyUpdateResult(UpdateCheckResult result)
    {
        _availableUpdate = result.Status == UpdateCheckStatus.UpdateAvailable ? result.Update : null;
        IsUpdateAvailable = _availableUpdate is not null;
        IsUpdateNotConfigured = result.Status == UpdateCheckStatus.NotConfigured;
        IsUpdateReadyToInstall = false;
        _downloadedPackage = null;
        ReleaseNotes = _availableUpdate?.ReleaseNotes ?? string.Empty;
        UpdateStatusText = result.Status switch
        {
            UpdateCheckStatus.UpToDate => T("Settings_Update_UpToDate"),
            UpdateCheckStatus.UpdateAvailable when result.Update is { } u => T("Settings_Update_Available", VersionFormat.Short(u.Version)),
            UpdateCheckStatus.NotConfigured => T("Settings_Update_NotConfigured"),
            _ => result.Message is { } m ? T(m) : T("Settings_Update_Failed"),
        };
    }

    /// <summary>Télécharge la mise à jour et vérifie son empreinte SHA-256.</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadUpdate))]
    private async Task DownloadUpdateAsync(CancellationToken cancellationToken)
    {
        if (_availableUpdate is not { } update) return;
        IsDownloadingUpdate = true;
        DownloadProgressPercent = 0;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var progress = UiProgress<double>(p => DownloadProgressPercent = Math.Clamp(p <= 1 ? p * 100 : p, 0, 100));
                var result = await _updates.DownloadAndVerifyAsync(update, progress, ct).ConfigureAwait(true);
                if (!CheckResult(result.Outcome) || result.LocalPath is null)
                {
                    UpdateStatusText = T("Settings_Update_DownloadFailed");
                    return;
                }

                _downloadedPackage = result.LocalPath;
                IsUpdateReadyToInstall = true;
                UpdateStatusText = T("Settings_Update_Ready", VersionFormat.Short(update.Version));
            }, cancellationToken, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    private bool CanDownloadUpdate() => IsUpdateAvailable && !IsDownloadingUpdate && !IsCheckingUpdates;

    [RelayCommand(CanExecute = nameof(IsUpdateReadyToInstall))]
    private void InstallUpdate()
    {
        if (_downloadedPackage is { } path) CheckResult(_updates.LaunchInstaller(path));
    }

    private void UpdateDismissed(AppSettings s)
    {
        DismissedRecommendationCount = s.DismissedRecommendations.Count;
        DismissedRecommendationsText = s.DismissedRecommendations.Count switch
        {
            0 => T("Settings_Recommendations_NoneDismissed"),
            1 => T("Settings_Recommendations_OneDismissed"),
            var n => T("Settings_Recommendations_Dismissed", n),
        };
    }
}
