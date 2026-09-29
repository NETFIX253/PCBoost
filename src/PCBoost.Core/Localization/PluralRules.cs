using System.Globalization;
using System.Text.RegularExpressions;

namespace PCBoost.Core.Localization;

/// <summary>
/// Accords au pluriel dans les textes localisés : « {0:plural:modification|modifications} » est remplacé, avant la mise en
/// forme, par la forme qui convient au nombre en position 0 (français : singulier pour 0 et 1 ; anglais : singulier pour 1).
/// </summary>
public static partial class PluralRules
{
    public const string Marker = ":plural:";

    public static string Resolve(string template, IReadOnlyList<object?> args, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(culture);
        if (!template.Contains(Marker, StringComparison.Ordinal)) return template;
        return Pattern().Replace(template, match =>
        {
            var index = int.Parse(match.Groups["i"].Value, CultureInfo.InvariantCulture);
            if (index >= args.Count || !TryGetNumber(args[index], out var number)) return match.Groups["other"].Value;
            return IsSingular(number, culture) ? match.Groups["one"].Value : match.Groups["other"].Value;
        });
    }

    public static bool IsSingular(decimal number, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var magnitude = Math.Abs(number);
        return string.Equals(culture.TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase) ? magnitude < 2 : magnitude == 1;
    }

    private static bool TryGetNumber(object? value, out decimal number)
    {
        switch (value)
        {
            case int i: number = i; return true;
            case long l: number = l; return true;
            case short sh: number = sh; return true;
            case byte b: number = b; return true;
            case uint ui: number = ui; return true;
            case ulong ul: number = ul; return true;
            case decimal d: number = d; return true;
            case double db when double.IsFinite(db) && Math.Abs(db) < 1e15: number = (decimal)db; return true;
            case float f when float.IsFinite(f) && Math.Abs(f) < 1e15f: number = (decimal)f; return true;
            default: number = 0; return false;
        }
    }

    [GeneratedRegex(@"\{(?<i>\d+):plural:(?<one>[^|{}]*)\|(?<other>[^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
