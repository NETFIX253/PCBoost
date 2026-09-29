using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.Tests;

/// <summary>
/// Grille de cartes de hauteur variable (niveaux de l'assistant « PC ancien », constats de l'analyse…).
/// Régression : UniformGridLayout donnait à toutes les cartes la hauteur de la première ; les niveaux Standard et Avancé,
/// plus longs qu'Essentiel, étaient coupés, bouton « Choisir ce niveau » compris.
/// </summary>
public sealed class CardGridMathTests
{
    [Fact]
    public void Arrange_NeverGivesACardLessThanItsDesiredHeight()
    {
        // Essentiel (2 optimisations), Standard (4), Avancé (5 + avertissement), sur une rangée de trois colonnes.
        double[] desired = [210, 262, 318];

        var grid = CardGridMath.Arrange(desired, columns: 3, itemWidth: 320, columnSpacing: 12, rowSpacing: 12);

        for (var i = 0; i < desired.Length; i++)
            Assert.True(grid.Items[i].Height >= desired[i], $"Carte {i} : {grid.Items[i].Height} < {desired[i]}");
    }

    [Fact]
    public void Arrange_StretchesEachRowToItsTallestCard()
    {
        double[] desired = [100, 180, 140, 90, 60];

        var grid = CardGridMath.Arrange(desired, columns: 3, itemWidth: 300, columnSpacing: 12, rowSpacing: 16);

        Assert.All(grid.Items.Take(3), r => Assert.Equal(180, r.Height));
        Assert.All(grid.Items.Skip(3), r => Assert.Equal(90, r.Height));
        Assert.Equal(180 + 16 + 90, grid.Height);
        Assert.Equal(0, grid.Items[3].X);
        Assert.Equal(180 + 16, grid.Items[3].Y);
        Assert.Equal(312, grid.Items[1].X);
        Assert.Equal(624, grid.Items[2].X);
    }

    [Fact]
    public void Arrange_Empty_HasNoHeight()
    {
        var grid = CardGridMath.Arrange([], columns: 2, itemWidth: 300, columnSpacing: 12, rowSpacing: 12);

        Assert.Empty(grid.Items);
        Assert.Equal(0, grid.Height);
    }

    [Theory]
    [InlineData(1000, 300, 12, 0, 3)]   // 3 × 300 + 2 × 12 = 924 ≤ 1000
    [InlineData(923, 300, 12, 0, 2)]
    [InlineData(250, 300, 12, 0, 1)]    // plus étroit que la largeur minimale : une colonne
    [InlineData(1400, 300, 12, 2, 2)]   // nombre maximal de colonnes respecté
    [InlineData(double.PositiveInfinity, 300, 12, 0, 1)]
    public void ColumnCount_MatchesAvailableWidth(double width, double minItemWidth, double spacing, int maximum, int expected)
        => Assert.Equal(expected, CardGridMath.ColumnCount(width, minItemWidth, spacing, maximum));

    [Fact]
    public void ItemWidth_FillsTheRow()
    {
        Assert.Equal(325.33, CardGridMath.ItemWidth(1000, 3, 12, 300), 2);
        Assert.Equal(300, CardGridMath.ItemWidth(double.PositiveInfinity, 1, 12, 300));
    }
}
