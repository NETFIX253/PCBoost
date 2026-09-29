using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Controls;

/// <summary>« Voici les modifications prévues » : aperçu sélectionnable, totaux, avertissements (§39).</summary>
public sealed partial class PlanPreviewView : UserControl
{
    public static readonly DependencyProperty PreviewProperty =
        DependencyProperty.Register(nameof(Preview), typeof(PlanPreviewViewModel), typeof(PlanPreviewView), new PropertyMetadata(null));

    public PlanPreviewView() => InitializeComponent();

    public PlanPreviewViewModel? Preview { get => (PlanPreviewViewModel?)GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }

    public Visibility EmptyVisibility(bool isEmpty) => Preview is null || isEmpty ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ItemsVisibility(bool isEmpty) => Preview is null || isEmpty ? Visibility.Collapsed : Visibility.Visible;
}
