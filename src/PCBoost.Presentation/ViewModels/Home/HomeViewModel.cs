using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

/// <summary>
/// Tableau de bord (§7, §40, §63) : score explicable, phrase d'état, tuiles en direct, état de Windows,
/// 3 recommandations principales, actions rapides. Le moniteur passe en mode Active tant que la page est affichée.
/// </summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private const int TopRecommendationCount = 3;
    private readonly ISystemAnalyzer _analyzer;
    private readonly IHealthRulesEngine _rules;
    private readonly IPerformanceScoreCalculator _score;
    private readonly IPerformanceRecommendationEngine _recommendations;
    private readonly IPerformanceMonitor _monitor;
    private readonly ISettingsService _settings;
    private readonly IAppLifecycle? _lifecycle;
    private MonitorLease? _lease;
    private SystemAnalysisReport? _report;

    public HomeViewModel(
        ViewModelContext context,
        ISystemAnalyzer analyzer,
        IHealthRulesEngine rules,
        IPerformanceScoreCalculator score,
        IPerformanceRecommendationEngine recommendations,
        IPerformanceMonitor monitor,
        ISettingsService settings,
        IAppLifecycle? lifecycle = null)
        : base(context)
    {
        _lifecycle = lifecycle;
        _analyzer = analyzer;
        _rules = rules;
        _score = score;
        _recommendations = recommendations;
        _monitor = monitor;
        _settings = settings;

        var na = Formatter.NotAvailable;
        CpuTile = new MetricTileViewModel(T("Home_Tile_Cpu"), na);
        MemoryTile = new MetricTileViewModel(T("Home_Tile_Memory"), na);
        DiskTile = new MetricTileViewModel(T("Home_Tile_Disk"), na);
        GpuTile = new MetricTileViewModel(T("Home_Tile_Gpu"), na);
        CpuTemperatureTile = new MetricTileViewModel(T("Home_Temperature_Cpu"), na);
        GpuTemperatureTile = new MetricTileViewModel(T("Home_Temperature_Gpu"), na);
        StorageTemperatureTile = new MetricTileViewModel(T("Home_Temperature_Storage"), na);
        StartupAppsText = na;
        SystemDriveFreeText = na;
        BackgroundProcessesText = na;
        WindowsEditionText = na;
        WindowsVersionText = na;
        UptimeText = na;
        LastAnalysisText = T("Home_LastAnalysis_Never");
    }

    // --- Analyse / score ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    public partial bool HasReport { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    public partial bool IsAnalyzing { get; private set; }

    /// <summary>Aucune analyse et aucune en cours : afficher l'invitation « Analyser mon PC ».</summary>
    public bool ShowEmptyState => !HasReport && !IsAnalyzing;

    [ObservableProperty]
    public partial double AnalysisProgressPercent { get; private set; }

    [ObservableProperty]
    public partial string AnalysisStepText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int ScoreValue { get; private set; }

    /// <summary>« 72 » (le « /100 » est un libellé statique : Home_Score_Max).</summary>
    [ObservableProperty]
    public partial string ScoreText { get; private set; } = string.Empty;

    /// <summary>Niveau textuel du score (« Bon état », « État correct », « À améliorer »).</summary>
    [ObservableProperty]
    public partial string ScoreLevelText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ScoreGlyph { get; private set; } = Glyphs.Unknown;

    [ObservableProperty]
    public partial string ScoreAccessibleName { get; private set; } = string.Empty;

    public ObservableCollection<ScoreFactorItemViewModel> Factors { get; } = [];

    /// <summary>« Votre PC est actuellement en état normal. » ou « {n} points peuvent être améliorés. »</summary>
    [ObservableProperty]
    public partial string StatusSentence { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int ImprovablePointCount { get; private set; }

    public ObservableCollection<RecommendationItemViewModel> TopRecommendations { get; } = [];

    [ObservableProperty]
    public partial bool HasRecommendations { get; private set; }

    /// <summary>« Dernière analyse : il y a 5 min ».</summary>
    [ObservableProperty]
    public partial string LastAnalysisText { get; private set; }

    // --- Tuiles en direct ---
    public MetricTileViewModel CpuTile { get; }

    public MetricTileViewModel MemoryTile { get; }

    public MetricTileViewModel DiskTile { get; }

    public MetricTileViewModel GpuTile { get; }

    public MetricTileViewModel CpuTemperatureTile { get; }

    public MetricTileViewModel GpuTemperatureTile { get; }

    public MetricTileViewModel StorageTemperatureTile { get; }

    /// <summary>Au moins une température est mesurée (sinon masquer la section).</summary>
    [ObservableProperty]
    public partial bool HasTemperatures { get; private set; }

    // --- Système ---
    /// <summary>« 12 applications ».</summary>
    [ObservableProperty]
    public partial string StartupAppsText { get; private set; }

    [ObservableProperty]
    public partial string SystemDriveFreeText { get; private set; }

    /// <summary>« sur 237,9 Go — C:\ ».</summary>
    [ObservableProperty]
    public partial string SystemDriveDetail { get; private set; } = string.Empty;

    /// <summary>Espace utilisé du disque système 0–100.</summary>
    [ObservableProperty]
    public partial double SystemDriveUsedPercent { get; private set; }

    [ObservableProperty]
    public partial string BackgroundProcessesText { get; private set; }

    /// <summary>« Windows 11 Professionnel ».</summary>
    [ObservableProperty]
    public partial string WindowsEditionText { get; private set; }

    /// <summary>« Version 24H2 (build 26100.2033) ».</summary>
    [ObservableProperty]
    public partial string WindowsVersionText { get; private set; }

    /// <summary>« Allumé depuis 3 j 4 h ».</summary>
    [ObservableProperty]
    public partial string UptimeText { get; private set; }

    [ObservableProperty]
    public partial bool IsWindowsUnsupported { get; private set; }

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        _lease = MonitorLease.Acquire(_monitor, _settings.Current.MonitoringEnabled, _lifecycle);
        _monitor.SampleAvailable += OnSample;
        _analyzer.AnalysisCompleted += OnAnalysisCompleted;
        UpdateLiveTiles(_monitor.Latest);

        if (_analyzer.LastReport is { } report)
        {
            ApplyReport(report);
        }
        else
        {
            await AnalyzeAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    protected override void OnDeactivated()
    {
        _monitor.SampleAvailable -= OnSample;
        _analyzer.AnalysisCompleted -= OnAnalysisCompleted;
        _lease?.Dispose();
        _lease = null;
    }

    /// <summary>Relance une analyse complète (action rapide « Analyser »).</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        IsAnalyzing = true;
        AnalysisProgressPercent = 0;
        AnalysisStepText = T("Analysis_StepRunning_System");
        ErrorText = null;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var progress = UiProgress<AnalysisProgress>(p =>
                {
                    AnalysisProgressPercent = Math.Clamp(p.Percent, 0, 100);
                    AnalysisStepText = T($"Analysis_StepRunning_{p.Stage}");
                });
                var report = await _analyzer.AnalyzeAsync(new AnalysisOptions(), progress, ct).ConfigureAwait(true);
                ApplyReport(report);
            }, cancellationToken, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private bool CanAnalyze() => !IsAnalyzing;

    [RelayCommand]
    private void Optimize() => Navigation.Navigate(PageKeys.Optimization);

    [RelayCommand]
    private void Cleanup() => Navigation.Navigate(PageKeys.Cleanup);

    [RelayCommand]
    private void OpenGaming() => Navigation.Navigate(PageKeys.Gaming);

    [RelayCommand]
    private void OpenPerformance() => Navigation.Navigate(PageKeys.Performance);

    /// <summary>« Pourquoi mon PC est lent ? »</summary>
    [RelayCommand]
    private void WhyIsMyPcSlow() => Navigation.Navigate(PageKeys.Diagnosis);

    [RelayCommand]
    private void OpenAnalysis() => Navigation.Navigate(PageKeys.Analysis);

    [RelayCommand]
    private void OpenStartup() => Navigation.Navigate(PageKeys.Startup);

    [RelayCommand]
    private void OpenStorage() => Navigation.Navigate(PageKeys.Storage);

    [RelayCommand]
    private void OpenProcesses() => Navigation.Navigate(PageKeys.Processes);

    private void OnAnalysisCompleted(object? sender, SystemAnalysisReport report) => OnUi(() =>
    {
        if (IsActive && !IsAnalyzing) ApplyReport(report);
    });

    private void OnSample(object? sender, SystemMetricsSample sample) => OnUi(() =>
    {
        if (IsActive) UpdateLiveTiles(sample);
    });

    internal void ApplyReport(SystemAnalysisReport report)
    {
        _report = report;
        var thresholds = _settings.Current.Thresholds;
        var score = _score.Calculate(report, thresholds);
        var findings = _rules.Evaluate(report, thresholds);

        ScoreValue = Math.Clamp(score.Value, 0, 100);
        ScoreText = Formatter.Number(ScoreValue);
        var level = ScoreValue >= 80 ? "Good" : ScoreValue >= 50 ? "Fair" : "Poor";
        ScoreLevelText = T($"Home_ScoreLevel_{level}");
        ScoreGlyph = level switch { "Good" => Glyphs.Success, "Fair" => Glyphs.Warning, _ => Glyphs.Error };
        ScoreAccessibleName = T("Home_Score_Accessible", ScoreValue, ScoreLevelText);

        CollectionSync.Replace(Factors, score.Factors.Select(f => new ScoreFactorItemViewModel(f, Localizer, NavigateTo)));
        ImprovablePointCount = score.ImprovablePoints;
        StatusSentence = BuildStatusSentence(ImprovablePointCount);

        var dismissed = _settings.Current.DismissedRecommendations;
        var recs = _recommendations.GetRecommendations(report, findings, dismissed);
        CollectionSync.Replace(TopRecommendations, recs.Take(TopRecommendationCount)
            .Select(r => new RecommendationItemViewModel(r, Localizer, OpenRecommendation, DismissRecommendationAsync)));
        HasRecommendations = TopRecommendations.Count > 0;

        LastAnalysisText = T("Home_LastAnalysis", Formatter.DateTime(report.Timestamp));
        StartupAppsText = report.EnabledStartupCount == 1
            ? T("Home_StartupApps_One")
            : T("Home_StartupApps", report.EnabledStartupCount);
        BackgroundProcessesText = T("Home_BackgroundProcesses", report.BackgroundProcessCount);

        if (report.SystemDrive is { } drive)
        {
            SystemDriveFreeText = T("Home_SystemDrive_Free", Formatter.Bytes(drive.FreeBytes));
            SystemDriveDetail = T("Home_SystemDrive_Detail", Formatter.Bytes(drive.TotalBytes), drive.RootPath);
            SystemDriveUsedPercent = drive.TotalBytes <= 0 ? 0 : Math.Clamp(drive.UsedBytes * 100d / drive.TotalBytes, 0, 100);
        }
        else
        {
            SystemDriveFreeText = Formatter.NotAvailable;
            SystemDriveDetail = string.Empty;
            SystemDriveUsedPercent = 0;
        }

        ApplyOs(report.Os);
        // Les températures en direct du moniteur priment sur celles, plus anciennes, du rapport.
        var live = _monitor.Temperatures;
        UpdateTemperatures(HasAnyTemperature(live) ? live : report.Temperatures);
        HasReport = true;
    }

    internal string BuildStatusSentence(int improvable) => improvable switch
    {
        <= 0 => T("Home_Status_Normal"),
        1 => T("Home_Status_OneImprovable"),
        _ => T("Home_Status_Improvable", improvable),
    };

    private void ApplyOs(OsInfo os)
    {
        WindowsEditionText = string.IsNullOrWhiteSpace(os.ProductName) ? Formatter.NotAvailable : os.ProductName;
        var build = os.UpdateBuildRevision > 0 ? $"{os.BuildNumber}.{os.UpdateBuildRevision}" : os.BuildNumber.ToString(Localizer.Culture);
        WindowsVersionText = string.IsNullOrWhiteSpace(os.DisplayVersion)
            ? T("Home_Windows_Build", build)
            : T("Home_Windows_Version", os.DisplayVersion, build);
        UptimeText = T("Home_Windows_Uptime", Formatter.Duration(os.Uptime));
        IsWindowsUnsupported = !os.IsSupported;
    }

    private void UpdateLiveTiles(SystemMetricsSample? sample)
    {
        var na = Formatter.NotAvailable;
        if (sample is null)
        {
            CpuTile.SetUnavailable(na);
            MemoryTile.SetUnavailable(na);
            DiskTile.SetUnavailable(na);
            GpuTile.SetUnavailable(na);
        }
        else
        {
            CpuTile.Set(Formatter.Percent(sample.CpuPercent), null, sample.CpuPercent);
            MemoryTile.Set(
                Formatter.Percent(sample.MemoryUsedPercent),
                T("Home_Tile_MemoryDetail", Formatter.Bytes(sample.MemoryUsedBytes), Formatter.Bytes(sample.MemoryTotalBytes)),
                sample.MemoryUsedPercent);

            if (sample.DiskActivePercent is { } disk)
            {
                var detail = sample.DiskReadBytesPerSec.HasValue || sample.DiskWriteBytesPerSec.HasValue
                    ? T("Home_Tile_DiskDetail", Formatter.Rate(sample.DiskReadBytesPerSec), Formatter.Rate(sample.DiskWriteBytesPerSec))
                    : null;
                DiskTile.Set(Formatter.Percent(disk), detail, disk);
            }
            else
            {
                DiskTile.SetUnavailable(na);
            }

            if (sample.GpuPercent is { } gpu)
            {
                var detail = sample.GpuDedicatedMemoryUsedBytes is { } vram ? T("Home_Tile_GpuDetail", Formatter.Bytes(vram)) : null;
                GpuTile.Set(Formatter.Percent(gpu), detail, gpu);
            }
            else
            {
                GpuTile.SetUnavailable(na);
            }

            if (_report is null) BackgroundProcessesText = T("Home_RunningProcesses", sample.ProcessCount);
        }

        UpdateTemperatures(_monitor.Temperatures);
    }

    private void UpdateTemperatures(TemperatureReadings? readings)
    {
        if (readings is null) return;
        SetTemperature(CpuTemperatureTile, readings.Cpu);
        SetTemperature(GpuTemperatureTile, readings.Gpu);
        SetTemperature(StorageTemperatureTile, readings.Storage);
        HasTemperatures = HasAnyTemperature(readings);
    }

    private static bool HasAnyTemperature(TemperatureReadings? r)
        => r is not null && (r.Cpu.HasValue || r.Gpu.HasValue || r.Storage.HasValue);

    private void SetTemperature(MetricTileViewModel tile, SensorReading reading)
    {
        if (reading.HasValue) tile.Set(Formatter.Temperature(reading.Value), null, reading.Value);
        else tile.SetUnavailable(Formatter.NotAvailable, T($"Common_Label_Availability_{reading.Availability}"));
    }

    private void NavigateTo(string target) => Navigation.Navigate(target);

    private void OpenRecommendation(RecommendationItemViewModel item)
    {
        if (item.Model.Action is { } action) Navigation.Navigate(action.NavigationTarget, action.OptimizationId);
    }

    private Task DismissRecommendationAsync(RecommendationItemViewModel item) => RunSafeAsync(async ct =>
    {
        await _settings.DismissRecommendationAsync(item.Id, ct).ConfigureAwait(true);
        TopRecommendations.Remove(item);
        if (_report is not null) ApplyReport(_report);
    });
}
