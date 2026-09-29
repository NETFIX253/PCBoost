using System.Globalization;
using PCBoost.Core.Common;

namespace PCBoost.Core.Localization;

/// <summary>
/// Résolution des chaînes UI depuis les fichiers de ressources (fr par défaut, en).
/// Aucune chaîne visible n'est codée en dur dans l'UI (§43).
/// </summary>
public interface ILocalizer
{
    CultureInfo Culture { get; }

    IReadOnlyList<LanguageOption> AvailableLanguages { get; }

    /// <summary>Retourne la chaîne pour la clé, ou la clé entre crochets si absente.</summary>
    string Get(string key);

    string Format(string key, params object[] args);

    string Format(TextRef text);

    /// <summary>"system", "fr" ou "en".</summary>
    void SetLanguage(string languageCode);

    event EventHandler? LanguageChanged;
}

public sealed record LanguageOption(string Code, string NativeName);

/// <summary>
/// Source de chaînes fournie par un module (fichiers .resx embarqués). Le localiseur interroge toutes
/// les sources enregistrées : chaque module possède ses propres ressources (extensibilité §77).
/// </summary>
public interface IStringResourceSource
{
    string? GetString(string key, CultureInfo culture);
}
