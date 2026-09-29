using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Services;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

public enum WelcomePhase { Intro = 0, Analyzing, Results }

/// <summary>
/// Premier lancement (§61) : analyse guidée (système → démarrage → stockage → processus → résultats),
/// puis principales sources potentielles de ralentissement. Termine en enregistrant <c>FirstRunCompleted</c>.
/// </summary>
public sealed partial class WelcomeViewModel : ViewModelBase
{
    private const int MaxFindings = 5;
    private readonly ISystemAnalyzer _analyzer;
    private readonly IHealthRulesEngine _rules;
    private readonly ISettingsService _settings;

    public WelcomeViewModel(ViewModelContext context, ISystemAnalyzer analyzer, IHealthRulesEngine rules, ISettingsService settings)
        : base(context)
    {
        _analyzer = analyzer;
        _rules = rules;
        _settings = settings;
        Steps = new ObservableCollection<ProgressStepViewModel>(
            Enum.GetValues<AnalysisStage>().Select(s => new ProgressStepViewModel((int)s, T($"Analysis_Step_{s}"), StepText)));
    }

    public ObservableCollection<ProgressStepViewModel> Steps { get; }

    public ObservableCollection<FindingItemViewModel> MainFindings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIntro), nameof(IsAnalyzing), nameof(IsResults))]
    public partial WelcomePhase Phase { get; private set; }

    public bool IsIntro => Phase == WelcomePhase.Intro;

    public bool IsAnalyzing => Phase == WelcomePhase.Analyzing;

    public bool IsResults => Phase == WelcomePhase.Results;

    /// <summary>Progression globale 0–100.</summary>
    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    /// <summary>Étape en cours (« Analyse du système… »).</summary>
    [ObservableProperty]
    public partial string CurrentStepText { get; private set; } = string.Empty;

    /// <summary>« Voici les principales sources potentielles de ralentissement » ou message rassurant si aucune.</summary>
    [ObservableProperty]
    public partial string ResultsTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultsSubtitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasFindings { get; private set; }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        ErrorText = null;
        MainFindings.Clear();
        ProgressSteps.Reset(Steps);
        ProgressPercent = 0;
        Phase = WelcomePhase.Analyzing;

        var ok = await RunSafeAsync(async ct =>
        {
            var progress = UiProgress<AnalysisProgress>(OnProgress);
            var report = await _analyzer.AnalyzeAsync(new AnalysisOptions(), progress, ct).ConfigureAwait(true);
            var findings = _rules.Evaluate(report, _settings.Current.Thresholds);
            foreach (var f in findings.Where(f => f.Severity > Core.Common.Severity.Info)
                                      .OrderByDescending(f => f.Severity)
                                      .Take(MaxFindings))
            {
                MainFindings.Add(new FindingItemViewModel(f, Localizer));
            }

            ProgressSteps.CompleteAll(Steps);
            ProgressPercent = 100;
            HasFindings = MainFindings.Count > 0;
            ResultsTitle = HasFindings ? T("Welcome_Results_Title") : T("Welcome_Results_NoneTitle");
            ResultsSubtitle = HasFindings ? T("Welcome_Results_Subtitle") : T("Welcome_Results_NoneSubtitle");
            Phase = WelcomePhase.Results;
        }, cancellationToken).ConfigureAwait(true);

        if (!ok)
        {
            ProgressSteps.FailCurrent(Steps);
            Phase = WelcomePhase.Intro;
        }
    }

    /// <summary>Termine l'accueil : enregistre le premier lancement et ouvre le tableau de bord.</summary>
    [RelayCommand]
    private async Task FinishAsync()
    {
        await CompleteFirstRunAsync().ConfigureAwait(true);
        Navigation.Navigate(PageKeys.Home);
    }

    /// <summary>Passe l'accueil sans analyse.</summary>
    [RelayCommand]
    private async Task SkipAsync()
    {
        StartCancelCommand.Execute(null);
        await CompleteFirstRunAsync().ConfigureAwait(true);
        Navigation.Navigate(PageKeys.Home);
    }

    /// <summary>Termine l'accueil et ouvre directement l'optimisation.</summary>
    [RelayCommand]
    private async Task OptimizeNowAsync()
    {
        await CompleteFirstRunAsync().ConfigureAwait(true);
        Navigation.Navigate(PageKeys.Optimization);
    }

    private Task CompleteFirstRunAsync() => RunSafeAsync(async ct =>
    {
        var settings = _settings.Current.Clone();
        if (settings.FirstRunCompleted) return;
        settings.FirstRunCompleted = true;
        await _settings.SaveAsync(settings, ct).ConfigureAwait(true);
    }, linkToPage: false);

    private void OnProgress(AnalysisProgress progress)
    {
        ProgressSteps.Advance(Steps, (int)progress.Stage);
        ProgressPercent = Math.Clamp(progress.Percent, 0, 100);
        CurrentStepText = T($"Analysis_StepRunning_{progress.Stage}");
    }

    private string StepText(StepState state) => T($"Common_Label_Step_{state}");

    protected override void OnDeactivated() => StartCancelCommand.Execute(null);
}
