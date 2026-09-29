using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;

namespace PCBoost.Infrastructure.Localization;

/// <summary>
/// Localiseur : interroge les sources de chaînes des modules dans l'ordre d'enregistrement (la première valeur trouvée l'emporte).
/// Ne lève jamais d'exception pour un texte manquant ou un format invalide (repli sur « [Cle] » ou le texte brut).
/// La culture de formatage est la variante régionale de l'utilisateur si elle correspond à la langue choisie
/// (ex. fr-CA, en-GB), sinon fr-FR / en-US.
/// </summary>
public sealed class Localizer : ILocalizer
{
    private const int MaxNestingDepth = 4;

    private readonly IStringResourceSource[] _sources;
    private readonly ILogger<Localizer> _logger;
    private readonly CultureInfo _systemUiCulture;
    private readonly CultureInfo _regionalCulture;
    private readonly ConcurrentDictionary<string, byte> _reportedMissing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reportedInvalidFormats = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private volatile string _languageCode = SupportedLanguages.System;
    private volatile CultureInfo _culture;

    /// <param name="sources">Sources de chaînes des modules, dans l'ordre d'enregistrement.</param>
    /// <param name="logger">Journal (textes manquants signalés une seule fois, en Debug).</param>
    /// <param name="systemUiCulture">Langue d'interface de Windows (par défaut : culture d'interface au démarrage).</param>
    /// <param name="regionalCulture">Format régional de l'utilisateur (par défaut : culture courante au démarrage).</param>
    public Localizer(
        IEnumerable<IStringResourceSource> sources,
        ILogger<Localizer> logger,
        CultureInfo? systemUiCulture = null,
        CultureInfo? regionalCulture = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToArray();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _systemUiCulture = systemUiCulture ?? CultureInfo.CurrentUICulture;
        _regionalCulture = regionalCulture ?? CultureInfo.CurrentCulture;
        _culture = ResolveCulture(SupportedLanguages.System);
    }

    public CultureInfo Culture => _culture;

    /// <summary>Code choisi ("system", "fr" ou "en").</summary>
    public string LanguageCode => _languageCode;

    public IReadOnlyList<LanguageOption> AvailableLanguages =>
    [
        new(SupportedLanguages.System, Get("Infra_Language_System")),
        new(SupportedLanguages.French, Get("Infra_Language_French")),
        new(SupportedLanguages.English, Get("Infra_Language_English")),
    ];

    public event EventHandler? LanguageChanged;

    public string Get(string key) => TryGet(key) ?? Missing(key);

    public string Format(string key, params object[] args)
    {
        var template = TryGet(key);
        if (template is null) return Missing(key);
        return FormatTemplate(template, NormalizeArguments(args, 0));
    }

    public string Format(TextRef text) => text is null ? string.Empty : FormatCore(text, 0);

    public void SetLanguage(string languageCode)
    {
        var normalized = SupportedLanguages.Normalize(languageCode);
        bool changed;
        lock (_gate)
        {
            var culture = ResolveCulture(normalized);
            changed = !string.Equals(normalized, _languageCode, StringComparison.Ordinal) || !culture.Equals(_culture);
            _languageCode = normalized;
            _culture = culture;
        }
        if (!changed) return;

        _logger.LogInformation("Langue de l'interface : {Language} ({Culture}).", normalized, _culture.Name);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private string? TryGet(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var culture = _culture;
        foreach (var source in _sources)
        {
            string? value;
            try
            {
                value = source.GetString(key, culture);
            }
            catch (Exception ex) when (ex is InvalidOperationException or MissingManifestResourceException or MissingSatelliteAssemblyException or BadImageFormatException)
            {
                _logger.LogDebug(ex, "Source de textes illisible pour la clé « {Key} ».", key);
                continue;
            }
            if (value is not null) return value;
        }
        return null;
    }

    private string Missing(string key)
    {
        var safeKey = key ?? string.Empty;
        if (_reportedMissing.TryAdd(safeKey, 0))
            _logger.LogDebug("Texte introuvable pour la clé « {Key} » (langue {Culture}).", safeKey, _culture.Name);
        return "[" + safeKey + "]";
    }

    private string FormatCore(TextRef text, int depth)
    {
        var args = text.Args ?? [];
        if (text.IsLiteral)
        {
            if (args.Length == 0) return string.Empty;
            var literal = NormalizeArgument(args[0], depth);
            return literal as string ?? Convert.ToString(literal, _culture) ?? string.Empty;
        }
        var template = TryGet(text.Key);
        if (template is null) return Missing(text.Key);
        return FormatTemplate(template, NormalizeArguments(args, depth));
    }

    private string FormatTemplate(string template, object?[] args)
    {
        if (args.Length == 0) return template;
        try
        {
            template = PluralRules.Resolve(template, args, _culture);
            return string.Format(_culture, template, args);
        }
        catch (FormatException ex)
        {
            if (_reportedInvalidFormats.TryAdd(template, 0))
                _logger.LogDebug(ex, "Texte mal formé ou arguments insuffisants : « {Template} » ({Count} argument(s)).", template, args.Length);
            return template;
        }
    }

    private object?[] NormalizeArguments(object?[]? args, int depth)
    {
        if (args is null || args.Length == 0) return [];
        var result = new object?[args.Length];
        for (var i = 0; i < args.Length; i++) result[i] = NormalizeArgument(args[i], depth);
        return result;
    }

    /// <summary>JsonElement (TextRef relu d'un stockage JSON) → nombre/chaîne/booléen ; TextRef imbriqué → texte localisé.</summary>
    private object? NormalizeArgument(object? arg, int depth) => arg switch
    {
        null => string.Empty,
        JsonElement element => FromJson(element),
        TextRef nested when depth < MaxNestingDepth => FormatCore(nested, depth + 1),
        TextRef nested => nested.Key,
        _ => arg,
    };

    private static object FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number when element.TryGetInt32(out var i) => i,
        JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => element.GetRawText(),
    };

    private CultureInfo ResolveCulture(string languageCode)
    {
        var language = SupportedLanguages.Resolve(languageCode, _systemUiCulture);
        foreach (var candidate in new[] { _regionalCulture, _systemUiCulture })
        {
            if (!candidate.IsNeutralCulture
                && !string.IsNullOrEmpty(candidate.Name)
                && string.Equals(candidate.TwoLetterISOLanguageName, language, StringComparison.OrdinalIgnoreCase))
            {
                return CultureInfo.ReadOnly(candidate);
            }
        }
        return CultureInfo.GetCultureInfo(language == SupportedLanguages.French ? "fr-FR" : "en-US");
    }
}
