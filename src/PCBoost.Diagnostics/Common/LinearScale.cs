namespace PCBoost.Diagnostics;

/// <summary>Interpolation linéaire bornée utilisée par le score.</summary>
internal static class LinearScale
{
    /// <summary>
    /// Fraction dans [0, 1] : 1 quand la valeur atteint la borne « bonne », 0 à la borne « mauvaise », linéaire entre les deux.
    /// Fonctionne dans les deux sens (plus bas = mieux, ou plus haut = mieux).
    /// </summary>
    public static double Fraction(double value, double good, double bad)
    {
        if (good.Equals(bad)) return good < bad ? (value <= good ? 1 : 0) : (value >= good ? 1 : 0);
        return Math.Clamp((bad - value) / (bad - good), 0, 1);
    }

    /// <summary>Points arrondis à l'entier le plus proche (0,5 → supérieur).</summary>
    public static int Points(int maxPoints, double fraction)
        => (int)Math.Round(maxPoints * Math.Clamp(fraction, 0, 1), MidpointRounding.AwayFromZero);
}
