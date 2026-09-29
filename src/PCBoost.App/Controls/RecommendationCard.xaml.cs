using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Controls;

/// <summary>Recommandation : titre, raison mesurée, impact, risque, réversibilité, action.</summary>
public sealed partial class RecommendationCard : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(RecommendationItemViewModel), typeof(RecommendationCard), new PropertyMetadata(null));

    public static readonly DependencyProperty AllowDismissProperty =
        DependencyProperty.Register(nameof(AllowDismiss), typeof(bool), typeof(RecommendationCard), new PropertyMetadata(true));

    public RecommendationCard() => InitializeComponent();

    public RecommendationItemViewModel? Item { get => (RecommendationItemViewModel?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }

    public bool AllowDismiss { get => (bool)GetValue(AllowDismissProperty); set => SetValue(AllowDismissProperty, value); }

    public BadgeKind RiskKind(bool isHighRisk) => isHighRisk ? BadgeKind.Caution : BadgeKind.Neutral;

    public BadgeKind ReversibleKind(bool reversible) => reversible ? BadgeKind.Success : BadgeKind.Neutral;

    public Visibility DismissVisibility(bool canDismiss, bool allow) => canDismiss && allow ? Visibility.Visible : Visibility.Collapsed;
}
