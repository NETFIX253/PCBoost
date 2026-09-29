using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Services;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Facteur de lenteur observé (§74) : constat mesuré, « Pourquoi ? », « Que peut-on faire ? », impact estimé, action.</summary>
public sealed partial class SlownessFactorItemViewModel : ObservableObject
{
    private readonly Action<SlownessFactorItemViewModel>? _open;

    public SlownessFactorItemViewModel(SlownessFactor factor, ILocalizer localizer, Action<SlownessFactorItemViewModel>? open)
    {
        ArgumentNullException.ThrowIfNull(factor);
        ArgumentNullException.ThrowIfNull(localizer);
        Model = factor;
        _open = open;
        Id = factor.Id;
        Title = localizer.Format(factor.Title);
        Evidence = localizer.Format(factor.Evidence);
        Why = localizer.Format(factor.Why);
        WhatToDo = localizer.Format(factor.WhatToDo);
        ImpactText = localizer.Impact(factor.Impact);
        ConfidenceText = localizer.Confidence(factor.Confidence);
        IconGlyph = factor.Impact switch
        {
            Core.Common.ImpactLevel.High => Glyphs.Error,
            Core.Common.ImpactLevel.Medium => Glyphs.Warning,
            _ => Glyphs.Info,
        };
        ActionLabel = factor.Action is { } a ? localizer.Format(a.Label) : string.Empty;
        AccessibleName = $"{Title}. {ImpactText}. {Evidence}";
    }

    public SlownessFactor Model { get; }

    public string Id { get; }

    public string Title { get; }

    /// <summary>Mesure observée (« 87 % de la mémoire utilisée »).</summary>
    public string Evidence { get; }

    /// <summary>Réponse à « Pourquoi ? ».</summary>
    public string Why { get; }

    /// <summary>Réponse à « Que peut-on faire ? ».</summary>
    public string WhatToDo { get; }

    /// <summary>« Impact estimé : élevé ».</summary>
    public string ImpactText { get; }

    public string ConfidenceText { get; }

    public string IconGlyph { get; }

    public bool HasAction => Model.Action is not null && _open is not null;

    public string ActionLabel { get; }

    public string AccessibleName { get; }

    /// <summary>Section « Pourquoi ? » dépliée.</summary>
    [ObservableProperty]
    public partial bool IsWhyExpanded { get; set; }

    /// <summary>Section « Que peut-on faire ? » dépliée.</summary>
    [ObservableProperty]
    public partial bool IsWhatToDoExpanded { get; set; }

    [RelayCommand]
    private void ToggleWhy() => IsWhyExpanded = !IsWhyExpanded;

    [RelayCommand]
    private void ToggleWhatToDo() => IsWhatToDoExpanded = !IsWhatToDoExpanded;

    [RelayCommand(CanExecute = nameof(HasAction))]
    private void Open() => _open?.Invoke(this);
}

/// <summary>« Pourquoi mon PC est lent ? » (§74) : « Les facteurs actuellement observés sont : ».</summary>
public sealed partial class DiagnosisViewModel : ViewModelBase
{
    private readonly ISlowPcDiagnosticService _diagnostic;

    public DiagnosisViewModel(ViewModelContext context, ISlowPcDiagnosticService diagnostic)
        : base(context)
    {
        _diagnostic = diagnostic;
    }

    public ObservableCollection<SlownessFactorItemViewModel> Factors { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFactors), nameof(ShowNoFactor))]
    public partial bool HasResult { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFactors), nameof(ShowNoFactor))]
    public partial bool NoSignificantFactor { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiagnoseCommand))]
    public partial bool IsDiagnosing { get; private set; }

    public bool ShowFactors => HasResult && Factors.Count > 0;

    /// <summary>Afficher « Aucun facteur de ralentissement important n'a été observé pour le moment. »</summary>
    public bool ShowNoFactor => HasResult && Factors.Count == 0;

    /// <summary>« Diagnostic effectué il y a 1 min ».</summary>
    [ObservableProperty]
    public partial string DiagnosisDateText { get; private set; } = string.Empty;

    protected override Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken) => DiagnoseAsync(cancellationToken);

    protected override void OnDeactivated() => DiagnoseCancelCommand.Execute(null);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanDiagnose))]
    private async Task DiagnoseAsync(CancellationToken cancellationToken)
    {
        IsDiagnosing = true;
        ErrorText = null;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var diagnosis = await _diagnostic.DiagnoseAsync(ct).ConfigureAwait(true);
                CollectionSync.Replace(Factors, diagnosis.Factors
                    .OrderByDescending(f => f.Impact)
                    .ThenByDescending(f => f.Confidence)
                    .Select(f => new SlownessFactorItemViewModel(f, Localizer, OpenFactor)));
                NoSignificantFactor = diagnosis.NoSignificantFactor || Factors.Count == 0;
                DiagnosisDateText = T("Diagnosis_Date", Formatter.DateTime(diagnosis.Timestamp));
                HasResult = true;
                OnPropertyChanged(nameof(ShowFactors));
                OnPropertyChanged(nameof(ShowNoFactor));
            }, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsDiagnosing = false;
        }
    }

    private bool CanDiagnose() => !IsDiagnosing;

    private void OpenFactor(SlownessFactorItemViewModel item)
    {
        if (item.Model.Action is { } action) Navigation.Navigate(action.NavigationTarget, action.OptimizationId);
    }
}
