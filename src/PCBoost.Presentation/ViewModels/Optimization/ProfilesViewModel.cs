using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

public enum ProfilePhase { List = 0, Previewing, Preview, Applying, Result }

/// <summary>Profil d'utilisation affiché dans la liste.</summary>
public sealed partial class ProfileItemViewModel : ObservableObject
{
    private readonly Func<ProfileItemViewModel, Task> _preview;

    public ProfileItemViewModel(PerformanceProfile profile, ILocalizer localizer, Func<ProfileItemViewModel, Task> preview)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(localizer);
        Model = profile;
        _preview = preview;
        Id = profile.Id;
        Name = localizer.Format(profile.Name);
        Description = localizer.Format(profile.Description);
        IconGlyph = string.IsNullOrEmpty(profile.IconGlyph) ? Glyphs.Profiles : profile.IconGlyph;
        IsCustom = profile.Id == PerformanceProfile.CustomId;
        OptimizationCountText = profile.OptimizationIds.Count == 1
            ? localizer.Get("Profiles_OneOptimization")
            : localizer.Format("Profiles_Optimizations", profile.OptimizationIds.Count);
        ActiveBadgeText = localizer.Get("Profiles_ActiveBadge");
    }

    public PerformanceProfile Model { get; }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public string IconGlyph { get; }

    public bool IsBuiltIn => Model.IsBuiltIn;

    public bool IsCustom { get; }

    /// <summary>« 4 optimisations ».</summary>
    public string OptimizationCountText { get; }

    public bool IsEmpty => Model.OptimizationIds.Count == 0;

    /// <summary>« Actif ».</summary>
    public string ActiveBadgeText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial bool IsActive { get; set; }

    public string AccessibleName => IsActive ? $"{Name}, {ActiveBadgeText}. {Description}" : $"{Name}. {Description}";

    /// <summary>Aperçu avant activation (rien n'est modifié).</summary>
    [RelayCommand]
    private Task PreviewAsync() => _preview(this);
}

/// <summary>Optimisation proposée dans l'éditeur du profil personnalisé.</summary>
public sealed partial class CustomProfileOptionViewModel : ObservableObject
{
    public CustomProfileOptionViewModel(Core.Optimization.IOptimization optimization, ILocalizer localizer, bool included)
    {
        ArgumentNullException.ThrowIfNull(optimization);
        ArgumentNullException.ThrowIfNull(localizer);
        Id = optimization.Id;
        Name = localizer.Format(optimization.Name);
        Description = localizer.Format(optimization.Description);
        CategoryText = localizer.Get($"Profiles_Category_{optimization.Category}");
        RiskText = localizer.Risk(optimization.RiskLevel);
        RiskGlyph = Labels.RiskGlyph(optimization.RiskLevel);
        ImpactText = localizer.Impact(optimization.ImpactLevel);
        ReversibleText = localizer.Reversibility(optimization.IsReversible);
        IsIncluded = included;
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public string CategoryText { get; }

    public string RiskText { get; }

    public string RiskGlyph { get; }

    public string ImpactText { get; }

    public string ReversibleText { get; }

    [ObservableProperty]
    public partial bool IsIncluded { get; set; }
}

/// <summary>
/// Profils (§15) : liste, profil actif, aperçu avant activation, activer / désactiver (restauration de l'état
/// précédent), édition du profil personnalisé par cases à cocher des optimisations disponibles.
/// </summary>
public sealed partial class ProfilesViewModel : ViewModelBase
{
    private readonly IProfileService _profiles;
    private readonly IOptimizationManager _manager;
    private readonly ISettingsService _settings;

