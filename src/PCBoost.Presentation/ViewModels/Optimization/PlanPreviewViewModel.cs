using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Optimization;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Modification prévue (case à cocher) dans l'aperçu d'une optimisation.</summary>
public sealed partial class PlannedChangeItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;

    public PlannedChangeItemViewModel(PlannedChange change, OptimizationPreviewItemViewModel owner, ILocalizer localizer, IValueFormatter formatter, Action selectionChanged)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Model = change;
        Owner = owner;
        // Sélection initiale posée avant l'abonnement : le parent n'est pas encore construit.
        IsSelected = change.SelectedByDefault;
        _selectionChanged = selectionChanged;
        Id = change.Id;
        Description = localizer.Format(change.Description);
        Target = change.Target;
        IsReversible = change.Reversible;
        ReversibleText = localizer.Reversibility(change.Reversible);
        ReversibleGlyph = Labels.ReversibilityGlyph(change.Reversible);
        RiskText = localizer.Risk(change.Risk);
        RiskGlyph = Labels.RiskGlyph(change.Risk);
        EstimatedBytesText = change.EstimatedBytes is > 0 ? formatter.Bytes(change.EstimatedBytes) : string.Empty;
    }

    public PlannedChange Model { get; }

    public OptimizationPreviewItemViewModel Owner { get; }

    public string Id { get; }

    public string Description { get; }

    /// <summary>Cible lisible (clé, programme, dossier…), utile en mode Expert.</summary>
    public string Target { get; }

    public bool IsReversible { get; }

    public string ReversibleText { get; }

    public string ReversibleGlyph { get; }

    public string RiskText { get; }

    public string RiskGlyph { get; }

    public bool IsHighRisk => Model.Risk == RiskLevel.High;

    public string EstimatedBytesText { get; }

    public bool HasEstimatedBytes => !string.IsNullOrEmpty(EstimatedBytesText);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string AccessibleName => $"{Description}, {RiskText}, {ReversibleText}";

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke();
}

/// <summary>Optimisation dans l'aperçu (§39) : nom, description, risque, impact, réversibilité, élévation, modifications.</summary>
public sealed partial class OptimizationPreviewItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private readonly ILocalizer _localizer;
    private readonly IValueFormatter _formatter;
    private bool _bulkUpdate;

    public OptimizationPreviewItemViewModel(OptimizationPreview preview, string description, ILocalizer localizer, IValueFormatter formatter, Action selectionChanged)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _selectionChanged = selectionChanged;
        Model = preview;
        OptimizationId = preview.OptimizationId;
        Name = localizer.Format(preview.Name);
        Description = description;
        IsApplicable = preview.Applicable;
        NotApplicableReason = preview.NotApplicableReason is { } r ? localizer.Format(r) : string.Empty;
        RiskText = localizer.Risk(preview.Risk);
        RiskGlyph = Labels.RiskGlyph(preview.Risk);
        ImpactText = localizer.Impact(preview.Impact);
        IsReversible = preview.Reversible;
        ReversibleText = localizer.Reversibility(preview.Reversible);
        ReversibleGlyph = Labels.ReversibilityGlyph(preview.Reversible);
        RequiresElevation = preview.RequiresElevation;
        ElevationText = preview.RequiresElevation ? localizer.Get("Optimization_Preview_RequiresElevation") : string.Empty;
        RequiresRestart = preview.RequiresRestart;
        RestartText = preview.RequiresRestart ? localizer.Get("Optimization_Preview_RequiresRestart") : string.Empty;
        Changes = new ObservableCollection<PlannedChangeItemViewModel>(
            preview.Changes.Select(c => new PlannedChangeItemViewModel(c, this, localizer, formatter, OnChildSelectionChanged)));
        RefreshSelection();
    }

    public OptimizationPreview Model { get; }

    public string OptimizationId { get; }

    public string Name { get; }

    public string Description { get; }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    public bool IsApplicable { get; }

    public string NotApplicableReason { get; }

    public string RiskText { get; }

    public string RiskGlyph { get; }

    public bool IsHighRisk => Model.Risk == RiskLevel.High;

    public string ImpactText { get; }

    public bool IsReversible { get; }

    /// <summary>« Réversible » / « Irréversible ».</summary>
    public string ReversibleText { get; }

    public string ReversibleGlyph { get; }

    public bool RequiresElevation { get; }

    /// <summary>« Autorisation administrateur requise ».</summary>
    public string ElevationText { get; }

    public bool RequiresRestart { get; }

    public string RestartText { get; }

    public ObservableCollection<PlannedChangeItemViewModel> Changes { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Case à trois états : true = toutes, false = aucune, null = sélection partielle. Modifiable (TwoWay).</summary>
    [ObservableProperty]
    public partial bool? SelectionState { get; set; }

    [ObservableProperty]
    public partial int SelectedCount { get; private set; }

    /// <summary>« 3 modifications sur 4 ».</summary>
    [ObservableProperty]
    public partial string SelectionSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string EstimatedBytesText { get; private set; } = string.Empty;

    public bool HasEstimatedBytes => !string.IsNullOrEmpty(EstimatedBytesText);

    public string AccessibleName => $"{Name}, {RiskText}, {ImpactText}, {ReversibleText}";

    public IEnumerable<PlannedChangeItemViewModel> SelectedChanges => Changes.Where(c => c.IsSelected);

    partial void OnSelectionStateChanged(bool? value)
    {
        if (_bulkUpdate || value is null) return;
        _bulkUpdate = true;
        try
        {
            foreach (var change in Changes) change.IsSelected = value.Value;
        }
        finally
        {
            _bulkUpdate = false;
        }

        RefreshSelection();
        _selectionChanged?.Invoke();
    }

    internal void SetAll(bool selected)
    {
        _bulkUpdate = true;
        try
        {
            foreach (var change in Changes) change.IsSelected = selected;
        }
        finally
        {
            _bulkUpdate = false;
        }

        RefreshSelection();
    }

    internal void ResetToDefault()
    {
        _bulkUpdate = true;
        try
        {
            foreach (var change in Changes) change.IsSelected = change.Model.SelectedByDefault;
        }
        finally
        {
            _bulkUpdate = false;
        }

        RefreshSelection();
    }

    private void OnChildSelectionChanged()
    {
        if (_bulkUpdate) return;
        RefreshSelection();
        _selectionChanged?.Invoke();
    }

    private void RefreshSelection()
    {
        var selected = Changes.Count(c => c.IsSelected);
        SelectedCount = selected;
        _bulkUpdate = true;
        try
        {
            SelectionState = selected == 0 ? false : selected == Changes.Count ? true : null;
        }
        finally
        {
            _bulkUpdate = false;
        }

        SelectionSummary = _localizer.Format("Optimization_Preview_SelectionSummary", selected, Changes.Count);
        var bytes = Changes.Where(c => c.IsSelected).Sum(c => c.Model.EstimatedBytes ?? 0);
        EstimatedBytesText = bytes > 0 ? _formatter.Bytes(bytes) : string.Empty;
        OnPropertyChanged(nameof(HasEstimatedBytes));
    }
}

