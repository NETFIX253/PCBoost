using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCBoost.Presentation.Common;

namespace PCBoost.App.Controls;

/// <summary>Ligne « libellé : valeur » (fiche matériel, rapports, détails).</summary>
public sealed partial class InfoRowView : UserControl
{
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.Register(nameof(Row), typeof(InfoRowViewModel), typeof(InfoRowView), new PropertyMetadata(null));

    public static readonly DependencyProperty LabelWidthProperty =
        DependencyProperty.Register(nameof(LabelWidth), typeof(GridLength), typeof(InfoRowView), new PropertyMetadata(new GridLength(200)));

    public InfoRowView() => InitializeComponent();

    public InfoRowViewModel? Row { get => (InfoRowViewModel?)GetValue(RowProperty); set => SetValue(RowProperty, value); }

    public GridLength LabelWidth { get => (GridLength)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }
}
