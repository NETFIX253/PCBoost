using System.Globalization;

namespace PCBoost.Infrastructure.Localization;

/// <summary>Langues de l'interface : "system" (langue de Windows), "fr", "en".</summary>
public static class SupportedLanguages
{
    public const string System = "system";
    public const string French = "fr";
    public const string English = "en";

    public static IReadOnlyList<string> Codes { get; } = [System, French, English];

    /// <summary>Normalise un code : "fr-CA" → "fr", "EN" → "en" ; vide ou inconnu → "system".</summary>
    public static string Normalize(string? code)
    {
        var value = (code ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0) return System;
        if (value == System) return System;
        var language = value.Split('-', '_')[0];
        return language switch
        {
            French => French,
            English => English,
            _ => System,
        };
    }

    /// <summary>Langue effective : "system" devient "fr" si l'interface Windows est en français, sinon "en".</summary>
    public static string Resolve(string? code, CultureInfo systemUiCulture)
    {
        ArgumentNullException.ThrowIfNull(systemUiCulture);
        var normalized = Normalize(code);
        if (normalized != System) return normalized;
        return string.Equals(systemUiCulture.TwoLetterISOLanguageName, French, StringComparison.OrdinalIgnoreCase) ? French : English;
    }
}
