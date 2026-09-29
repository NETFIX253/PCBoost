using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCBoost.Presentation.Common;
using Windows.Foundation;

namespace PCBoost.App.Controls;

/// <summary>
/// Disposition d'ItemsRepeater pour des cartes de hauteur variable. Les colonnes sont calculées comme par
/// UniformGridLayout (largeur minimale, espacements, nombre maximal de colonnes), mais chaque rangée prend la hauteur
/// de sa carte la plus haute et ses cartes sont étirées à cette hauteur (boutons alignés en bas de carte).
/// UniformGridLayout donne à tous les éléments la taille du premier : les cartes plus hautes que la première étaient
/// coupées. Non virtualisée : réservée aux listes courtes (niveaux, catégories de constats, indicateurs).
/// </summary>
public sealed partial class CardGridLayout : NonVirtualizingLayout
{
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(CardGridLayout), new PropertyMetadata(300d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty =
        DependencyProperty.Register(nameof(ColumnSpacing), typeof(double), typeof(CardGridLayout), new PropertyMetadata(12d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty =
        DependencyProperty.Register(nameof(RowSpacing), typeof(double), typeof(CardGridLayout), new PropertyMetadata(12d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MaximumColumnsProperty =
        DependencyProperty.Register(nameof(MaximumColumns), typeof(int), typeof(CardGridLayout), new PropertyMetadata(0, OnLayoutPropertyChanged));

    /// <summary>Largeur minimale d'une carte (DIP).</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>Nombre maximal de colonnes ; 0 : sans limite.</summary>
    public int MaximumColumns
    {
        get => (int)GetValue(MaximumColumnsProperty);
        set => SetValue(MaximumColumnsProperty, value);
    }

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var children = context.Children;
        var columns = CardGridMath.ColumnCount(availableSize.Width, MinItemWidth, ColumnSpacing, MaximumColumns);
        var itemWidth = CardGridMath.ItemWidth(availableSize.Width, columns, ColumnSpacing, MinItemWidth);
        var heights = new double[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            heights[i] = children[i].DesiredSize.Height;
        }

        var grid = CardGridMath.Arrange(heights, columns, itemWidth, ColumnSpacing, RowSpacing);
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : Math.Min(columns, Math.Max(1, children.Count)) * (itemWidth + ColumnSpacing) - ColumnSpacing;
        return new Size(Math.Max(0, width), grid.Height);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var children = context.Children;
        var columns = CardGridMath.ColumnCount(finalSize.Width, MinItemWidth, ColumnSpacing, MaximumColumns);
        var itemWidth = CardGridMath.ItemWidth(finalSize.Width, columns, ColumnSpacing, MinItemWidth);
        var heights = new double[children.Count];
        for (var i = 0; i < children.Count; i++) heights[i] = children[i].DesiredSize.Height;

        var grid = CardGridMath.Arrange(heights, columns, itemWidth, ColumnSpacing, RowSpacing);
        for (var i = 0; i < children.Count; i++)
        {
            var r = grid.Items[i];
            children[i].Arrange(new Rect(r.X, r.Y, r.Width, r.Height));
        }

        return new Size(finalSize.Width, Math.Max(finalSize.Height, grid.Height));
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CardGridLayout)d).InvalidateMeasure();
}