    public ProfilesViewModel(ViewModelContext context, IProfileService profiles, IOptimizationManager manager, ISettingsService settings)
        : base(context)
    {
        _profiles = profiles;
        _manager = manager;
        _settings = settings;
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsList), nameof(IsPreviewing), nameof(IsPreview), nameof(IsApplying), nameof(IsResult))]
    [NotifyCanExecuteChangedFor(nameof(ActivateCommand), nameof(DeactivateCommand))]
    public partial ProfilePhase Phase { get; private set; }

    public bool IsList => Phase == ProfilePhase.List;

    public bool IsPreviewing => Phase == ProfilePhase.Previewing;

    public bool IsPreview => Phase == ProfilePhase.Preview;

    public bool IsApplying => Phase == ProfilePhase.Applying;

    public bool IsResult => Phase == ProfilePhase.Result;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeactivateCommand))]
    public partial bool HasActiveProfile { get; private set; }

    /// <summary>« Profil actif : Gaming » / « Aucun profil actif ».</summary>
    [ObservableProperty]
    public partial string ActiveProfileText { get; private set; } = string.Empty;

    /// <summary>« Activé il y a 2 h ».</summary>
    [ObservableProperty]
    public partial string ActivatedAtText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial ProfileItemViewModel? SelectedProfile { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial PlanPreviewViewModel? Preview { get; private set; }

    public bool HasPreview => Preview is not null;

    /// <summary>« Voici les modifications prévues pour le profil Gaming ».</summary>
    [ObservableProperty]
    public partial string PreviewTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    public partial OptimizationReportViewModel? Report { get; private set; }

    public bool HasReport => Report is not null;

    /// <summary>Résultat d'une désactivation (« 5 modifications restaurées. »).</summary>
    [ObservableProperty]
    public partial string ResultText { get; private set; } = string.Empty;

    // --- Profil personnalisé ---
    [ObservableProperty]
    public partial bool IsEditingCustom { get; private set; }

    public ObservableCollection<CustomProfileOptionViewModel> CustomOptions { get; } = [];

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        _profiles.StateChanged += OnStateChanged;
        await _profiles.LoadAsync(cancellationToken).ConfigureAwait(true);
        LoadProfiles();
    }

    protected override void OnDeactivated() => _profiles.StateChanged -= OnStateChanged;

    private void OnStateChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        if (IsActive) LoadProfiles();
    });

    private void LoadProfiles()
    {
        var state = _profiles.State;
        CollectionSync.Replace(Profiles, _profiles.GetProfiles().Select(p => new ProfileItemViewModel(p, Localizer, PreviewProfileAsync)
        {
            IsActive = p.Id == state.ActiveProfileId,
        }));
        var active = Profiles.FirstOrDefault(p => p.IsActive);
        HasActiveProfile = active is not null;
        ActiveProfileText = active is null ? T("Profiles_NoActive") : T("Profiles_Active", active.Name);
        ActivatedAtText = active is not null && state.ActivatedAt is { } at ? T("Profiles_ActivatedAt", Formatter.DateTime(at)) : string.Empty;
    }

    private async Task PreviewProfileAsync(ProfileItemViewModel profile)
    {
        if (Phase is ProfilePhase.Applying or ProfilePhase.Previewing) return;
        ErrorText = null;
        ResultText = string.Empty;
        Report = null;
        SelectedProfile = profile;
        IsEditingCustom = false;
        Phase = ProfilePhase.Previewing;
        var ok = await RunSafeAsync(async ct =>
        {
            var plan = await _profiles.PreviewActivationAsync(profile.Id, ct).ConfigureAwait(true);
            Preview = new PlanPreviewViewModel(plan, Localizer, Formatter, id => _manager.Find(id)?.Description);
            Preview.SelectionChanged += (_, _) => ActivateCommand.NotifyCanExecuteChanged();
            PreviewTitle = T("Profiles_PreviewTitle", profile.Name);
            Phase = ProfilePhase.Preview;
        }).ConfigureAwait(true);
        if (!ok) Phase = ProfilePhase.List;
    }

    /// <summary>Active le profil de l'aperçu (le profil précédent est d'abord restauré par le service).</summary>
    [RelayCommand(CanExecute = nameof(CanActivate))]
    private async Task ActivateAsync()
    {
        if (Preview is not { } preview || SelectedProfile is not { } profile) return;

        var confirmation = preview.BuildConfirmation(_settings.Current.ConfirmSensitiveOperations, "Profiles_Confirm_Title")
            ?? new ConfirmationRequest(
                TextRef.Of("Profiles_Confirm_Title"),
                TextRef.Of("Profiles_Confirm_Message", profile.Name),
                TextRef.Of("Profiles_Action_Activate"),
                CloseButton: TextRef.Of("Common_Action_Cancel"));
        var answer = await Dialogs.ConfirmAsync(confirmation).ConfigureAwait(true);
        if (answer != DialogResultKind.Primary) return;

        var plan = preview.BuildPlan(preview.HasIrreversibleSelected, preview.HasHighRiskSelected);
        Phase = ProfilePhase.Applying;
        var ok = await RunSafeAsync(async ct =>
        {
            var report = await _profiles.ActivateAsync(plan, ct).ConfigureAwait(true);
            Report = new OptimizationReportViewModel(report, Localizer, Formatter, id => _manager.Find(id)?.Name,
                id => _profiles.GetProfiles().FirstOrDefault(p => p.Id == id)?.Name);
            ResultText = report.Status is SessionStatus.Completed or SessionStatus.PartiallyCompleted
                ? T("Profiles_Activated", profile.Name)
                : string.Empty;
            Preview = null;
            Phase = ProfilePhase.Result;
            LoadProfiles();
        }, linkToPage: false).ConfigureAwait(true);
        if (!ok) Phase = ProfilePhase.Preview;
    }

    private bool CanActivate() => Phase == ProfilePhase.Preview && Preview is { HasSelection: true } or { IsEmpty: true };

    /// <summary>Désactive le profil actif : retour à l'état d'avant le profil.</summary>
    [RelayCommand(CanExecute = nameof(CanDeactivate))]
    private async Task DeactivateAsync()
    {
        var answer = await Dialogs.ConfirmAsync(new ConfirmationRequest(
            TextRef.Of("Profiles_Deactivate_ConfirmTitle"),
            TextRef.Of("Profiles_Deactivate_ConfirmMessage"),
            TextRef.Of("Profiles_Action_Deactivate"),
            CloseButton: TextRef.Of("Common_Action_Cancel"))).ConfigureAwait(true);
        if (answer != DialogResultKind.Primary) return;

        var previous = Phase;
        Phase = ProfilePhase.Applying;
        var ok = await RunSafeAsync(async ct =>
        {
            var result = await _profiles.DeactivateAsync(ct).ConfigureAwait(true);
            ResultText = T("Profiles_Deactivated") + " " + RestoreText.Describe(Localizer, result);
            if (!result.Success && result.Errors.FirstOrDefault(e => !e.Success) is { } err) CheckResult(err);
            Report = null;
            Preview = null;
            Phase = ProfilePhase.Result;
            LoadProfiles();
        }, linkToPage: false).ConfigureAwait(true);
        if (!ok) Phase = previous;
    }

    private bool CanDeactivate() => HasActiveProfile && Phase is ProfilePhase.List or ProfilePhase.Result or ProfilePhase.Preview;

    [RelayCommand]
    private void BackToList()
    {
        Preview = null;
        Report = null;
        SelectedProfile = null;
        IsEditingCustom = false;
        Phase = ProfilePhase.List;
    }

    /// <summary>Ouvre l'éditeur du profil personnalisé (cases des optimisations disponibles).</summary>
    [RelayCommand]
    private void EditCustom()
    {
        var custom = _profiles.GetProfiles().FirstOrDefault(p => p.Id == PerformanceProfile.CustomId);
        var included = new HashSet<string>(custom?.OptimizationIds ?? [], StringComparer.Ordinal);
        CollectionSync.Replace(CustomOptions, _manager.Optimizations
            .OrderBy(o => o.Category)
            .ThenBy(o => o.Id, StringComparer.Ordinal)
            .Select(o => new CustomProfileOptionViewModel(o, Localizer, included.Contains(o.Id))));
        IsEditingCustom = true;
        Phase = ProfilePhase.List;
    }

    [RelayCommand]
    private async Task SaveCustomAsync()
    {
        var ids = CustomOptions.Where(o => o.IsIncluded).Select(o => o.Id).ToList();
        var ok = await RunSafeAsync(ct => _profiles.SaveCustomProfileAsync(ids, ct)).ConfigureAwait(true);
        if (!ok) return;
        IsEditingCustom = false;
        StatusMessage = T("Profiles_CustomSaved");
        LoadProfiles();
    }

    [RelayCommand]
    private void CancelCustomEdit() => IsEditingCustom = false;

    [RelayCommand]
    private void OpenHistory() => Navigation.Navigate(PageKeys.History);
}
