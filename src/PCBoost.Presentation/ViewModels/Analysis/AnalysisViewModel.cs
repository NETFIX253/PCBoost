using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

public enum AnalysisPhase { NotStarted = 0, Analyzing, Done }

/// <summary>
/// Analyse complète (§8, §62, §24, §60) : étapes, matériel, logiciel, cartes de constats par catégorie,
/// recommandations (masquables), profil matériel, conseils matériels, lien vers le stockage.
/// Paramètre de navigation <see cref="StartParameter"/> : lance une nouvelle analyse à l'ouverture.
/// </summary>
public sealed partial class AnalysisViewModel : ViewModelBase
{
    /// <summary>Paramètre de navigation qui lance immédiatement une nouvelle analyse.</summary>
    public const string StartParameter = "start";

    private static readonly TimeSpan AdviceHistoryWindow = TimeSpan.FromDays(7);
    private readonly ISystemAnalyzer _analyzer;
    private readonly IHealthRulesEngine _rules;
    private readonly IPerformanceRecommendationEngine _recommendations;
    private readonly IHardwareAdvisor _advisor;
    private readonly IPerformanceHistoryService _history;
    private readonly ISettingsService _settings;
    private SystemAnalysisReport? _report;
    private IReadOnlyList<HealthFinding> _findings = [];

