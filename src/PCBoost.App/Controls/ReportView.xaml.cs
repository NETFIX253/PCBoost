using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Controls;

/// <summary>Rapport final d'optimisation (§71) : actions, espace récupéré, redémarrage, avertissements.</summary>
public sealed partial class ReportView : UserControl
{
    public static readonly DependencyProperty ReportProperty =
        DependencyProperty.Register(nameof(Report), typeof(OptimizationReportViewModel), typeof(ReportView), new PropertyMetadata(null));

    public ReportView() => InitializeComponent();

    public OptimizationReportViewModel? Report { get => (OptimizationReportViewModel?)GetValue(ReportProperty); set => SetValue(ReportProperty, value); }
}
