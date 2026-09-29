using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Réglage d'un jeu à trois choix : réglage général (valeur rappelée), toujours, jamais.</summary>
public sealed partial class GameSettingOptionViewModel : ObservableObject
{
    private readonly Action<GameSettingOptionViewModel> _changed;
    private bool _loading;

    public GameSettingOptionViewModel(string key, string label, string description, string glyph, bool globalValue, bool? value, ILocalizer localizer, Action<GameSettingOptionViewModel> changed)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        Key = key;
        Label = label;
        Description = description;
        Glyph = glyph;
        _changed = changed;
        Options =
        [
            new OptionItem("default", localizer.Format("GameProfile_Option_Default", localizer.Get(globalValue ? "GameProfile_On" : "GameProfile_Off"))),
            new OptionItem("on", localizer.Get("GameProfile_Option_Always")),
            new OptionItem("off", localizer.Get("GameProfile_Option_Never")),
        ];
        _loading = true;
        SelectedIndex = value switch { true => 1, false => 2, _ => 0 };
        _loading = false;
    }

    public string Key { get; }

    public string Label { get; }

    public string Description { get; }

    public string Glyph { get; }

    public IReadOnlyList<OptionItem> Options { get; }

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    /// <summary>null = réglage général.</summary>
    public bool? Value => SelectedIndex switch { 1 => true, 2 => false, _ => null };

    partial void OnSelectedIndexChanged(int value)
    {
        if (!_loading) _changed(this);
    }
}

/// <summary>Session de jeu passée : date, durée, FPS mesurés (ou « non mesuré »).</summary>
public sealed record GameSessionItemViewModel(string DateText, string DurationText, string FpsText, string LowText, string PointOneLowText, bool IsMeasured)
{
    public string AccessibleName => IsMeasured ? $"{DateText}, {DurationText}, {FpsText}, {LowText}" : $"{DateText}, {DurationText}, {FpsText}";
}

/// <summary>
/// Réglages et historique d'un jeu : les réglages du mode Gaming propres à ce jeu (sinon les réglages généraux) et les
/// FPS mesurés lors des sessions précédentes. Aucune valeur n'est estimée : une session sans mesure est indiquée comme telle.
/// </summary>
public sealed partial class GameProfileViewModel : ViewModelBase
{
    public const int HistoryLimit = 20;

    private readonly ISettingsService _settings;
    private readonly IGameDetectionService _detection;
    private readonly IGamingSessionRepository _sessions;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private IReadOnlyList<GameInfo> _games = [];
    private bool _loadingGame;

    public GameProfileViewModel(ViewModelContext context, ISettingsService settings, IGameDetectionService detection, IGamingSessionRepository sessions)
        : base(context)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _detection = detection ?? throw new ArgumentNullException(nameof(detection));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public ObservableCollection<OptionItem> Games { get; } = [];

    [ObservableProperty]
    public partial int SelectedGameIndex { get; set; } = -1;

    [ObservableProperty]
    public partial bool HasGame { get; private set; }

    [ObservableProperty]
    public partial string GameName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string GameSourceText { get; private set; } = string.Empty;

    public ObservableCollection<GameSettingOptionViewModel> Settings { get; } = [];

    [ObservableProperty]
    public partial bool HasCustomSettings { get; private set; }

    public ObservableCollection<GameSessionItemViewModel> History { get; } = [];

    [ObservableProperty]
    public partial bool HasHistory { get; private set; }

    /// <summary>« Moyenne des 4 sessions mesurées : 72 FPS (1 % low : 51 FPS) ».</summary>
    [ObservableProperty]
    public partial string HistorySummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string NoGameText { get; private set; } = string.Empty;

