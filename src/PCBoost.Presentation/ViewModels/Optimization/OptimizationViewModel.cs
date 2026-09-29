using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

public enum OptimizationPhase { Idle = 0, Scanning, Preview, Applying, Report }

/// <summary>
/// « Optimiser mon PC » (§9, §39, §71). Machine d'états Idle → Scanning → Preview → Applying → Report.
/// Rien n'est modifié avant la confirmation de l'aperçu ; confirmation obligatoire pour les actions irréversibles
/// ou à risque élevé ; rapport final avec restauration. Sections imbriquées : <see cref="Profiles"/> et <see cref="OldPc"/>.
/// Paramètre de navigation : identifiant d'optimisation (aperçu ciblé), « profiles » ou « oldpc » (section).
/// </summary>
public sealed partial class OptimizationViewModel : ViewModelBase
{
    /// <summary>Une analyse plus ancienne est refaite avant de construire le plan.</summary>
    internal static readonly TimeSpan MaxAnalysisAge = TimeSpan.FromMinutes(10);

    private readonly IOptimizationManager _manager;
    private readonly ISystemAnalyzer _analyzer;
    private readonly IRollbackManager _rollback;
    private readonly ISettingsService _settings;
    private readonly IProfileService _profileService;
    private SystemAnalysisReport? _analysis;
    private bool _childrenActive;

    public OptimizationViewModel(
        ViewModelContext context,
        IOptimizationManager manager,
        ISystemAnalyzer analyzer,
        IRollbackManager rollback,
        ISettingsService settings,
        IProfileService profileService,
        ProfilesViewModel profiles,
        OldPcViewModel oldPc)
        : base(context)
    {
        _manager = manager;
        _analyzer = analyzer;
        _rollback = rollback;
        _settings = settings;
        _profileService = profileService;
        Profiles = profiles;
        OldPc = oldPc;
        Steps = OptimizationSteps.Create(Localizer);
    }

    /// <summary>Section Profils (liste, aperçu, activation, profil personnalisé).</summary>
    public ProfilesViewModel Profiles { get; }

    /// <summary>Section « Optimiser un ancien PC ».</summary>
    public OldPcViewModel OldPc { get; }

