using System.Globalization;
using System.Text.RegularExpressions;

namespace PCBoost.Core.Drivers;

/// <summary>
/// Formats acceptés pour les identifiants échangés avec l'assistant administrateur (validation stricte, appliquée avant
/// l'invite UAC puis par PCBoost.Elevator) et lecture des versions de pilotes.
/// </summary>
public static partial class DriverIdentifiers
{
    /// <summary>Longueur maximale d'un identifiant d'instance ou matériel (MAX_DEVICE_ID_LEN).</summary>
    public const int MaxDeviceIdLength = 200;

    /// <summary>Identifiant de mise à jour Windows Update : GUID au format « D ».</summary>
    public static bool IsValidUpdateId(string? value)
        => value is { Length: 36 } && Guid.TryParseExact(value, "D", out _);

    /// <summary>
    /// Identifiant d'instance de périphérique (ex. « PCI\VEN_8086&amp;DEV_5917&amp;…\3&amp;11583659&amp;0&amp;10 ») :
    /// ASCII imprimable, au moins un « \ », sans guillemet ni caractère de redirection. Il ne désigne jamais un fichier.
    /// </summary>
    public static bool IsValidInstanceId(string? value)
        => IsPrintableDeviceId(value) && value!.Contains('\\', StringComparison.Ordinal) && !value.StartsWith('\\') && !value.EndsWith('\\');

    /// <summary>Identifiant matériel (ex. « PCI\VEN_8086&amp;DEV_5917 », « USB\VID_8087&amp;PID_0A2B »).</summary>
    public static bool IsValidHardwareId(string? value) => IsPrintableDeviceId(value);

    /// <summary>Nom de fichier INF du magasin de pilotes (ex. « oem42.inf », « netrtwlane.inf ») : jamais un chemin.</summary>
    public static bool IsValidInfName(string? value) => value is not null && InfNameRegex().IsMatch(value);

    /// <summary>Version de pilote « a.b.c.d » (1 à 4 nombres de 0 à 65535).</summary>
    public static bool IsValidVersion(string? value) => TryParseVersion(value, out _);

    public static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 23 || !VersionRegex().IsMatch(value)) return false;
        var parts = value.Split('.');
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > 65535) return false;
            numbers[i] = n;
        }
        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    /// <summary>
    /// Version annoncée à la fin du titre Windows Update (« Intel - Display - 31.0.101.4502 », « Realtek Semiconductor Corp.
    /// - Extension - 10.0.22621.31248 ») ; null si le titre ne se termine pas par une version.
    /// </summary>
    public static string? VersionFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var match = TitleVersionRegex().Match(title.Trim());
        return match.Success && IsValidVersion(match.Groups[1].Value) ? match.Groups[1].Value : null;
    }

    private static bool IsPrintableDeviceId(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= MaxDeviceIdLength
           && value.Trim().Length == value.Length
           && value.All(c => c is >= ' ' and <= '~' && c is not ('"' or '<' or '>' or '|' or '*' or '?' or '/'));

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}\.inf$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InfNameRegex();

    [GeneratedRegex(@"^\d{1,5}(\.\d{1,5}){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"(?:^|[\s\-])(\d{1,5}(?:\.\d{1,5}){1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex TitleVersionRegex();
}
