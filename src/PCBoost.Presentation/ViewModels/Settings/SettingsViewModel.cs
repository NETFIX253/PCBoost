using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
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
    private readonly IAutoUpdateService _updates;
    private readonly IShellService _shell;
    private readonly IAppInfo _appInfo;
    private readonly IGameDetectionService _games;
    private readonly List<string?> _preferredGameIds = [null];
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _loading;

    public SettingsViewModel(
        ViewModelContext context,
        ISettingsService settings,
        IAutoStartRegistration autoStart,
        IThemeService theme,
        IAutoUpdateService updates,
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
        ApplyUpdateState(updates.State);
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
    public partial bool CreateRestorePointBeforeAdvanced { get; set; }

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

    /// <summary>Mises à jour automatiques (vérification, téléchargement vérifié, installation à la fermeture).</summary>
    [ObservableProperty]
    public partial bool AutoUpdate { get; set; }

    /// <summary>Réglage imposé par une stratégie de l'organisation (interrupteur désactivé).</summary>
    [ObservableProperty]
    public partial bool IsAutoUpdatePolicy { get; private set; }

    public bool CanChangeAutoUpdate => !IsAutoUpdatePolicy;

    [ObservableProperty]
    public partial string UpdateStatusText { get; private set; }

    /// <summary>« Dernière vérification : … » (vide si aucune).</summary>
    [ObservableProperty]
    public partial string LastUpdateCheckText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(DownloadUpdateCommand))]
    public partial bool IsCheckingUpdates { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    public partial bool IsUpdateAvailable { get; private set; }

    /// <summary>Aucune source de mise à jour pour cette copie (affiché honnêtement).</summary>
    [ObservableProperty]
    public partial bool IsUpdateNotConfigured { get; private set; }

    /// <summary>Nouveautés de la version proposée (ou de la version installée juste après une mise à jour).</summary>
    [ObservableProperty]
    public partial string ReleaseNotes { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ReleaseNotesHeader { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(DownloadUpdateCommand))]
    public partial bool IsDownloadingUpdate { get; private set; }

    [ObservableProperty]
    public partial double DownloadProgressPercent { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    public partial bool IsUpdateReadyToInstall { get; private set; }

    /// <summary>Copie portable : la nouvelle version se télécharge depuis la page de la version.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReleasePageCommand))]
    public partial bool CanOpenReleasePage { get; private set; }

    private Uri? _releasePage;

    private void OnUpdateStateChanged(object? sender, AutoUpdateState state) => OnUi(() => ApplyUpdateState(state));

    internal void ApplyUpdateState(AutoUpdateState state)
    {
        var version = state.Update is { } u ? VersionFormat.Short(u.Version) : string.Empty;
        IsCheckingUpdates = state.Phase == AutoUpdatePhase.Checking;
        IsDownloadingUpdate = state.Phase == AutoUpdatePhase.Downloading;
        DownloadProgressPercent = state.DownloadPercent ?? 0;
        IsUpdateAvailable = state.Phase is AutoUpdatePhase.Available or AutoUpdatePhase.Failed && state.Update is not null && state.IsInstalledCopy;
        IsUpdateReadyToInstall = state.CanInstall;
        IsUpdateNotConfigured = state.Phase == AutoUpdatePhase.NotConfigured;
        _releasePage = state.Update?.ReleaseUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
        CanOpenReleasePage = !state.IsInstalledCopy && _releasePage is not null && state.Phase is AutoUpdatePhase.Available;
        LastUpdateCheckText = state.LastCheckedAt is { } at ? T("Settings_Update_LastCheck", Formatter.DateTimeFull(at)) : string.Empty;

        UpdateStatusText = state.Phase switch
        {
            AutoUpdatePhase.Checking => T("Settings_Update_Checking"),
            AutoUpdatePhase.UpToDate => T("Settings_Update_UpToDate"),
            AutoUpdatePhase.Available when !state.IsInstalledCopy => T("Settings_Update_AvailablePortable", version),
            AutoUpdatePhase.Available when state.IsMetered && _updates.IsAutomatic => T("Settings_Update_AvailableMetered", version),
            AutoUpdatePhase.Available => T("Settings_Update_Available", version),
            AutoUpdatePhase.Downloading => T("Settings_Update_Downloading", version),
            AutoUpdatePhase.ReadyToInstall when state.InstallBlocked => T("Settings_Update_ReadyBlocked", version),
            AutoUpdatePhase.ReadyToInstall when _updates.IsAutomatic => T("Settings_Update_ReadyAtClose", version),
            AutoUpdatePhase.ReadyToInstall => T("Settings_Update_Ready", version),
            AutoUpdatePhase.NotConfigured => T("Settings_Update_NotConfigured"),
            AutoUpdatePhase.Failed => state.Message is { } m ? T(m) : T("Settings_Update_Failed"),
            _ => T("Settings_Update_NotChecked"),
        };

        // Nouveautés : version proposée, sinon version installée par la dernière mise à jour.
        if (state.Update is { ReleaseNotes: { Length: > 0 } notes } update && state.Phase is not (AutoUpdatePhase.UpToDate or AutoUpdatePhase.NotConfigured))
        {
            ReleaseNotes = notes;
            ReleaseNotesHeader = T("Settings_Update_NotesOf", VersionFormat.Short(update.Version));
        }
        else if (_updates.InstalledReleaseNotes is { Length: > 0 } installed)
        {
            ReleaseNotes = installed;
            ReleaseNotesHeader = T("Settings_Update_NotesInstalled", VersionFormat.Short(_appInfo.Version));
        }
        else
        {
            ReleaseNotes = string.Empty;
            ReleaseNotesHeader = string.Empty;
        }
    }

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        LoadFromSettings(_settings.Current);
        _updates.StateChanged -= OnUpdateStateChanged;
        _updates.StateChanged += OnUpdateStateChanged;
        ApplyUpdateState(_updates.State);
        await LoadGamesAsync(cancellationToken).ConfigureAwait(true);
    }

    protected override void OnDeactivated()
    {
        _updates.StateChanged -= OnUpdateStateChanged;
        base.OnDeactivated();
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
            CreateRestorePointBeforeAdvanced = s.CreateRestorePointBeforeAdvanced;
            VerboseLogging = s.VerboseLogging;
            IsAutoUpdatePolicy = _updates.IsDisabledByPolicy;
            AutoUpdate = s.AutoUpdate && !IsAutoUpdatePolicy;
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

    partial void OnCreateRestorePointBeforeAdvancedChanged(bool value) => Save(s => s.CreateRestorePointBeforeAdvanced = value);

    partial void OnVerboseLoggingChanged(bool value) => Save(s => s.VerboseLogging = value);

    partial void OnAutoUpdateChanged(bool value)
    {
        if (IsAutoUpdatePolicy) return;
        Save(s => s.AutoUpdate = value);
    }

    partial void OnIsAutoUpdatePolicyChanged(bool value) => OnPropertyChanged(nameof(CanChangeAutoUpdate));

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
    private void OpenReport() => Navigation.Navigate(PageKeys.Report);

    [RelayCommand]
    private void OpenPrivacy() => Navigation.Navigate(PageKeys.Privacy);

    [RelayCommand]
    private void OpenAbout() => Navigation.Navigate(PageKeys.About);

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        // Non liée à la page : quitter les paramètres n'interrompt pas la vérification (le service gère son état).
        var ok = await RunSafeAsync(ct => _updates.CheckNowAsync(ct), cancellationToken, trackBusy: false, linkToPage: false).ConfigureAwait(true);
        if (!ok) UpdateStatusText = T("Settings_Update_Failed");
    }

    private bool CanCheckForUpdates() => !IsCheckingUpdates && !IsDownloadingUpdate;

    /// <summary>Télécharge la mise à jour et vérifie sa signature, sa taille et son empreinte SHA-256.</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadUpdate))]
    private async Task DownloadUpdateAsync(CancellationToken cancellationToken)
    {
        await RunSafeAsync(ct => _updates.DownloadNowAsync(ct), cancellationToken, trackBusy: false, linkToPage: false).ConfigureAwait(true);
        if (_updates.State.Phase == AutoUpdatePhase.Failed) UpdateStatusText = T("Settings_Update_DownloadFailed");
    }

    private bool CanDownloadUpdate() => IsUpdateAvailable && !IsDownloadingUpdate && !IsCheckingUpdates;

    /// <summary>« Installer maintenant » : PCBoost se ferme, installe la mise à jour puis se relance.</summary>
    [RelayCommand(CanExecute = nameof(IsUpdateReadyToInstall))]
    private async Task InstallUpdateAsync()
    {
        var version = _updates.State.Update is { } u ? VersionFormat.Short(u.Version) : string.Empty;
        var answer = await Dialogs.ConfirmAsync(new ConfirmationRequest(
            TextRef.Of("Settings_Update_InstallConfirm_Title", version),
            TextRef.Of("Settings_Update_InstallConfirm_Message"),
            TextRef.Of("Settings_Action_InstallUpdate"),
            CloseButton: TextRef.Of("Common_Action_Cancel"))).ConfigureAwait(true);
        if (answer != DialogResultKind.Primary) return;
        CheckResult(_updates.RequestInstallNow());
    }

    [RelayCommand(CanExecute = nameof(CanOpenReleasePage))]
    private void OpenReleasePage()
    {
        if (_releasePage is { } page) CheckResult(_shell.OpenUri(page));
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
