namespace PCBoost.Presentation.Common;

/// <summary>Position et taille d'une carte dans la grille.</summary>
public readonly record struct CardGridRect(double X, double Y, double Width, double Height);

/// <summary>Disposition calculée : une position par carte, hauteur totale.</summary>
public sealed record CardGridArrangement(IReadOnlyList<CardGridRect> Items, double Height);

/// <summary>
/// Calcul d'une grille de cartes de hauteur variable, indépendant de l'interface (testable) :
/// colonnes déterminées comme par UniformGridLayout (largeur minimale, espacement, nombre maximal),
/// mais chaque rangée prend la hauteur de sa carte la plus haute ; aucune carte n'est donc coupée.
/// </summary>
public static class CardGridMath
{
    /// <summary>Nombre de colonnes pour une largeur disponible. <paramref name="maximumColumns"/> ≤ 0 : sans limite.</summary>
    public static int ColumnCount(double availableWidth, double minItemWidth, double columnSpacing, int maximumColumns)
    {
        var columns = double.IsFinite(availableWidth) && availableWidth > 0 && minItemWidth > 0
            ? (int)Math.Floor((availableWidth + columnSpacing) / (minItemWidth + columnSpacing))
            : 1;
        columns = Math.Max(1, columns);
        return maximumColumns > 0 ? Math.Min(columns, maximumColumns) : columns;
    }

    /// <summary>Largeur d'une carte : les colonnes remplissent la rangée.</summary>
    public static double ItemWidth(double availableWidth, int columns, double columnSpacing, double minItemWidth)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return minItemWidth;
        columns = Math.Max(1, columns);
        return Math.Max(0, (availableWidth - (columns - 1) * columnSpacing) / columns);
    }

    /// <summary>Place les cartes ligne par ligne ; chaque carte reçoit la hauteur de la plus haute de sa rangée.</summary>
    public static CardGridArrangement Arrange(IReadOnlyList<double> desiredHeights, int columns, double itemWidth, double columnSpacing, double rowSpacing)
    {
        ArgumentNullException.ThrowIfNull(desiredHeights);
        columns = Math.Max(1, columns);
        var items = new CardGridRect[desiredHeights.Count];
        var y = 0.0;
        for (var rowStart = 0; rowStart < desiredHeights.Count; rowStart += columns)
        {
            var rowEnd = Math.Min(rowStart + columns, desiredHeights.Count);
            var rowHeight = 0.0;
            for (var i = rowStart; i < rowEnd; i++)
                rowHeight = Math.Max(rowHeight, Math.Max(0, desiredHeights[i]));

            for (var i = rowStart; i < rowEnd; i++)
                items[i] = new CardGridRect((i - rowStart) * (itemWidth + columnSpacing), y, itemWidth, rowHeight);

            y += rowHeight + (rowEnd < desiredHeights.Count ? rowSpacing : 0);
        }

        return new CardGridArrangement(items, y);
    }
}
