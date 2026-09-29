using PCBoost.Core.Common;
using PCBoost.Core.Localization;

namespace PCBoost.Infrastructure.Localization;

/// <summary>
/// Formats communs (tailles, pourcentages, températures, durées, erreurs) cohérents dans toute l'interface.
/// Une valeur absente est affichée « Non disponible », jamais estimée.
/// </summary>
public static class LocalizerFormattingExtensions
{
    private static readonly string[] SizeUnitKeys = ["Common_Unit_Byte", "Common_Unit_KB", "Common_Unit_MB", "Common_Unit_GB", "Common_Unit_TB"];

    public static string NotAvailable(this ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Get("Common_NotAvailable");
    }

    /// <summary>Taille en base 1024 : « 512 o », « 1,5 Go », « 120 Go ».</summary>
    public static string FormatBytes(this ILocalizer localizer, long? bytes)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (bytes is not { } value || value < 0) return localizer.NotAvailable();

        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < SizeUnitKeys.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        var format = unit == 0 || size >= 100 ? "N0" : "N1";
        return localizer.Format("Common_SizeFormat", size.ToString(format, localizer.Culture), localizer.Get(SizeUnitKeys[unit]));
    }

    public static string FormatPercent(this ILocalizer localizer, double? percent, int decimals = 0)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (percent is not { } value || !double.IsFinite(value)) return localizer.NotAvailable();
        return localizer.Format("Common_Percent", value.ToString("N" + Math.Clamp(decimals, 0, 3), localizer.Culture));
    }

    public static string FormatTemperature(this ILocalizer localizer, double? celsius)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (celsius is not { } value || !double.IsFinite(value)) return localizer.NotAvailable();
        return localizer.Format("Common_Temperature", value.ToString("N0", localizer.Culture));
    }

    public static string FormatTemperature(this ILocalizer localizer, SensorReading reading)
        => localizer.FormatTemperature(reading.HasValue ? reading.Value : null);

    /// <summary>Durée lisible : « 45 s », « 3 min 05 s », « 2 h 07 min », « 3 j 4 h ».</summary>
    public static string FormatDuration(this ILocalizer localizer, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (duration < TimeSpan.Zero) duration = duration.Negate();
        if (duration.TotalMinutes < 1)
            return localizer.Format("Common_Duration_Seconds", (int)Math.Round(duration.TotalSeconds));
        if (duration.TotalHours < 1)
        {
            return duration.Seconds == 0
                ? localizer.Format("Common_Duration_Minutes", duration.Minutes)
                : localizer.Format("Common_Duration_MinutesSeconds", duration.Minutes, duration.Seconds);
        }
        if (duration.TotalDays < 1)
            return localizer.Format("Common_Duration_HoursMinutes", duration.Hours, duration.Minutes);
        return localizer.Format("Common_Duration_DaysHours", (int)duration.TotalDays, duration.Hours);
    }

    public static string FormatYesNo(this ILocalizer localizer, bool value)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Get(value ? "Common_Yes" : "Common_No");
    }

    /// <summary>Message humain d'une erreur (§79). Le détail technique reste réservé au mode Expert.</summary>
    public static string FormatError(this ILocalizer localizer, OperationErrorKind error)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Get("Error_" + error);
    }

    /// <summary>Message du résultat s'il en porte un, sinon message humain de son type d'erreur.</summary>
    public static string FormatResult(this ILocalizer localizer, OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Message is not null) return localizer.Format(result.Message);
        return localizer.FormatError(result.Error);
    }
}
