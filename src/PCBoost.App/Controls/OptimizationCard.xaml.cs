using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Controls;

/// <summary>Aperçu d'une optimisation : sélection, risque, réversibilité, élévation, détail des changements prévus.</summary>
public sealed partial class OptimizationCard : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(OptimizationPreviewItemViewModel), typeof(OptimizationCard), new PropertyMetadata(null));

    public OptimizationCard() => InitializeComponent();

    public OptimizationPreviewItemViewModel? Item { get => (OptimizationPreviewItemViewModel?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }

    public BadgeKind RiskKind(bool isHighRisk) => isHighRisk ? BadgeKind.Caution : BadgeKind.Neutral;

    public BadgeKind ReversibleKind(bool reversible) => reversible ? BadgeKind.Success : BadgeKind.Caution;
}
