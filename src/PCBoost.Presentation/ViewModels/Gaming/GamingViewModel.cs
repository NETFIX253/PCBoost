using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

/// <summary>
/// Mode Gaming (§16–21, §68) : jeu actuel, statut, activer / désactiver, métriques en direct (FPS, 1 % low, 0,1 % low,
/// temps d'image, CPU, GPU, RAM, VRAM, températures ; « Non disponible » + raison), optimisations prévues puis actives,
/// vérifications Windows, jeux installés, réglage rapide d'activation automatique.
/// </summary>
public sealed partial class GamingViewModel : ViewModelBase
{
    private readonly IGamingService _gaming;
    private readonly IGameDetectionService _detection;
    private readonly ISettingsService _settings;
    private readonly IPerformanceMonitor _monitor;
    private readonly IShellService _shell;
    private readonly IAppLifecycle? _lifecycle;
    private MonitorLease? _lease;
    private DetectedGameProcess? _detectedGame;
    private bool _loadingSettings;

    public GamingViewModel(
        ViewModelContext context,
        IGamingService gaming,
        IGameDetectionService detection,
        ISettingsService settings,
        IPerformanceMonitor monitor,
        IShellService shell,
        IAppLifecycle? lifecycle = null)
        : base(context)
    {
        _lifecycle = lifecycle;
        _gaming = gaming;
        _detection = detection;
        _settings = settings;
        _monitor = monitor;
        _shell = shell;

        var na = Formatter.NotAvailable;
        FpsTile = new MetricTileViewModel(T("Gaming_Metric_Fps"), na);
        OnePercentLowTile = new MetricTileViewModel(T("Gaming_Metric_OnePercentLow"), na);
        PointOnePercentLowTile = new MetricTileViewModel(T("Gaming_Metric_PointOnePercentLow"), na);
        FrameTimeTile = new MetricTileViewModel(T("Gaming_Metric_FrameTime"), na);
        CpuTile = new MetricTileViewModel(T("Gaming_Metric_Cpu"), na);
        GpuTile = new MetricTileViewModel(T("Gaming_Metric_Gpu"), na);
        RamTile = new MetricTileViewModel(T("Gaming_Metric_Ram"), na);
        VramTile = new MetricTileViewModel(T("Gaming_Metric_Vram"), na);
        CpuTemperatureTile = new MetricTileViewModel(T("Gaming_Metric_CpuTemperature"), na);
        GpuTemperatureTile = new MetricTileViewModel(T("Gaming_Metric_GpuTemperature"), na);
        AutoActivationOptions =
        [
            new OptionItem(nameof(AutoGamingBehavior.Off), T("Settings_AutoGaming_Off")),
            new OptionItem(nameof(AutoGamingBehavior.Ask), T("Settings_AutoGaming_Ask")),
            new OptionItem(nameof(AutoGamingBehavior.Automatic), T("Settings_AutoGaming_Automatic")),
        ];
        CurrentGameText = T("Gaming_NoGame");
        StateText = T("Gaming_State_Inactive");
    }

    // --- Jeu et statut ---
    /// <summary>Nom du jeu ou « Aucun jeu détecté ».</summary>
    [ObservableProperty]
    public partial string CurrentGameText { get; private set; }

    [ObservableProperty]
    public partial bool HasCurrentGame { get; private set; }