/// <summary>
/// Aperçu (dry-run) d'un plan : optimisations applicables avec cases à cocher, non applicables avec raison,
/// totaux de la sélection, détection des actions irréversibles / à risque élevé (confirmation obligatoire).
/// </summary>
public sealed partial class PlanPreviewViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly IValueFormatter _formatter;

    public PlanPreviewViewModel(OptimizationPlan plan, ILocalizer localizer, IValueFormatter formatter, Func<string, TextRef?>? descriptionLookup = null)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));

        string Describe(OptimizationPreview p) => descriptionLookup?.Invoke(p.OptimizationId) is { } d ? localizer.Format(d) : string.Empty;

        Items = new ObservableCollection<OptimizationPreviewItemViewModel>(plan.Previews
            .Where(p => p.Applicable && p.Changes.Count > 0)
            .Select(p => new OptimizationPreviewItemViewModel(p, Describe(p), localizer, formatter, Refresh)));
        NotApplicableItems = new ObservableCollection<OptimizationPreviewItemViewModel>(plan.Previews
            .Where(p => !p.Applicable || p.Changes.Count == 0)
            .Select(p => new OptimizationPreviewItemViewModel(p, Describe(p), localizer, formatter, Refresh)));

        // La sélection initiale respecte celle du plan si elle est renseignée, sinon la sélection par défaut.
        if (plan.SelectedChangeIds.Count > 0)
        {
            foreach (var change in Items.SelectMany(i => i.Changes))
                change.IsSelected = plan.SelectedChangeIds.Contains(change.Id);
        }

        Refresh();
    }

    public OptimizationPlan Plan { get; }

    /// <summary>Optimisations applicables (groupes avec cases à cocher).</summary>
    public ObservableCollection<OptimizationPreviewItemViewModel> Items { get; }

    /// <summary>Optimisations non applicables sur ce PC, avec la raison.</summary>
    public ObservableCollection<OptimizationPreviewItemViewModel> NotApplicableItems { get; }

    public bool HasNotApplicable => NotApplicableItems.Count > 0;

    /// <summary>Aucune modification applicable : « Aucune modification n'est nécessaire pour le moment. »</summary>
    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial int SelectedChangeCount { get; private set; }

    public int TotalChangeCount => Items.Sum(i => i.Changes.Count);

    public bool HasSelection => SelectedChangeCount > 0;

    /// <summary>« 5 modifications sélectionnées sur 7 ».</summary>
    [ObservableProperty]
    public partial string SelectedSummaryText { get; private set; } = string.Empty;

    /// <summary>« Espace estimé récupérable : 1,2 Go » ; vide si aucune estimation.</summary>
    [ObservableProperty]
    public partial string EstimatedTotalText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasEstimatedTotal { get; private set; }

    [ObservableProperty]
    public partial bool HasIrreversibleSelected { get; private set; }

    [ObservableProperty]
    public partial bool HasHighRiskSelected { get; private set; }

    [ObservableProperty]
    public partial bool RequiresElevation { get; private set; }

    [ObservableProperty]
    public partial bool RequiresRestart { get; private set; }

    /// <summary>Avertissement affiché au-dessus du bouton Appliquer (irréversible, élévation, redémarrage).</summary>
    [ObservableProperty]
    public partial string SelectionNoticeText { get; private set; } = string.Empty;

    public event EventHandler? SelectionChanged;

    public IEnumerable<PlannedChangeItemViewModel> SelectedChanges => Items.SelectMany(i => i.SelectedChanges);

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items) item.SetAll(true);
        Refresh();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in Items) item.SetAll(false);
        Refresh();
    }

    /// <summary>Revient à la sélection recommandée par défaut.</summary>
    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var item in Items) item.ResetToDefault();
        Refresh();
    }

    /// <summary>Plan à exécuter avec la sélection de l'utilisateur et les confirmations obtenues.</summary>
    public OptimizationPlan BuildPlan(bool irreversibleConfirmed, bool highRiskConfirmed)
        => Plan with
        {
            SelectedChangeIds = SelectedChanges.Select(c => c.Id).ToHashSet(StringComparer.Ordinal),
            IrreversibleActionsConfirmed = irreversibleConfirmed,
            HighRiskActionsConfirmed = highRiskConfirmed,
        };

    /// <summary>
    /// Demande de confirmation si la sélection contient des actions irréversibles ou à risque élevé (obligatoire),
    /// ou si <paramref name="alwaysConfirm"/> (réglage « confirmer les opérations sensibles », niveau Avancé…). Null sinon.
    /// </summary>
    public ConfirmationRequest? BuildConfirmation(bool alwaysConfirm, string titleKey = "Optimization_Confirm_Title")
    {
        if (!HasIrreversibleSelected && !HasHighRiskSelected && !alwaysConfirm) return null;

        var details = new List<TextRef>();
        foreach (var change in SelectedChanges.Where(c => !c.IsReversible))
            details.Add(TextRef.Of("Optimization_Confirm_IrreversibleItem", change.Description));
        foreach (var change in SelectedChanges.Where(c => c.IsHighRisk && c.IsReversible))
            details.Add(TextRef.Of("Optimization_Confirm_HighRiskItem", change.Description));

        var message = HasIrreversibleSelected
            ? TextRef.Of("Optimization_Confirm_IrreversibleMessage", SelectedChangeCount)
            : HasHighRiskSelected
                ? TextRef.Of("Optimization_Confirm_HighRiskMessage", SelectedChangeCount)
                : TextRef.Of("Optimization_Confirm_Message", SelectedChangeCount);

        return new ConfirmationRequest(
            TextRef.Of(titleKey),
            message,
            TextRef.Of("Common_Action_Apply"),
            CloseButton: TextRef.Of("Common_Action_Cancel"),
            IsDestructive: HasIrreversibleSelected || HasHighRiskSelected,
            Details: details.Count > 0 ? details : null);
    }

    private void Refresh()
    {
        var selected = SelectedChanges.ToList();
        SelectedChangeCount = selected.Count;
        SelectedSummaryText = _localizer.Format("Optimization_Preview_SelectedTotal", selected.Count, TotalChangeCount);
        var bytes = selected.Sum(c => c.Model.EstimatedBytes ?? 0);
        HasEstimatedTotal = bytes > 0;
        EstimatedTotalText = bytes > 0 ? _localizer.Format("Optimization_Preview_EstimatedTotal", _formatter.Bytes(bytes)) : string.Empty;
        HasIrreversibleSelected = selected.Any(c => !c.IsReversible);
        HasHighRiskSelected = selected.Any(c => c.IsHighRisk) || Items.Any(i => i.IsHighRisk && i.SelectedCount > 0);
        RequiresElevation = Items.Any(i => i.RequiresElevation && i.SelectedCount > 0);
        RequiresRestart = Items.Any(i => i.RequiresRestart && i.SelectedCount > 0);

        var notices = new List<string>();
        if (HasIrreversibleSelected) notices.Add(_localizer.Get("Optimization_Preview_NoticeIrreversible"));
        if (RequiresElevation) notices.Add(_localizer.Get("Optimization_Preview_NoticeElevation"));
        if (RequiresRestart) notices.Add(_localizer.Get("Optimization_Preview_NoticeRestart"));
        SelectionNoticeText = string.Join(" ", notices);

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
