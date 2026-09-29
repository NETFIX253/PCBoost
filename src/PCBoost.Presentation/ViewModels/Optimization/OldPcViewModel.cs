using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

public enum OldPcPhase { NotAssessed = 0, Assessing, Assessed, Previewing, Preview, Applying, Report }

/// <summary>Indicateur de l'évaluation (« Mémoire limitée : constaté »).</summary>
public sealed record OldPcIndicatorViewModel(string Label, bool IsDetected, string StatusText)
{
    public string IconGlyph => IsDetected ? Glyphs.Warning : Glyphs.Success;

    public string AccessibleName => $"{Label} : {StatusText}";
}

/// <summary>Niveau d'optimisation (Essentiel / Standard / Avancé) avec ses optimisations.</summary>
public sealed partial class OldPcLevelViewModel
{
    private readonly Func<OldPcLevelViewModel, Task> _select;

    public OldPcLevelViewModel(OldPcLevel level, IReadOnlyList<string> optimizationNames, ILocalizer localizer, Func<OldPcLevelViewModel, Task> select)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        Level = level;
        _select = select;
        Name = localizer.Get($"OldPc_Level_{level}_Name");
        Description = localizer.Get($"OldPc_Level_{level}_Description");
        OptimizationNames = new ObservableCollection<string>(optimizationNames);
        CountText = optimizationNames.Count == 1 ? localizer.Get("OldPc_Level_OneOptimization") : localizer.Format("OldPc_Level_Optimizations", optimizationNames.Count);
        IconGlyph = level == OldPcLevel.Advanced ? Glyphs.Warning : Glyphs.Shield;
        AdvancedNotice = level == OldPcLevel.Advanced ? localizer.Get("OldPc_Level_AdvancedNotice") : string.Empty;
    }

    public OldPcLevel Level { get; }

    public string Name { get; }

    public string Description { get; }

    public ObservableCollection<string> OptimizationNames { get; }

    public string CountText { get; }

    public bool IsEmpty => OptimizationNames.Count == 0;

    public bool IsAdvanced => Level == OldPcLevel.Advanced;

    /// <summary>Le niveau Avancé exige toujours une confirmation.</summary>
    public bool RequiresConfirmation => IsAdvanced;

    public string AdvancedNotice { get; }

    public string IconGlyph { get; }

    [RelayCommand]
    private Task SelectAsync() => _select(this);
}

/// <summary>
/// « Optimiser un ancien PC » (§23) : évaluation (mémoire, processeur, disque mécanique, espace, démarrage, arrière-plan),
/// niveaux Essentiel / Standard / Avancé avec aperçu ; confirmation obligatoire pour Avancé ; rapport et restauration.
/// </summary>
public sealed partial class OldPcViewModel : ViewModelBase
{
    private readonly IOldPcAssistant _assistant;
    private readonly ISystemAnalyzer _analyzer;
    private readonly IOptimizationManager _manager;
    private readonly IRollbackManager _rollback;
    private readonly ISettingsService _settings;
    private SystemAnalysisReport? _analysis;