    [ObservableProperty]
    public partial string CurrentGameSourceText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModeActive), nameof(IsTransitioning), nameof(IsInactive), nameof(StateGlyph))]
    [NotifyCanExecuteChangedFor(nameof(ActivateCommand), nameof(DeactivateCommand))]
    public partial GamingState State { get; private set; }

    public bool IsModeActive => State == GamingState.Active;

    public bool IsInactive => State == GamingState.Inactive;

    /// <summary>Activation ou restauration en cours.</summary>
    public bool IsTransitioning => State is GamingState.Activating or GamingState.Restoring;

    /// <summary>« Inactif », « Activation… », « Actif », « Restauration… ».</summary>
    [ObservableProperty]
    public partial string StateText { get; private set; }

    public string StateGlyph => State switch
    {
        GamingState.Active => Glyphs.Success,
        GamingState.Activating or GamingState.Restoring => Glyphs.Running,
        _ => Glyphs.Gaming,
    };

    /// <summary>Résultat de la dernière activation / restauration.</summary>
    [ObservableProperty]
    public partial string ResultText { get; private set; } = string.Empty;

    // --- Métriques ---
    public MetricTileViewModel FpsTile { get; }

    public MetricTileViewModel OnePercentLowTile { get; }

    public MetricTileViewModel PointOnePercentLowTile { get; }

    public MetricTileViewModel FrameTimeTile { get; }

    public MetricTileViewModel CpuTile { get; }

    public MetricTileViewModel GpuTile { get; }

    public MetricTileViewModel RamTile { get; }

    public MetricTileViewModel VramTile { get; }

    public MetricTileViewModel CpuTemperatureTile { get; }

    public MetricTileViewModel GpuTemperatureTile { get; }

    /// <summary>Raison de l'absence de mesure des images (autorisation requise, non pris en charge, désactivée…) ; vide si mesurées.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFrameCaptureNotice))]
    public partial string FrameCaptureNotice { get; private set; } = string.Empty;

    public bool HasFrameCaptureNotice => !string.IsNullOrEmpty(FrameCaptureNotice);

    // --- Optimisations ---
    /// <summary>Avant activation : optimisations prévues (aperçu, rien n'est modifié).</summary>
    public ObservableCollection<GamingOptimizationItemViewModel> PlannedOptimizations { get; } = [];

    /// <summary>Pendant la session : optimisations actives (appliquée / ignorée + détail).</summary>
    public ObservableCollection<GamingOptimizationItemViewModel> ActiveOptimizations { get; } = [];

    // --- Vérifications Windows ---
    public ObservableCollection<GameSettingCheckItemViewModel> WindowsChecks { get; } = [];

    // --- Jeux installés ---
    public ObservableCollection<GameItemViewModel> InstalledGames { get; } = [];

    [ObservableProperty]
    public partial string InstalledGamesText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoadingGames { get; private set; }

    // --- Activation automatique ---
    public IReadOnlyList<OptionItem> AutoActivationOptions { get; }

    /// <summary>0 = Désactivée, 1 = Demander, 2 = Automatique (enregistré immédiatement).</summary>
    [ObservableProperty]
    public partial int AutoActivationIndex { get; set; }

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        _lease = MonitorLease.Acquire(_monitor, _settings.Current.MonitoringEnabled, _lifecycle);
        _gaming.StateChanged += OnStateChanged;
        _gaming.LiveMetricsUpdated += OnLiveMetrics;
        _monitor.SampleAvailable += OnSample;
        _detection.GameStarted += OnGameStarted;
        _detection.GameExited += OnGameExited;

        _loadingSettings = true;
        AutoActivationIndex = (int)_settings.Current.Gaming.AutoActivation;
        _loadingSettings = false;

        UpdateState();
        UpdateMetrics();
        await DetectGameAsync(cancellationToken).ConfigureAwait(true);
        await LoadGamesAsync(false, cancellationToken).ConfigureAwait(true);
    }

    protected override void OnDeactivated()
    {
        _gaming.StateChanged -= OnStateChanged;
        _gaming.LiveMetricsUpdated -= OnLiveMetrics;
        _monitor.SampleAvailable -= OnSample;
        _detection.GameStarted -= OnGameStarted;
        _detection.GameExited -= OnGameExited;
        _lease?.Dispose();
        _lease = null;
    }

    partial void OnAutoActivationIndexChanged(int value)
    {
        if (_loadingSettings) return;
        var behavior = (AutoGamingBehavior)Math.Clamp(value, 0, 2);
        _ = RunSafeAsync(ct => _settings.UpdateAsync(s => s.Gaming.AutoActivation = behavior, ct), trackBusy: false, linkToPage: false);
    }

    /// <summary>Active le mode Gaming pour le jeu détecté (ou sans jeu).</summary>
    [RelayCommand(CanExecute = nameof(CanActivate))]
    private async Task ActivateAsync()
    {
        ErrorText = null;
        ResultText = string.Empty;
        await RunSafeAsync(async ct =>
        {
            var report = await _gaming.ActivateAsync(_detectedGame, ct).ConfigureAwait(true);
            if (CheckResult(report.Outcome))
            {
                var applied = report.Optimizations.Count(o => o.Applied);
                ResultText = applied == 1 ? T("Gaming_Activated_One") : T("Gaming_Activated", applied);
            }

            UpdateState();
            CollectionSync.Replace(ActiveOptimizations, report.Optimizations.Select(o => new GamingOptimizationItemViewModel(o, Localizer, planned: false)));
            if (_gaming.LiveMetrics is { } live) ApplyLiveMetrics(live);
            else UpdateFrameCaptureNotice(report.FrameCapture, hasFrames: false);
        }, linkToPage: false).ConfigureAwait(true);
    }

    private bool CanActivate() => State == GamingState.Inactive;

    /// <summary>Désactive et restaure chaque modification de la session.</summary>
    [RelayCommand(CanExecute = nameof(CanDeactivate))]
    private async Task DeactivateAsync()
    {
        ErrorText = null;
        await RunSafeAsync(async ct =>
        {
            var report = await _gaming.DeactivateAsync(ct).ConfigureAwait(true);
            ResultText = report.Success
                ? report.Restored == 1 ? T("Gaming_Restored_One") : T("Gaming_Restored", report.Restored)
                : T("Gaming_Restored_Partial", report.Restored, report.Failed);
            if (!report.Success && report.Messages.Count > 0) ErrorText = string.Join(" ", report.Messages.Select(T));
            ActiveOptimizations.Clear();
            UpdateState();
            await LoadPreviewAsync(ct).ConfigureAwait(true);
        }, linkToPage: false).ConfigureAwait(true);
    }

    private bool CanDeactivate() => State == GamingState.Active;

    /// <summary>Recherche un jeu en cours d'exécution.</summary>
    [RelayCommand]
    private Task DetectGameAsync(CancellationToken cancellationToken) => RunSafeAsync(async ct =>
    {
        _detectedGame = _gaming.CurrentGame ?? await _detection.DetectRunningGameAsync(ct).ConfigureAwait(true);
        UpdateGame();
        RefreshChecks();
        if (State == GamingState.Inactive) await LoadPreviewAsync(ct).ConfigureAwait(true);
    }, cancellationToken);

    [RelayCommand]
    private Task RefreshGamesAsync(CancellationToken cancellationToken) => LoadGamesAsync(true, cancellationToken);

    [RelayCommand]
    private void OpenBenchmark() => Navigation.Navigate(PageKeys.Benchmark);

    [RelayCommand]
    private void OpenSettings() => Navigation.Navigate(PageKeys.Settings);

    private async Task LoadGamesAsync(bool refresh, CancellationToken cancellationToken)
    {
        IsLoadingGames = true;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var games = await _detection.GetInstalledGamesAsync(refresh, ct).ConfigureAwait(true);
                CollectionSync.Replace(InstalledGames, games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => new GameItemViewModel(g, Localizer)));
                InstalledGamesText = games.Count switch
                {
                    0 => T("Gaming_Games_None"),
                    1 => T("Gaming_Games_One"),
                    var n => T("Gaming_Games_Many", n),
                };
            }, cancellationToken, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsLoadingGames = false;
        }
    }

    private async Task LoadPreviewAsync(CancellationToken cancellationToken)
    {
        var preview = await _gaming.PreviewAsync(_detectedGame, cancellationToken).ConfigureAwait(true);
        CollectionSync.Replace(PlannedOptimizations, preview.Select(o => new GamingOptimizationItemViewModel(o, Localizer, planned: true)));
    }

    private void OnStateChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        if (!IsActive) return;
        UpdateState();
        if (_gaming.CurrentSession is { } session && ActiveOptimizations.Count == 0 && session.Optimizations.Count > 0)
            CollectionSync.Replace(ActiveOptimizations, session.Optimizations.Select(o => new GamingOptimizationItemViewModel(o, Localizer, planned: false)));
    });

    private void OnLiveMetrics(object? sender, GamingLiveMetrics metrics) => OnUi(() =>
    {
        if (IsActive) ApplyLiveMetrics(metrics);
    });

    private void OnSample(object? sender, SystemMetricsSample sample) => OnUi(() =>
    {
        // Hors session, les métriques système proviennent du moniteur ; les images ne sont pas mesurées.
        if (IsActive && _gaming.LiveMetrics is null) ApplySystemSample(sample);
    });

    private void OnGameStarted(object? sender, DetectedGameProcess game) => OnUi(() =>
    {
        if (!IsActive) return;
        _detectedGame = game;
        UpdateGame();
        RefreshChecks();
        if (State == GamingState.Inactive) _ = RunSafeAsync(LoadPreviewAsync, trackBusy: false);
    });

    private void OnGameExited(object? sender, DetectedGameProcess game) => OnUi(() =>
    {
        if (!IsActive || _detectedGame?.ProcessId != game.ProcessId) return;
        _detectedGame = _gaming.CurrentGame;
        UpdateGame();
        RefreshChecks();
    });

    private void UpdateState()
    {
        State = _gaming.State;
        StateText = T($"Gaming_State_{State}");
        if (_gaming.CurrentGame is { } game) _detectedGame = game;
        UpdateGame();
        if (_gaming.CurrentSession is { } session && State == GamingState.Active && ActiveOptimizations.Count == 0)
            CollectionSync.Replace(ActiveOptimizations, session.Optimizations.Select(o => new GamingOptimizationItemViewModel(o, Localizer, planned: false)));
    }

    private void UpdateGame()
    {
        HasCurrentGame = _detectedGame is not null;
        CurrentGameText = _detectedGame?.Game.Name ?? T("Gaming_NoGame");
        CurrentGameSourceText = _detectedGame is { } g ? T($"Gaming_Source_{g.Game.Source}") : string.Empty;
    }

    private void RefreshChecks()
    {
        var checks = _gaming.CheckWindowsGameSettings(_detectedGame);
        CollectionSync.Replace(WindowsChecks, checks.Select(c => new GameSettingCheckItemViewModel(c, Localizer, OpenWindowsSetting)));
    }

    private void UpdateMetrics()
    {
        if (_gaming.LiveMetrics is { } live) ApplyLiveMetrics(live);
        else
        {
            if (_monitor.Latest is { } sample) ApplySystemSample(sample);
            UpdateFrameCaptureNotice(null, hasFrames: false);
        }
    }

    private void ApplyLiveMetrics(GamingLiveMetrics m)
    {
        var na = Formatter.NotAvailable;
        var frames = m.Frames;
        if (frames.HasData)
        {
            SetOrUnavailable(FpsTile, frames.AverageFps, Formatter.Fps);
            SetOrUnavailable(OnePercentLowTile, frames.OnePercentLowFps, Formatter.Fps);
            SetOrUnavailable(PointOnePercentLowTile, frames.PointOnePercentLowFps, Formatter.Fps);
            if (frames.AverageFrameTimeMs is { } ft)
                FrameTimeTile.Set(Formatter.Milliseconds(ft), frames.P99FrameTimeMs is { } p99 ? T("Gaming_Metric_FrameTimeP99", Formatter.Milliseconds(p99)) : null);
            else FrameTimeTile.SetUnavailable(na);
        }
        else
        {
            var reason = FrameCaptureReason(m.FrameCapture, hasFrames: false);
            FpsTile.SetUnavailable(na, reason);
            OnePercentLowTile.SetUnavailable(na);
            PointOnePercentLowTile.SetUnavailable(na);
            FrameTimeTile.SetUnavailable(na);
        }

        UpdateFrameCaptureNotice(m.FrameCapture, frames.HasData);
        CpuTile.Set(Formatter.Percent(m.CpuPercent), null, m.CpuPercent);
        if (m.GpuPercent is { } gpu) GpuTile.Set(Formatter.Percent(gpu), null, gpu);
        else GpuTile.SetUnavailable(na);
        RamTile.Set(Formatter.Percent(m.MemoryUsedPercent), null, m.MemoryUsedPercent);
        if (m.GpuMemoryUsedBytes is { } vram) VramTile.Set(Formatter.Bytes(vram));
        else VramTile.SetUnavailable(na);
        SetTemperature(CpuTemperatureTile, m.CpuTemperature);
        SetTemperature(GpuTemperatureTile, m.GpuTemperature);
    }

    private void ApplySystemSample(SystemMetricsSample s)
    {
        var na = Formatter.NotAvailable;
        CpuTile.Set(Formatter.Percent(s.CpuPercent), null, s.CpuPercent);
        if (s.GpuPercent is { } gpu) GpuTile.Set(Formatter.Percent(gpu), null, gpu);
        else GpuTile.SetUnavailable(na);
        RamTile.Set(Formatter.Percent(s.MemoryUsedPercent),
            T("Home_Tile_MemoryDetail", Formatter.Bytes(s.MemoryUsedBytes), Formatter.Bytes(s.MemoryTotalBytes)), s.MemoryUsedPercent);
        if (s.GpuDedicatedMemoryUsedBytes is { } vram) VramTile.Set(Formatter.Bytes(vram));
        else VramTile.SetUnavailable(na);
        var temps = _monitor.Temperatures;
        SetTemperature(CpuTemperatureTile, temps.Cpu);
        SetTemperature(GpuTemperatureTile, temps.Gpu);

        var reason = T("Gaming_Capture_Inactive");
        FpsTile.SetUnavailable(na, reason);
        OnePercentLowTile.SetUnavailable(na);
        PointOnePercentLowTile.SetUnavailable(na);
        FrameTimeTile.SetUnavailable(na);
    }

    private void SetOrUnavailable(MetricTileViewModel tile, double? value, Func<double?, string> format)
    {
        if (value is { } v && double.IsFinite(v)) tile.Set(format(v));
        else tile.SetUnavailable(Formatter.NotAvailable);
    }

    private void SetTemperature(MetricTileViewModel tile, SensorReading reading)
    {
        if (reading.HasValue) tile.Set(Formatter.Temperature(reading.Value), null, reading.Value);
        else tile.SetUnavailable(Formatter.NotAvailable, T($"Common_Label_Availability_{reading.Availability}"));
    }

    private void UpdateFrameCaptureNotice(FrameCaptureAvailability? availability, bool hasFrames)
        => FrameCaptureNotice = FrameCaptureReason(availability, hasFrames);

    private string FrameCaptureReason(FrameCaptureAvailability? availability, bool hasFrames)
    {
        if (hasFrames) return string.Empty;
        if (availability is null || State != GamingState.Active) return T("Gaming_Capture_Inactive");
        if (!_settings.Current.Gaming.MeasureFrameRate) return T("Gaming_Capture_Disabled");
        return availability.Value switch
        {
            FrameCaptureAvailability.Available => T("Gaming_Capture_Waiting"),
            _ => T($"Gaming_Capture_{availability.Value}"),
        };
    }

    private void OpenWindowsSetting(GameSettingCheckItemViewModel check)
    {
        var id = check.Id.ToLowerInvariant();
        var uri = id.Contains("gamemode", StringComparison.Ordinal) || id.Contains("game-mode", StringComparison.Ordinal) || id.Contains("game_mode", StringComparison.Ordinal)
            ? "ms-settings:gaming-gamemode"
            : id.Contains("gpu", StringComparison.Ordinal) || id.Contains("hags", StringComparison.Ordinal) || id.Contains("windowed", StringComparison.Ordinal)
              || id.Contains("graphics", StringComparison.Ordinal) || id.Contains("scheduling", StringComparison.Ordinal)
                ? "ms-settings:display-advancedgraphics"
                : "ms-settings:gaming";
        CheckResult(_shell.OpenUri(new Uri(uri)));
    }
}