    public string CurrentGameId => SelectedGameIndex >= 0 && SelectedGameIndex < _games.Count ? _games[SelectedGameIndex].Id : string.Empty;

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        _games = (await _detection.GetInstalledGamesAsync(false, cancellationToken).ConfigureAwait(true))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        CollectionSync.Replace(Games, _games.Select(g => new OptionItem(g.Id, g.Name)));
        NoGameText = _games.Count == 0 ? T("GameProfile_NoGames") : string.Empty;
        var wanted = parameter as string ?? _settings.Current.Gaming.PreferredGameId;
        var index = _games.ToList().FindIndex(g => string.Equals(g.Id, wanted, StringComparison.Ordinal));
        _loadingGame = true;
        SelectedGameIndex = index >= 0 ? index : _games.Count > 0 ? 0 : -1;
        _loadingGame = false;
        await LoadGameAsync(cancellationToken).ConfigureAwait(true);
    }

    partial void OnSelectedGameIndexChanged(int value)
    {
        if (!_loadingGame) _ = RunSafeAsync(LoadGameAsync, trackBusy: false);
    }

    internal async Task LoadGameAsync(CancellationToken cancellationToken)
    {
        if (SelectedGameIndex < 0 || SelectedGameIndex >= _games.Count)
        {
            HasGame = false;
            Settings.Clear();
            History.Clear();
            HasHistory = false;
            return;
        }

        var game = _games[SelectedGameIndex];
        GameName = game.Name;
        GameSourceText = T($"Gaming_Source_{game.Source}");
        HasGame = true;
        BuildSettings(game.Id);

        var sessions = await _sessions.GetByGameAsync(game.Id, HistoryLimit, cancellationToken).ConfigureAwait(true);
        CollectionSync.Replace(History, sessions.Select(Describe));
        HasHistory = History.Count > 0;
        var measured = sessions.Where(s => s.FrameStats is { HasData: true }).Select(s => s.FrameStats!).ToList();
        HistorySummary = sessions.Count == 0
            ? T("GameProfile_History_None")
            : measured.Count == 0
                ? T("GameProfile_History_NotMeasured", sessions.Count)
                : measured.All(m => m.OnePercentLowFps.HasValue)
                    ? T("GameProfile_History_Average", measured.Count, Formatter.Number(measured.Average(m => m.AverageFps!.Value)), Formatter.Number(measured.Average(m => m.OnePercentLowFps!.Value)))
                    : T("GameProfile_History_AverageFpsOnly", measured.Count, Formatter.Number(measured.Average(m => m.AverageFps!.Value)));
    }

    private void BuildSettings(string gameId)
    {
        var general = _settings.Current.Gaming;
        var profile = general.GameProfiles?.GetValueOrDefault(gameId);
        CollectionSync.Replace(Settings,
        [
            new GameSettingOptionViewModel(nameof(GameProfile.SwitchPowerPlan), T("Settings_PowerPlan_Label"), T("GameProfile_SwitchPower_Description"), "",
                general.SwitchPowerPlan, profile?.SwitchPowerPlan, Localizer, OnSettingChanged),
            new GameSettingOptionViewModel(nameof(GameProfile.RaiseGamePriority), T("Settings_Priority_Label"), T("GameProfile_Priority_Description"), "",
                general.RaiseGamePriority, profile?.RaiseGamePriority, Localizer, OnSettingChanged),
            new GameSettingOptionViewModel(nameof(GameProfile.ThrottleBackgroundApps), T("Settings_BackgroundApps_Label"), T("GameProfile_Background_Description"), "",
                general.ThrottleBackgroundApps, profile?.ThrottleBackgroundApps, Localizer, OnSettingChanged),
            new GameSettingOptionViewModel(nameof(GameProfile.MeasureFrameRate), T("Settings_MeasureFps_Label"), T("GameProfile_Fps_Description"), "",
                general.MeasureFrameRate, profile?.MeasureFrameRate, Localizer, OnSettingChanged),
        ]);
        HasCustomSettings = profile is { IsEmpty: false };
    }

    private void OnSettingChanged(GameSettingOptionViewModel option)
    {
        var gameId = CurrentGameId;
        if (gameId.Length == 0) return;
        var values = Settings.ToDictionary(s => s.Key, s => s.Value);
        HasCustomSettings = values.Values.Any(v => v.HasValue);
        _ = RunSafeAsync(async ct =>
        {
            await _saveLock.WaitAsync(ct).ConfigureAwait(true);
            try
            {
                await _settings.UpdateAsync(s =>
                {
                    s.Gaming.GameProfiles ??= [];
                    var profile = new GameProfile
                    {
                        SwitchPowerPlan = values[nameof(GameProfile.SwitchPowerPlan)],
                        RaiseGamePriority = values[nameof(GameProfile.RaiseGamePriority)],
                        ThrottleBackgroundApps = values[nameof(GameProfile.ThrottleBackgroundApps)],
                        MeasureFrameRate = values[nameof(GameProfile.MeasureFrameRate)],
                    };
                    if (profile.IsEmpty) s.Gaming.GameProfiles.Remove(gameId);
                    else s.Gaming.GameProfiles[gameId] = profile;
                }, ct).ConfigureAwait(true);
            }
            finally
            {
                _saveLock.Release();
            }
        }, trackBusy: false, linkToPage: false);
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetToGeneral()
    {
        foreach (var option in Settings) option.SelectedIndex = 0;
    }

    private GameSessionItemViewModel Describe(GamingSession session)
    {
        var duration = session.EndedAt is { } end ? end - session.StartedAt : (TimeSpan?)null;
        var stats = session.FrameStats;
        var measured = stats is { HasData: true };
        return new GameSessionItemViewModel(
            Formatter.DateTimeFull(session.StartedAt),
            duration is { } d ? Formatter.Duration(d) : T("GameProfile_Session_Unfinished"),
            measured ? T("GameProfile_Fps", Formatter.Number(stats!.AverageFps!.Value)) : T("GameProfile_NotMeasured"),
            measured && stats!.OnePercentLowFps is { } low ? T("GameProfile_Fps", Formatter.Number(low)) : Formatter.NotAvailable,
            measured && stats!.PointOnePercentLowFps is { } lower ? T("GameProfile_Fps", Formatter.Number(lower)) : Formatter.NotAvailable,
            measured);
    }
}