    public OldPcViewModel(
        ViewModelContext context,
        IOldPcAssistant assistant,
        ISystemAnalyzer analyzer,
        IOptimizationManager manager,
        IRollbackManager rollback,
        ISettingsService settings)
        : base(context)
    {
        _assistant = assistant;
        _analyzer = analyzer;
        _manager = manager;
        _rollback = rollback;
        _settings = settings;
        Steps = OptimizationSteps.Create(Localizer);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotAssessed), nameof(IsAssessing), nameof(IsAssessed), nameof(IsPreviewing), nameof(IsPreview), nameof(IsApplying), nameof(IsReport))]
    [NotifyCanExecuteChangedFor(nameof(AssessCommand), nameof(ApplyCommand))]
    public partial OldPcPhase Phase { get; private set; }

    public bool IsNotAssessed => Phase == OldPcPhase.NotAssessed;

    public bool IsAssessing => Phase == OldPcPhase.Assessing;

    /// <summary>Évaluation affichée, choix du niveau.</summary>
    public bool IsAssessed => Phase == OldPcPhase.Assessed;

    public bool IsPreviewing => Phase == OldPcPhase.Previewing;

    public bool IsPreview => Phase == OldPcPhase.Preview;

    public bool IsApplying => Phase == OldPcPhase.Applying;

    public bool IsReport => Phase == OldPcPhase.Report;

    public ObservableCollection<OldPcIndicatorViewModel> Indicators { get; } = [];

    /// <summary>Constats de l'assistant (langage simple).</summary>
    public ObservableCollection<TextItemViewModel> Findings { get; } = [];

    /// <summary>« Ce PC présente 3 signes de configuration modeste. » / « Ce PC ne présente pas de signe de configuration modeste. »</summary>
    [ObservableProperty]
    public partial string AssessmentSummary { get; private set; } = string.Empty;

    public ObservableCollection<OldPcLevelViewModel> Levels { get; } = [];

    [ObservableProperty]
    public partial OldPcLevelViewModel? SelectedLevel { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial PlanPreviewViewModel? Preview { get; private set; }

    public bool HasPreview => Preview is not null;

    public ObservableCollection<ProgressStepViewModel> Steps { get; }

    [ObservableProperty]
    public partial double ApplyProgressPercent { get; private set; }

    [ObservableProperty]
    public partial string CurrentActionText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial OptimizationReportViewModel? Report { get; private set; }

    public bool HasReport => Report is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial bool IsRestoring { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial bool IsRestored { get; private set; }

    [ObservableProperty]
    public partial string RestoreResultText { get; private set; } = string.Empty;

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (Phase == OldPcPhase.NotAssessed && _analyzer.LastReport is { } report)
            await AssessCoreAsync(report, cancellationToken).ConfigureAwait(true);
    }

    protected override void OnDeactivated() => AssessCancelCommand.Execute(null);

    /// <summary>Évalue le PC (analyse si nécessaire).</summary>
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanAssess))]
    private async Task AssessAsync(CancellationToken cancellationToken)
    {
        var fresh = _analyzer.LastReport is { } last && Context.Clock.UtcNow - last.Timestamp <= OptimizationViewModel.MaxAnalysisAge ? last : null;
        await AssessCoreAsync(fresh, cancellationToken).ConfigureAwait(true);
    }

    private bool CanAssess() => Phase is not (OldPcPhase.Assessing or OldPcPhase.Applying or OldPcPhase.Previewing);

    private async Task AssessCoreAsync(SystemAnalysisReport? report, CancellationToken cancellationToken)
    {
        ErrorText = null;
        Phase = OldPcPhase.Assessing;
        var ok = await RunSafeAsync(async ct =>
        {
            _analysis = report ?? await _analyzer.AnalyzeAsync(new AnalysisOptions(), null, ct).ConfigureAwait(true);
            var assessment = await _assistant.AssessAsync(_analysis, ct).ConfigureAwait(true);
            ApplyAssessment(assessment);
            Phase = OldPcPhase.Assessed;
        }, cancellationToken, trackBusy: false).ConfigureAwait(true);
        if (!ok) Phase = OldPcPhase.NotAssessed;
    }

    private void ApplyAssessment(OldPcAssessment a)
    {
        string Status(bool detected) => T(detected ? "OldPc_Indicator_Detected" : "OldPc_Indicator_NotDetected");
        var indicators = new (string Key, bool Value)[]
        {
            ("OldPc_Indicator_LowMemory", a.LowMemory),
            ("OldPc_Indicator_WeakCpu", a.OldOrWeakCpu),
            ("OldPc_Indicator_Hdd", a.MechanicalSystemDrive),
            ("OldPc_Indicator_LowDisk", a.LowDiskSpace),
            ("OldPc_Indicator_HeavyStartup", a.HeavyStartup),
            ("OldPc_Indicator_Background", a.ManyBackgroundProcesses),
        };
        CollectionSync.Replace(Indicators, indicators.Select(i => new OldPcIndicatorViewModel(T(i.Key), i.Value, Status(i.Value))));
        CollectionSync.Replace(Findings, a.Findings.Select(f => new TextItemViewModel(T(f), Glyphs.Info)));

        var detected = indicators.Count(i => i.Value);
        AssessmentSummary = detected switch
        {
            0 => T("OldPc_Summary_None"),
            1 => T("OldPc_Summary_One"),
            var n => T("OldPc_Summary_Many", n),
        };

        CollectionSync.Replace(Levels, Enum.GetValues<OldPcLevel>().Select(level =>
        {
            var ids = a.OptimizationsByLevel.TryGetValue(level, out var list) ? list : [];
            var names = ids.Select(id => _manager.Find(id) is { } o ? T(o.Name) : id).ToList();
            return new OldPcLevelViewModel(level, names, Localizer, SelectLevelAsync);
        }));
    }

    private async Task SelectLevelAsync(OldPcLevelViewModel level)
    {
        if (_analysis is null || Phase is OldPcPhase.Applying or OldPcPhase.Previewing) return;
        ErrorText = null;
        SelectedLevel = level;
        Report = null;
        IsRestored = false;
        RestoreResultText = string.Empty;
        Phase = OldPcPhase.Previewing;
        var ok = await RunSafeAsync(async ct =>
        {
            var plan = await _assistant.BuildPlanAsync(level.Level, _analysis, ct).ConfigureAwait(true);
            Preview = new PlanPreviewViewModel(plan, Localizer, Formatter, id => _manager.Find(id)?.Description);
            Preview.SelectionChanged += (_, _) => ApplyCommand.NotifyCanExecuteChanged();
            Phase = OldPcPhase.Preview;
        }).ConfigureAwait(true);
        if (!ok) Phase = OldPcPhase.Assessed;
    }

    /// <summary>Applique le niveau choisi. Le niveau Avancé demande toujours une confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (Preview is not { HasSelection: true } preview || SelectedLevel is not { } level) return;

        var alwaysConfirm = level.RequiresConfirmation || (_settings.Current.ConfirmSensitiveOperations && preview.RequiresElevation);
        var confirmation = preview.BuildConfirmation(alwaysConfirm, level.IsAdvanced ? "OldPc_Confirm_AdvancedTitle" : "Optimization_Confirm_Title");
        if (confirmation is not null && level.IsAdvanced)
            confirmation = confirmation with { Message = TextRef.Of("OldPc_Confirm_AdvancedMessage", preview.SelectedChangeCount), IsDestructive = true };

        var confirmed = false;
        if (confirmation is not null)
        {
            if (await Dialogs.ConfirmAsync(confirmation).ConfigureAwait(true) != DialogResultKind.Primary) return;
            confirmed = true;
        }

        var plan = preview.BuildPlan(confirmed && preview.HasIrreversibleSelected, confirmed && preview.HasHighRiskSelected);
        ProgressSteps.Reset(Steps);
        ApplyProgressPercent = 0;
        Phase = OldPcPhase.Applying;
        var ok = await RunSafeAsync(async ct =>
        {
            var progress = UiProgress<OptimizationProgress>(p =>
            {
                OptimizationSteps.Apply(Steps, p);
                ApplyProgressPercent = Math.Clamp(p.Percent, 0, 100);
                CurrentActionText = p.CurrentAction is { } action ? T(action) : T($"Optimization_StepRunning_{p.Stage}");
            });
            var report = await _manager.ExecuteAsync(plan, _analysis, progress, ct).ConfigureAwait(true);
            ProgressSteps.CompleteAll(Steps);
            Report = new OptimizationReportViewModel(report, Localizer, Formatter, id => _manager.Find(id)?.Name);
            Preview = null;
            Phase = OldPcPhase.Report;
        }, linkToPage: false).ConfigureAwait(true);
        if (!ok)
        {
            ProgressSteps.FailCurrent(Steps);
            Phase = OldPcPhase.Preview;
        }
    }

    private bool CanApply() => Phase == OldPcPhase.Preview && Preview?.HasSelection == true;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        if (Report is not { CanRestore: true } report) return;
        IsRestoring = true;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var result = await _rollback.RestoreSessionAsync(report.SessionId, ct).ConfigureAwait(true);
                RestoreResultText = RestoreText.Describe(Localizer, result);
                IsRestored = result.Success;
                if (!result.Success && result.Errors.FirstOrDefault(e => !e.Success) is { } err) CheckResult(err);
            }, linkToPage: false).ConfigureAwait(true);
        }
        finally
        {
            IsRestoring = false;
        }
    }

    private bool CanRestore() => Report?.CanRestore == true && !IsRestoring && !IsRestored;

    /// <summary>Retour au choix du niveau.</summary>
    [RelayCommand]
    private void BackToLevels()
    {
        Preview = null;
        Report = null;
        SelectedLevel = null;
        Phase = _analysis is null ? OldPcPhase.NotAssessed : OldPcPhase.Assessed;
    }

    [RelayCommand]
    private void OpenHistory() => Navigation.Navigate(PageKeys.History);
}