    /// <summary>0 = Optimisation en un clic, 1 = Profils, 2 = Ancien PC.</summary>
    [ObservableProperty]
    public partial int SelectedSectionIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsScanning), nameof(IsPreview), nameof(IsApplying), nameof(IsReport))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(ApplyCommand), nameof(BackToStartCommand))]
    public partial OptimizationPhase Phase { get; private set; }

    public bool IsIdle => Phase == OptimizationPhase.Idle;

    public bool IsScanning => Phase == OptimizationPhase.Scanning;

    public bool IsPreview => Phase == OptimizationPhase.Preview;

    public bool IsApplying => Phase == OptimizationPhase.Applying;

    public bool IsReport => Phase == OptimizationPhase.Report;

    /// <summary>« Analyse de votre PC… », « Préparation de l'aperçu… ».</summary>
    [ObservableProperty]
    public partial string ScanStatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double ScanProgressPercent { get; private set; }

    /// <summary>Aperçu (Phase = Preview) ; null sinon.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial PlanPreviewViewModel? Preview { get; private set; }

    public bool HasPreview => Preview is not null;

    /// <summary>Étapes d'exécution (Analyse → Préparation → Sauvegarde → Optimisation → Vérification → Terminé).</summary>
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

    /// <summary>Résultat de la restauration (« 4 modifications restaurées. »).</summary>
    [ObservableProperty]
    public partial string RestoreResultText { get; private set; } = string.Empty;

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        // La section demandée s'affiche immédiatement, sans attendre le chargement des profils ni l'évaluation « PC ancien ».
        string? scanOptimizationId = null;
        switch (parameter as string)
        {
            case PageRegistry.Profiles:
                SelectedSectionIndex = 1;
                break;
            case PageKeys.OldPc:
                SelectedSectionIndex = 2;
                break;
            case { Length: > 0 } optimizationId when _manager.Find(optimizationId) is not null:
                SelectedSectionIndex = 0;
                scanOptimizationId = optimizationId;
                break;
        }

        _childrenActive = true;
        await Profiles.OnNavigatedToAsync(null).ConfigureAwait(true);
        await OldPc.OnNavigatedToAsync(null).ConfigureAwait(true);

        if (scanOptimizationId is not null)
            await ScanCoreAsync([scanOptimizationId], cancellationToken).ConfigureAwait(true);
    }

    protected override void OnDeactivated()
    {
        ScanCancelCommand.Execute(null);
        if (_childrenActive)
        {
            Profiles.OnNavigatedFrom();
            OldPc.OnNavigatedFrom();
            _childrenActive = false;
        }
    }

    /// <summary>« Optimiser mon PC » : analyse puis aperçu du plan (rien n'est modifié).</summary>
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanScan))]
    private Task ScanAsync(CancellationToken cancellationToken) => ScanCoreAsync(null, cancellationToken);

    private bool CanScan() => Phase is OptimizationPhase.Idle or OptimizationPhase.Report or OptimizationPhase.Preview;

    private async Task ScanCoreAsync(IReadOnlyCollection<string>? optimizationIds, CancellationToken cancellationToken)
    {
        ErrorText = null;
        StatusMessage = null;
        Report = null;
        Preview = null;
        IsRestored = false;
        RestoreResultText = string.Empty;
        ScanProgressPercent = 0;
        ScanStatusText = T("Optimization_Scan_Analyzing");
        Phase = OptimizationPhase.Scanning;

        var ok = await RunSafeAsync(async ct =>
        {
            _analysis = await GetFreshAnalysisAsync(ct).ConfigureAwait(true);
            ScanStatusText = T("Optimization_Scan_Preparing");
            ScanProgressPercent = 100;
            var plan = optimizationIds is null
                ? await _manager.BuildOneClickPlanAsync(_analysis, ct).ConfigureAwait(true)
                : await _manager.BuildPlanAsync(SessionType.Manual, optimizationIds, _analysis, null, ct).ConfigureAwait(true);
            Preview = new PlanPreviewViewModel(plan, Localizer, Formatter, id => _manager.Find(id)?.Description);
            Preview.SelectionChanged += (_, _) => ApplyCommand.NotifyCanExecuteChanged();
            Phase = OptimizationPhase.Preview;
        }, cancellationToken, trackBusy: false).ConfigureAwait(true);

        if (!ok) Phase = OptimizationPhase.Idle;
    }

    private async Task<SystemAnalysisReport> GetFreshAnalysisAsync(CancellationToken cancellationToken)
    {
        if (_analyzer.LastReport is { } last && Context.Clock.UtcNow - last.Timestamp <= MaxAnalysisAge) return last;
        var progress = UiProgress<AnalysisProgress>(p =>
        {
            ScanProgressPercent = Math.Clamp(p.Percent, 0, 100);
            ScanStatusText = T($"Analysis_StepRunning_{p.Stage}");
        });
        return await _analyzer.AnalyzeAsync(new AnalysisOptions(), progress, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Applique la sélection après confirmation (obligatoire si irréversible ou risque élevé).</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (Preview is not { } preview || !preview.HasSelection) return;

        var confirmation = preview.BuildConfirmation(_settings.Current.ConfirmSensitiveOperations && preview.RequiresElevation);
        var confirmed = false;
        if (confirmation is not null)
        {
            var answer = await Dialogs.ConfirmAsync(confirmation).ConfigureAwait(true);
            if (answer != DialogResultKind.Primary) return;
            confirmed = true;
        }

        var plan = preview.BuildPlan(confirmed && preview.HasIrreversibleSelected, confirmed && preview.HasHighRiskSelected);
        ProgressSteps.Reset(Steps);
        ApplyProgressPercent = 0;
        CurrentActionText = string.Empty;
        Phase = OptimizationPhase.Applying;

        // L'exécution n'est pas interrompue si l'utilisateur quitte la page : les modifications restent consignées.
        var ok = await RunSafeAsync(async ct =>
        {
            var progress = UiProgress<OptimizationProgress>(p =>
            {
                OptimizationSteps.Apply(Steps, p);
                ApplyProgressPercent = Math.Clamp(p.Percent, 0, 100);
                CurrentActionText = p.CurrentAction is { } a ? T(a) : T($"Optimization_StepRunning_{p.Stage}");
            });
            var report = await _manager.ExecuteAsync(plan, _analysis, progress, ct).ConfigureAwait(true);
            ProgressSteps.CompleteAll(Steps);
            ApplyProgressPercent = 100;
            Report = new OptimizationReportViewModel(report, Localizer, Formatter, id => _manager.Find(id)?.Name, ProfileName);
            Preview = null;
            Phase = OptimizationPhase.Report;
        }, linkToPage: false).ConfigureAwait(true);

        if (!ok)
        {
            ProgressSteps.FailCurrent(Steps);
            Phase = OptimizationPhase.Preview;
        }
    }

    private bool CanApply() => Phase == OptimizationPhase.Preview && Preview?.HasSelection == true;

    /// <summary>Abandonne l'aperçu : aucune modification n'a été faite.</summary>
    [RelayCommand]
    private void CancelPreview()
    {
        Preview = null;
        Phase = OptimizationPhase.Idle;
        StatusMessage = T("Optimization_Preview_Cancelled");
    }

    /// <summary>Restaure toutes les modifications de la session du rapport.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        if (Report is not { CanRestore: true } report) return;
        var answer = await Dialogs.ConfirmAsync(new ConfirmationRequest(
            TextRef.Of("Optimization_Restore_ConfirmTitle"),
            TextRef.Of("Optimization_Restore_ConfirmMessage"),
            TextRef.Of("Common_Action_Restore"),
            CloseButton: TextRef.Of("Common_Action_Cancel"))).ConfigureAwait(true);
        if (answer != DialogResultKind.Primary) return;

        IsRestoring = true;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var result = await _rollback.RestoreSessionAsync(report.SessionId, ct).ConfigureAwait(true);
                RestoreResultText = RestoreText.Describe(Localizer, result);
                IsRestored = result.Success;
                if (!result.Success && result.Errors.FirstOrDefault(e => !e.Success) is { } firstError) CheckResult(firstError);
            }, linkToPage: false).ConfigureAwait(true);
        }
        finally
        {
            IsRestoring = false;
        }
    }

    private bool CanRestore() => Report?.CanRestore == true && !IsRestoring && !IsRestored;

    [RelayCommand(CanExecute = nameof(CanGoBackToStart))]
    private void BackToStart()
    {
        Report = null;
        Preview = null;
        Phase = OptimizationPhase.Idle;
    }

    private bool CanGoBackToStart() => Phase is OptimizationPhase.Report or OptimizationPhase.Preview;

    [RelayCommand]
    private void OpenHistory() => Navigation.Navigate(PageKeys.History);

    private TextRef? ProfileName(string profileId) => _profileService.GetProfiles().FirstOrDefault(p => p.Id == profileId)?.Name;
}
