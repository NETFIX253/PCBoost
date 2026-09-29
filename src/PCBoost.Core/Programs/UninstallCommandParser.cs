using System.Text.RegularExpressions;
using PCBoost.Core.Models.Programs;

namespace PCBoost.Core.Programs;

/// <summary>
/// Lecture de la commande de désinstallation déclarée par l'éditeur (UninstallString). Seuls deux cas sont acceptés :
/// Windows Installer (msiexec avec un code produit, toujours lancé en mode interactif « /x ») et un exécutable désigné par
/// un chemin absolu. Les interpréteurs de commandes et hôtes de scripts sont refusés : PCBoost renvoie alors vers les
/// Paramètres de Windows.
/// </summary>
public static partial class UninstallCommandParser
{
    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe",
        "explorer.exe", "conhost.exe", "bash.exe", "wsl.exe",
    };

    [GeneratedRegex(@"^\s*""?(?:[a-z]:\\[^""]*\\)?msiexec(?:\.exe)?""?\s+.*?/[ix]\s*(\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MsiRegex();

    public const string WindowsInstaller = "msiexec.exe";

    public static UninstallCommand? Parse(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString) || uninstallString.Length > 2048) return null;
        var text = uninstallString.Trim();
        if (text.IndexOfAny(['\r', '\n', '\0']) >= 0) return null;

        if (MsiRegex().Match(text) is { Success: true } msi)
        {
            var code = msi.Groups[1].Value.ToUpperInvariant();
            return new UninstallCommand(WindowsInstaller, $"/x {code}", true, code);
        }

        string file, args;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end <= 1) return null;
            file = text[1..end];
            args = text[(end + 1)..].Trim();
        }
        else
        {
            var index = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (index < 0) return null;
            var stop = index + 4;
            if (stop < text.Length && !char.IsWhiteSpace(text[stop])) return null;
            file = text[..stop];
            args = text[stop..].Trim();
        }

        if (!IsAbsoluteExecutable(file)) return null;
        var name = file[(file.LastIndexOfAny(['\\', '/']) + 1)..];
        if (BlockedHosts.Contains(name) || name.Equals(WindowsInstaller, StringComparison.OrdinalIgnoreCase)) return null;
        return new UninstallCommand(file, args, false, null);
    }

    private static bool IsAbsoluteExecutable(string path)
        => path.Length > 7
           && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
           && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
           && path.IndexOfAny(['"', '<', '>', '|', '*', '?']) < 0
           && !path.Contains(@"\..\", StringComparison.Ordinal);
}