    public AnalysisViewModel(
        ViewModelContext context,
        ISystemAnalyzer analyzer,
        IHealthRulesEngine rules,
        IPerformanceRecommendationEngine recommendations,
        IHardwareAdvisor advisor,
        IPerformanceHistoryService history,
        ISettingsService settings)
        : base(context)
    {
        _analyzer = analyzer;
        _rules = rules;
        _recommendations = recommendations;
        _advisor = advisor;
        _history = history;
        _settings = settings;
        Steps = new ObservableCollection<ProgressStepViewModel>(
            Enum.GetValues<AnalysisStage>().Select(s => new ProgressStepViewModel((int)s, T($"Analysis_Step_{s}"), st => T($"Common_Label_Step_{st}"))));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotStarted), nameof(IsAnalyzing), nameof(IsDone))]
    [NotifyCanExecuteChangedFor(nameof(StartAnalysisCommand))]
    public partial AnalysisPhase Phase { get; private set; }

    public bool IsNotStarted => Phase == AnalysisPhase.NotStarted;

    public bool IsAnalyzing => Phase == AnalysisPhase.Analyzing;

    public bool IsDone => Phase == AnalysisPhase.Done;

    public ObservableCollection<ProgressStepViewModel> Steps { get; }

    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial string CurrentStepText { get; private set; } = string.Empty;

    /// <summary>« Analyse effectuée il y a 2 min ».</summary>
    [ObservableProperty]
    public partial string AnalysisDateText { get; private set; } = string.Empty;

    // --- Matériel ---
    /// <summary>Processeur, cœurs/threads, fréquence, mémoire, architecture, Windows, alimentation.</summary>
    public ObservableCollection<InfoRowViewModel> HardwareRows { get; } = [];

    /// <summary>Cartes graphiques : nom, mémoire vidéo, pilote.</summary>
    public ObservableCollection<InfoRowViewModel> GpuRows { get; } = [];

    public ObservableCollection<DriveItemViewModel> Drives { get; } = [];

    /// <summary>Températures CPU / GPU / stockage (« Non disponible » + raison si non mesurée).</summary>
    public ObservableCollection<InfoRowViewModel> TemperatureRows { get; } = [];

    // --- Logiciel ---
    /// <summary>Programmes installés, démarrage, processus, arrière-plan, charge observée.</summary>
    public ObservableCollection<InfoRowViewModel> SoftwareRows { get; } = [];

    public ObservableCollection<ProcessUsageItemViewModel> TopMemoryProcesses { get; } = [];

    public ObservableCollection<ProcessUsageItemViewModel> TopCpuProcesses { get; } = [];

    // --- Constats ---
    public ObservableCollection<FindingCardViewModel> FindingCards { get; } = [];

    /// <summary>Catégories ayant au moins un constat (affichées en cartes) ; les autres sont résumées en une ligne.</summary>
    public ObservableCollection<FindingCardViewModel> ActiveFindingCards { get; } = [];

    /// <summary>« Aucun constat pour : Processeur, Stockage… » (vide si toutes les catégories ont un constat).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCleanCategories))]
    public partial string CleanCategoriesText { get; private set; } = string.Empty;

    public bool HasCleanCategories => !string.IsNullOrEmpty(CleanCategoriesText);

    /// <summary>Colonnes de la grille des constats : une seule carte occupe toute la largeur.</summary>
    [ObservableProperty]
    public partial int FindingColumns { get; private set; } = 2;

    [ObservableProperty]
    public partial int FindingCount { get; private set; }

    /// <summary>« 3 points d'attention » / « Aucun point d'attention ».</summary>
    [ObservableProperty]
    public partial string FindingSummaryText { get; private set; } = string.Empty;

    // --- Recommandations ---
    public ObservableCollection<RecommendationItemViewModel> Recommendations { get; } = [];

    [ObservableProperty]
    public partial bool HasRecommendations { get; private set; }

    [ObservableProperty]
    public partial int DismissedRecommendationCount { get; private set; }

    /// <summary>« 2 recommandations masquées (réaffichables dans les Paramètres) ».</summary>
    [ObservableProperty]
    public partial string DismissedRecommendationsText { get; private set; } = string.Empty;

    public bool HasDismissedRecommendations => DismissedRecommendationCount > 0;

    // --- Profil et conseils matériels ---
    [ObservableProperty]
    public partial string HardwareTierText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string HardwareTierDescription { get; private set; } = string.Empty;

    public ObservableCollection<TextItemViewModel> HardwareProfileReasons { get; } = [];

    public ObservableCollection<HardwareAdviceItemViewModel> HardwareAdvice { get; } = [];

    [ObservableProperty]
    public partial bool HasHardwareAdvice { get; private set; }

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        var forceStart = string.Equals(parameter as string, StartParameter, StringComparison.OrdinalIgnoreCase);
        if (!forceStart && _analyzer.LastReport is { } last)
        {
            await ApplyReportAsync(last, cancellationToken).ConfigureAwait(true);
            return;
        }

        await StartAnalysisAsync(cancellationToken).ConfigureAwait(true);
    }

    protected override void OnDeactivated() => StartAnalysisCancelCommand.Execute(null);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanStartAnalysis))]
    private async Task StartAnalysisAsync(CancellationToken cancellationToken)
    {
        var previous = Phase;
        ErrorText = null;
        ProgressSteps.Reset(Steps);
        ProgressPercent = 0;
        CurrentStepText = T("Analysis_StepRunning_System");
        Phase = AnalysisPhase.Analyzing;

        var ok = await RunSafeAsync(async ct =>
        {
            var progress = UiProgress<AnalysisProgress>(p =>
            {
                ProgressSteps.Advance(Steps, (int)p.Stage);
                ProgressPercent = Math.Clamp(p.Percent, 0, 100);
                CurrentStepText = T($"Analysis_StepRunning_{p.Stage}");
            });
            var report = await _analyzer.AnalyzeAsync(new AnalysisOptions(), progress, ct).ConfigureAwait(true);
            ProgressSteps.CompleteAll(Steps);
            ProgressPercent = 100;
            await ApplyReportAsync(report, ct).ConfigureAwait(true);
        }, cancellationToken, trackBusy: false).ConfigureAwait(true);

        if (!ok)
        {
            ProgressSteps.FailCurrent(Steps);
            Phase = _report is null ? AnalysisPhase.NotStarted : previous == AnalysisPhase.Analyzing ? AnalysisPhase.Done : previous;
        }
    }

    private bool CanStartAnalysis() => Phase != AnalysisPhase.Analyzing;

    [RelayCommand]
    private void OpenStorage() => Navigation.Navigate(PageKeys.Storage);

    [RelayCommand]
    private void OpenDiagnosis() => Navigation.Navigate(PageKeys.Diagnosis);

    [RelayCommand]
    private void Optimize() => Navigation.Navigate(PageKeys.Optimization);

    [RelayCommand]
    private void OpenSettings() => Navigation.Navigate(PageKeys.Settings);

    private async Task ApplyReportAsync(SystemAnalysisReport report, CancellationToken cancellationToken)
    {
        _report = report;
        _findings = _rules.Evaluate(report, _settings.Current.Thresholds);
        AnalysisDateText = T("Analysis_Date", Formatter.DateTime(report.Timestamp));

        BuildHardware(report);
        BuildSoftware(report);
        BuildFindingCards();
        BuildRecommendations();
        BuildProfile(report.HardwareProfile);
        Phase = AnalysisPhase.Done;

        IReadOnlyList<Core.Models.Monitoring.PerformanceSnapshot> history;
        try
        {
            history = await _history.GetHistoryAsync(AdviceHistoryWindow, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // L'historique est facultatif : les conseils s'appuient alors sur la seule analyse.
            history = [];
        }

        CollectionSync.Replace(HardwareAdvice, _advisor.GetAdvice(report, history).Select(a => new HardwareAdviceItemViewModel(a, Localizer)));
        HasHardwareAdvice = HardwareAdvice.Count > 0;
    }

    private void BuildHardware(SystemAnalysisReport r)
    {
        var rows = new List<InfoRowViewModel>
        {
            new(T("Analysis_Hw_Cpu"), string.IsNullOrWhiteSpace(r.Cpu.Name) ? Formatter.NotAvailable : r.Cpu.Name.Trim()),
            new(T("Analysis_Hw_Cores"), T("Analysis_Hw_CoresValue", r.Cpu.PhysicalCores, r.Cpu.LogicalProcessors)),
            new(T("Analysis_Hw_Frequency"), FrequencyText(r.Cpu)),
            new(T("Analysis_Hw_Memory"), Formatter.Bytes(r.Memory.TotalBytes),
                T("Analysis_Hw_MemoryAvailable", Formatter.Bytes(r.Memory.AvailableBytes))),
        };
        if (r.Memory.SpeedMHz.HasValue || r.Memory.ModuleCount.HasValue)
        {
            var parts = new List<string>();
            // Windows indique le débit des barrettes (DDR) : des mégatransferts par seconde, pas une fréquence en GHz.
            if (r.Memory.SpeedMHz is { } s) parts.Add(T("Common_Format_MTs", s.ToString("N0", Localizer.Culture)));
            if (r.Memory.ModuleCount is { } m) parts.Add(m == 1 ? T("Analysis_Hw_OneModule") : T("Analysis_Hw_Modules", m));
            rows.Add(new(T("Analysis_Hw_MemoryDetails"), string.Join(" · ", parts)));
        }

        rows.Add(new(T("Analysis_Hw_Architecture"), T($"Analysis_Arch_{r.Os.Architecture}")));
        rows.Add(new(T("Analysis_Hw_Windows"), r.Os.ProductName,
            string.IsNullOrWhiteSpace(r.Os.DisplayVersion)
                ? T("Home_Windows_Build", r.Os.BuildNumber)
                : T("Home_Windows_Version", r.Os.DisplayVersion, r.Os.UpdateBuildRevision > 0 ? $"{r.Os.BuildNumber}.{r.Os.UpdateBuildRevision}" : r.Os.BuildNumber.ToString(Localizer.Culture))));
        rows.Add(new(T("Analysis_Hw_Uptime"), Formatter.Duration(r.Os.Uptime)));
        rows.Add(new(T("Analysis_Hw_Power"), PowerText(r.Power), r.ActivePowerScheme is { } scheme ? T("Analysis_Hw_PowerScheme", scheme.Name) : null));
        CollectionSync.Replace(HardwareRows, rows);

        CollectionSync.Replace(GpuRows, r.Gpus.Where(g => !g.IsSoftwareAdapter).Select(g => new InfoRowViewModel(
            g.Name,
            g.DedicatedVideoMemoryBytes is { } vram && vram > 0
                ? T("Analysis_Gpu_Vram", Formatter.Bytes(vram))
                : g.IsLikelyIntegrated ? T("Analysis_Gpu_Integrated") : Formatter.NotAvailable,
            string.IsNullOrWhiteSpace(g.DriverVersion) ? null : T("Analysis_Gpu_Driver", g.DriverVersion!))));
        if (GpuRows.Count == 0) GpuRows.Add(new InfoRowViewModel(T("Analysis_Hw_Gpu"), Formatter.NotAvailable));

        CollectionSync.Replace(Drives, r.Drives.OrderByDescending(d => d.IsSystemDrive).Select(d => new DriveItemViewModel(d, Localizer, Formatter)));

        CollectionSync.Replace(TemperatureRows,
        [
            TemperatureRow("Home_Temperature_Cpu", r.Temperatures.Cpu),
            TemperatureRow("Home_Temperature_Gpu", r.Temperatures.Gpu),
            TemperatureRow("Home_Temperature_Storage", r.Temperatures.Storage),
        ]);
    }

    private void BuildSoftware(SystemAnalysisReport r)
    {
        var rows = new List<InfoRowViewModel>
        {
            new(T("Analysis_Sw_InstalledPrograms"), r.InstalledProgramCount is { } n ? Formatter.Number(n) : Formatter.NotAvailable),
            new(T("Analysis_Sw_Startup"), T("Analysis_Sw_StartupValue", r.EnabledStartupCount, r.StartupEntries.Count)),
            new(T("Analysis_Sw_Processes"), Formatter.Number(r.RunningProcessCount)),
            new(T("Analysis_Sw_Background"), Formatter.Number(r.BackgroundProcessCount)),
            new(T("Analysis_Sw_CpuLoad"), T("Analysis_Sw_CpuLoadValue", Formatter.Percent(r.Load.CpuAveragePercent), Formatter.Percent(r.Load.CpuMaxPercent)),
                T("Analysis_Sw_Sampling", Formatter.Duration(r.Load.SamplingDuration))),
            new(T("Analysis_Sw_MemoryLoad"), Formatter.Percent(r.Load.MemoryUsedPercent)),
            new(T("Analysis_Sw_DiskLoad"), Formatter.Percent(r.Load.DiskActiveAveragePercent)),
            new(T("Analysis_Sw_GpuLoad"), Formatter.Percent(r.Load.GpuAveragePercent)),
            new(T("Analysis_Sw_Cleanable"), r.CleanableBytes is { } bytes ? Formatter.Bytes(bytes) : T("Analysis_Sw_NotAnalyzed")),
        };
        CollectionSync.Replace(SoftwareRows, rows);

        CollectionSync.Replace(TopMemoryProcesses, r.TopMemoryProcesses.Select(p => new ProcessUsageItemViewModel(p.Name, Formatter.Bytes(p.MemoryBytes), p.ExecutablePath)));
        CollectionSync.Replace(TopCpuProcesses, r.TopCpuProcesses.Select(p => new ProcessUsageItemViewModel(p.Name, Formatter.Percent(p.CpuPercent, 1), p.ExecutablePath)));
    }

    private void BuildFindingCards()
    {
        var relevant = _findings.Where(f => f.Severity > Severity.Info || FindingCategories.Normalize(f.Category) != FindingCategories.Other).ToList();
        var byCategory = relevant
            .GroupBy(f => FindingCategories.Normalize(f.Category))
            .ToDictionary(g => g.Key, g => g.Select(f => new FindingItemViewModel(f, Localizer)).ToList());

        var cards = new List<FindingCardViewModel>();
        foreach (var category in FindingCategories.Canonical)
        {
            byCategory.TryGetValue(category, out var items);
            cards.Add(new FindingCardViewModel(category, items ?? [], Localizer));
        }

        if (byCategory.TryGetValue(FindingCategories.Other, out var others) && others.Count > 0)
            cards.Add(new FindingCardViewModel(FindingCategories.Other, others, Localizer));

        // Cartes avec constats d'abord (plus grave en tête), puis les cartes vides dans l'ordre canonique.
        var ordered = cards.Where(c => c.HasFindings).OrderByDescending(c => c.WorstSeverity)
            .Concat(cards.Where(c => !c.HasFindings));
        CollectionSync.Replace(FindingCards, ordered);
        CollectionSync.Replace(ActiveFindingCards, FindingCards.Where(c => c.HasFindings));
        FindingColumns = Math.Clamp(ActiveFindingCards.Count, 1, 2);
        var clean = FindingCards.Where(c => !c.HasFindings).Select(c => c.Title).ToList();
        CleanCategoriesText = clean.Count == 0 ? string.Empty
            : T("Analysis_Findings_CleanCategories", string.Join(", ", clean));

        FindingCount = _findings.Count(f => f.Severity > Severity.Info);
        FindingSummaryText = FindingCount switch
        {
            0 => T("Analysis_Findings_None"),
            1 => T("Analysis_Findings_One"),
            var n => T("Analysis_Findings_Many", n),
        };
    }

    private void BuildRecommendations()
    {
        if (_report is null) return;
        var dismissed = _settings.Current.DismissedRecommendations;
        var recs = _recommendations.GetRecommendations(_report, _findings, dismissed);
        CollectionSync.Replace(Recommendations, recs.Select(r => new RecommendationItemViewModel(r, Localizer, OpenRecommendation, DismissRecommendationAsync)));
        HasRecommendations = Recommendations.Count > 0;
        DismissedRecommendationCount = dismissed.Count;
        DismissedRecommendationsText = dismissed.Count switch
        {
            0 => string.Empty,
            1 => T("Analysis_Recommendations_OneDismissed"),
            var n => T("Analysis_Recommendations_Dismissed", n),
        };
        OnPropertyChanged(nameof(HasDismissedRecommendations));
    }

    private void BuildProfile(HardwareProfile? profile)
    {
        HardwareProfileReasons.Clear();
        if (profile is null)
        {
            HardwareTierText = Formatter.NotAvailable;
            HardwareTierDescription = string.Empty;
            return;
        }

        HardwareTierText = T($"Analysis_Tier_{profile.Tier}");
        HardwareTierDescription = T($"Analysis_TierDescription_{profile.Tier}");
        foreach (var reason in profile.Reasons) HardwareProfileReasons.Add(new TextItemViewModel(T(reason), Glyphs.Info));
    }

    private string FrequencyText(CpuInfo cpu)
    {
        if (cpu.BaseClockMHz is { } b && cpu.MaxClockMHz is { } m && m > b)
            return T("Analysis_Hw_FrequencyRange", Formatter.Frequency(b), Formatter.Frequency(m));
        return Formatter.Frequency(cpu.MaxClockMHz ?? cpu.BaseClockMHz);
    }

    private string PowerText(PowerStatus power) => power.Source switch
    {
        PowerSource.AC when power.HasBattery && power.BatteryPercent is { } p => T("Analysis_Power_AcBattery", Formatter.Percent(p)),
        PowerSource.AC => T("Analysis_Power_Ac"),
        PowerSource.Battery when power.BatteryPercent is { } p => T("Analysis_Power_Battery", Formatter.Percent(p)),
        PowerSource.Battery => T("Analysis_Power_BatteryUnknown"),
        _ => Formatter.NotAvailable,
    };

    private InfoRowViewModel TemperatureRow(string labelKey, SensorReading reading)
        => reading.HasValue
            ? new InfoRowViewModel(T(labelKey), Formatter.Temperature(reading.Value))
            : new InfoRowViewModel(T(labelKey), Formatter.NotAvailable, T($"Common_Label_Availability_{reading.Availability}"));

    private void OpenRecommendation(RecommendationItemViewModel item)
    {
        if (item.Model.Action is { } action) Navigation.Navigate(action.NavigationTarget, action.OptimizationId);
    }

    private Task DismissRecommendationAsync(RecommendationItemViewModel item) => RunSafeAsync(async ct =>
    {
        await _settings.DismissRecommendationAsync(item.Id, ct).ConfigureAwait(true);
        BuildRecommendations();
    });
}
