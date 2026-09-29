namespace PCBoost.Platform;

/// <summary>Conversions et bornes de plausibilité des températures (aucune valeur hors plage n'est affichée).</summary>
internal static class TemperatureMath
{
    public const double KelvinOffset = 273.15;
    public const double MaxPlausibleCelsius = 120;

    public static double KelvinToCelsius(double kelvin) => kelvin - KelvinOffset;

    /// <summary>MSAcpi_ThermalZoneTemperature.CurrentTemperature est exprimé en dixièmes de kelvin.</summary>
    public static double TenthsKelvinToCelsius(double tenthsOfKelvin) => (tenthsOfKelvin / 10d) - KelvinOffset;

    /// <summary>Une température ≤ 0 °C ou &gt; 120 °C est considérée comme une lecture invalide.</summary>
    public static bool IsPlausible(double celsius)
        => double.IsFinite(celsius) && celsius > 0 && celsius <= MaxPlausibleCelsius;

    /// <summary>Maximum des valeurs plausibles, ou null.</summary>
    public static double? MaxPlausible(IEnumerable<double> celsiusValues)
    {
        double? max = null;
        foreach (var value in celsiusValues)
        {
            if (!IsPlausible(value)) continue;
            if (max is null || value > max) max = value;
        }
        return max is null ? null : Math.Round(max.Value, 1);
    }
}
