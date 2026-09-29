using System.Globalization;
using System.Text.RegularExpressions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Programs;

/// <summary>Entrée brute d'une clé de désinstallation de Windows.</summary>
internal sealed record UninstallRegistryEntry(
    RegistryLocation Key,
    string? DisplayName,
    string? Publisher,
    string? DisplayVersion,
    string? InstallDate,
    int? EstimatedSizeKb,
    string? InstallLocation,
    string? DisplayIcon,
    string? UninstallString,
    int? SystemComponent,
    string? ParentKeyName,
    string? ReleaseType,
    int? NoRemove);

/// <summary>Règles pures (testables) : programmes proposés, composants protégés, exécutable principal.</summary>
internal static partial class ProgramClassifier
{
    // Composants d'exécution et éléments dont d'autres programmes dépendent : jamais proposés.
    [GeneratedRegex(@"(Visual C\+\+.*Redistributable|Microsoft \.NET|\.NET (Runtime|Desktop Runtime|Core)|ASP\.NET Core|Windows Desktop Runtime|Windows App (SDK|Runtime)|WindowsAppRuntime|Microsoft Edge|WebView2|Update Health Tools|Microsoft GameInput|DirectX|Visual Studio.*(Build Tools|Installer)|Windows Software Development Kit|Windows Driver Kit|Microsoft Windows Desktop Runtime|Microsoft ODBC Driver|Microsoft OLE DB Driver|SQL Server .*(Native Client|LocalDB))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeRegex();

    [GeneratedRegex(@"\b(driver|drivers|pilote|pilotes|chipset|firmware|bios|thunderbolt)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DriverRegex();

    [GeneratedRegex(@"^(unins|uninst|uninstall|setup|install|update|updater|autoupdate|crashpad|crashreport|crashhandler|helper|maintenance|vc_redist|repair|elevat|notification|squirrel|createdump)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HelperExecutableRegex();

    /// <summary>Entrée visible dans « Applications installées » et désinstallable : nom, commande, pas de mise à jour ni de composant système.</summary>
    public static bool IsCandidate(UninstallRegistryEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.DisplayName) || string.IsNullOrWhiteSpace(e.UninstallString)) return false;
        if (e.SystemComponent == 1 || e.NoRemove == 1) return false;
        if (!string.IsNullOrWhiteSpace(e.ParentKeyName)) return false;
        if (e.ReleaseType is { } type && (type.Contains("Update", StringComparison.OrdinalIgnoreCase) || type.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
            return false;
        return !Regex.IsMatch(e.DisplayName, @"\(KB\d{6,}\)|^Update for |^Security Update for |^Hotfix for ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>Composants d'exécution, pilotes, sécurité et PCBoost lui-même : jamais proposés à la désinstallation.</summary>
    public static bool IsProtected(UninstallRegistryEntry e, string productName)
    {
        var name = e.DisplayName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(productName) && name.Trim().Equals(productName.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        if (RuntimeRegex().IsMatch(name)) return true;
        if (DriverRegex().IsMatch(name)) return true;
        if (KnownSoftware.IsSecuritySoftware(name, null, e.Publisher)) return true;
        return KnownSoftware.IsHardwareDriverUtility(name, null, e.Publisher);
    }

    /// <summary>« "C:\App\app.exe",0 » → « C:\App\app.exe » ; null si ce n'est pas un exécutable désigné par un chemin absolu.</summary>
    public static string? ExecutableFromIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var text = displayIcon.Trim();
        var comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) text = text[..comma];
        text = text.Trim().Trim('"');
        return text.Length > 7 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] == '\\' && text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? text
            : null;
    }

    public static bool IsHelperExecutable(string fileName) => HelperExecutableRegex().IsMatch(fileName);

    /// <summary>Dossier d'installation exploitable : chemin absolu, hors du dossier Windows et différent des dossiers racines (lecteur, Program Files, profil).</summary>
    public static bool IsUsableInstallLocation(string? location, string? windowsDirectory, IEnumerable<string?> forbiddenRoots)
    {
        if (string.IsNullOrWhiteSpace(location)) return false;
        var path = location.Trim().Trim('"').TrimEnd('\\');
        if (path.Length <= 3 || !(char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')) return false;
        if (windowsDirectory is { Length: > 2 } windows)
        {
            var win = windows.TrimEnd('\\');
            if (path.Equals(win, StringComparison.OrdinalIgnoreCase) || path.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return !forbiddenRoots.Any(root => !string.IsNullOrWhiteSpace(root) && path.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>« 20240317 » → 17/03/2024 ; null si la date est absente ou invalide.</summary>
    public static DateOnly? ParseInstallDate(string? value)
        => value is { Length: 8 } && DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
