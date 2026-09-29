using System.Text;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Startup;

/// <summary>Extraction du chemin d'exécutable d'une ligne de commande de démarrage (guillemets, variables d'environnement, « .exe »).</summary>
public static class CommandLineParser
{
    /// <summary>
    /// Renvoie le chemin de l'exécutable : texte entre guillemets initiaux, sinon jusqu'à la première occurrence de « .exe »,
    /// sinon jusqu'au premier espace. Les variables %VAR% connues sont développées. Un nom nu est recherché dans System32/Windows.
    /// </summary>
    public static string? ExtractExecutable(string? commandLine, IFileSystemProvider fileSystem)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var command = ExpandEnvironment(commandLine.Trim(), fileSystem).Replace('/', '\\');

        string candidate;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            candidate = end > 1 ? command[1..end] : command.Trim('"');
        }
        else
        {
            var exe = FindExeEnd(command);
            if (exe > 0)
            {
                candidate = command[..exe];
            }
            else
            {
                var space = command.IndexOfAny([' ', '\t']);
                candidate = space > 0 ? command[..space] : command;
            }
        }

        candidate = candidate.Trim().Trim('"');
        if (candidate.Length == 0) return null;
        return IsRooted(candidate) ? candidate : ResolveBareName(candidate, fileSystem);
    }

    /// <summary>Développe les variables %NOM% connues du fournisseur ; les inconnues sont laissées telles quelles.</summary>
    public static string ExpandEnvironment(string text, IFileSystemProvider fileSystem)
    {
        if (!text.Contains('%')) return text;
        var builder = new StringBuilder(text.Length + 32);
        var i = 0;
        while (i < text.Length)
        {
            var start = text.IndexOf('%', i);
            if (start < 0) { builder.Append(text, i, text.Length - i); break; }
            var end = text.IndexOf('%', start + 1);
            if (end < 0) { builder.Append(text, i, text.Length - i); break; }
            builder.Append(text, i, start - i);
            var name = text[(start + 1)..end];
            var value = name.Length == 0 ? null : fileSystem.GetEnvironmentVariable(name) ?? KnownVariable(name, fileSystem);
            if (value is null)
            {
                builder.Append(text, start, end - start + 1);
            }
            else
            {
                builder.Append(value);
            }
            i = end + 1;
        }
        return builder.ToString();
    }

    private static string? KnownVariable(string name, IFileSystemProvider fileSystem) => name.ToUpperInvariant() switch
    {
        "SYSTEMROOT" or "WINDIR" => fileSystem.GetKnownFolder(KnownFolder.WindowsDirectory),
        "PROGRAMFILES" => fileSystem.GetKnownFolder(KnownFolder.ProgramFiles),
        "PROGRAMFILES(X86)" => fileSystem.GetKnownFolder(KnownFolder.ProgramFilesX86),
        "PROGRAMDATA" or "ALLUSERSPROFILE" => fileSystem.GetKnownFolder(KnownFolder.ProgramData),
        "LOCALAPPDATA" => fileSystem.GetKnownFolder(KnownFolder.LocalAppData),
        "APPDATA" => fileSystem.GetKnownFolder(KnownFolder.RoamingAppData),
        "USERPROFILE" => fileSystem.GetKnownFolder(KnownFolder.UserProfile),
        "TEMP" or "TMP" => fileSystem.GetKnownFolder(KnownFolder.UserTemp),
        _ => null,
    };

    /// <summary>Position juste après le premier « .exe » suivi d'une fin, d'un espace, d'une virgule ou d'un guillemet (-1 sinon).</summary>
    private static int FindExeEnd(string command)
    {
        var from = 0;
        while (from < command.Length)
        {
            var index = command.IndexOf(".exe", from, StringComparison.OrdinalIgnoreCase);
            if (index <= 0) return -1;
            var end = index + 4;
            if (end == command.Length || command[end] is ' ' or '\t' or ',' or '"') return end;
            from = end;
        }
        return -1;
    }

    private static bool IsRooted(string path)
        => (path.Length > 2 && path[1] == ':' && path[2] == '\\') || path.StartsWith(@"\\", StringComparison.Ordinal);

    private static string ResolveBareName(string name, IFileSystemProvider fileSystem)
    {
        var fileName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.Contains('.') ? name : name + ".exe";
        var windows = fileSystem.GetKnownFolder(KnownFolder.WindowsDirectory);
        if (windows is not null && !name.Contains('\\'))
        {
            foreach (var folder in new[] { "System32", "SysWOW64", string.Empty })
            {
                var candidate = folder.Length == 0 ? PathUtil.Combine(windows, fileName) : PathUtil.Combine(PathUtil.Combine(windows, folder), fileName);
                if (fileSystem.FileExists(candidate)) return candidate;
            }
        }
        return name;
    }
}
