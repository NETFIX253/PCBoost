using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Analysis;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>
/// Recommandation prête à afficher : impact, confiance, risque, réversibilité, nature (Recommandation / Possibilité),
/// action (navigation) et masquage (mémorisé dans les réglages).
/// </summary>
public sealed partial class RecommendationItemViewModel
{
    private readonly Action<RecommendationItemViewModel>? _open;
    private readonly Func<RecommendationItemViewModel, Task>? _dismiss;

    public RecommendationItemViewModel(
        Recommendation recommendation,
        ILocalizer localizer,
        Action<RecommendationItemViewModel>? open,
        Func<RecommendationItemViewModel, Task>? dismiss)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(localizer);
        Model = recommendation;
        _open = open;
        _dismiss = dismiss;
        Id = recommendation.Id;
        Title = localizer.Format(recommendation.Title);
        Description = localizer.Format(recommendation.Description);
        Reason = localizer.Format(recommendation.Reason);
        ImpactText = localizer.Impact(recommendation.Impact);
        ConfidenceText = localizer.Confidence(recommendation.Confidence);
        RiskText = localizer.Risk(recommendation.Risk);
        RiskGlyph = Labels.RiskGlyph(recommendation.Risk);
        IsReversible = recommendation.Reversible;
        ReversibleText = localizer.Reversibility(recommendation.Reversible);
        IsPossibility = recommendation.Kind == RecommendationKind.Possibility;
        KindText = localizer.Get(IsPossibility ? "Analysis_Recommendation_KindPossibility" : "Analysis_Recommendation_KindRecommendation");
        KindGlyph = IsPossibility ? Glyphs.Info : Glyphs.Recommendation;
        ActionLabel = recommendation.Action is { } a ? localizer.Format(a.Label) : string.Empty;
        DismissLabel = localizer.Get("Analysis_Recommendation_Dismiss");
        AccessibleName = $"{KindText} : {Title}. {ImpactText}, {RiskText}, {ReversibleText}.";
    }

    public Recommendation Model { get; }

    public string Id { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>Pourquoi cette recommandation (constat mesuré).</summary>
    public string Reason { get; }

    public bool HasReason => !string.IsNullOrEmpty(Reason);

    public string ImpactText { get; }

    public string ConfidenceText { get; }

    public string RiskText { get; }

    public string RiskGlyph { get; }

    public bool IsHighRisk => Model.Risk == RiskLevel.High;

    public bool IsReversible { get; }

    public string ReversibleText { get; }

    /// <summary>« Recommandation » ou « Possibilité ».</summary>
    public string KindText { get; }

    public string KindGlyph { get; }

    public bool IsPossibility { get; }

    public bool HasAction => Model.Action is not null && _open is not null;

    public string ActionLabel { get; }

    public bool CanDismiss => _dismiss is not null;

    public string DismissLabel { get; }

    public string AccessibleName { get; }

    [RelayCommand(CanExecute = nameof(HasAction))]
    private void Open() => _open?.Invoke(this);

    [RelayCommand(CanExecute = nameof(CanDismiss))]
    private Task DismissAsync() => _dismiss?.Invoke(this) ?? Task.CompletedTask;
}
