using System.Globalization;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Gaming;
using PCBoost.Gaming.Common;

namespace PCBoost.Gaming.Detection.Scanners;

/// <summary>Outils communs aux scanners de bibliothèques.</summary>
internal static class ScannerSupport
{
    private static readonly HashSet<string> GenericFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin32", "bin64", "binaries", "win64", "win32", "x64", "x86", "amd64", "game", "games", "retail", "live",
        "shipping", "client", "common", "release", "build", "app", "program", "content", "windows", "windowsnoeditor",
        "wingdk", "dx11", "dx12", "vulkan", "exe", "launcher", "steamapps", "pbe", "beta",
    };

    private static readonly HashSet<string> ContainerFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "program files", "program files (x86)", "steamlibrary", "xboxgames", "epic games", "gog games", "gog galaxy",
        "riot games", "ea games", "origin games", "ubisoft", "ubisoft game launcher", "users", "appdata", "local", "roaming",
    };

    public static string? GetString(IRegistryProvider registry, RegistryLocation location, string valueName, IFileSystemProvider? fileSystem = null)
    {
        var data = registry.GetValue(location, valueName);
        if (data is null) return null;
        var text = data.Type switch
        {
            RegistryValueType.String or RegistryValueType.ExpandString => data.Value as string ?? Convert.ToString(data.Value, CultureInfo.InvariantCulture),
            RegistryValueType.DWord or RegistryValueType.QWord => Convert.ToString(data.Value, CultureInfo.InvariantCulture),
            RegistryValueType.MultiString => data.Value is string[] arr ? arr.FirstOrDefault() : null,
            _ => data.Value?.ToString(),
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        return data.Type == RegistryValueType.ExpandString && fileSystem is not null ? ExpandEnvironment(text, fileSystem) : text;
    }

    public static long? GetNumber(IRegistryProvider registry, RegistryLocation location, string valueName)
    {
        var data = registry.GetValue(location, valueName);
        if (data is null) return null;
        try
        {
            return data.Type switch
            {
                RegistryValueType.DWord or RegistryValueType.QWord => Convert.ToInt64(data.Value, CultureInfo.InvariantCulture),
                RegistryValueType.String when long.TryParse(data.Value as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Remplace les variables « %NOM% » via le fournisseur (les variables inconnues sont conservées).</summary>
    public static string ExpandEnvironment(string text, IFileSystemProvider fileSystem)
    {
        if (!text.Contains('%')) return text;
        var result = new System.Text.StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var start = text.IndexOf('%', i);
            if (start < 0) { result.Append(text, i, text.Length - i); break; }
            var end = text.IndexOf('%', start + 1);
            if (end < 0) { result.Append(text, i, text.Length - i); break; }
            result.Append(text, i, start - i);
            var name = text[(start + 1)..end];
            var value = name.Length == 0 ? null : fileSystem.GetEnvironmentVariable(name);
            result.Append(value ?? text[start..(end + 1)]);
            i = end + 1;
        }
        return result.ToString();
    }

    /// <summary>Retire les guillemets et l'index d'icône éventuels (« "C:\x\y.exe",0 »).</summary>
    public static string? CleanExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.StartsWith('"'))
        {
            var close = v.IndexOf('"', 1);
            v = close > 0 ? v[1..close] : v.Trim('"');
        }
        else
        {
            var comma = v.LastIndexOf(',');
            if (comma > 0 && int.TryParse(v[(comma + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) v = v[..comma];
        }
        v = WinPath.Normalize(v);
        return v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? v : null;
    }

    /// <summary>Nom lisible déduit du dossier d'un exécutable (en ignorant « bin », « Win64 »…), sinon du nom de fichier.</summary>
    public static string DeriveName(string executablePath)
    {
        var dir = WinPath.GetDirectoryName(executablePath);
        while (!string.IsNullOrEmpty(dir) && !WinPath.IsDriveRoot(dir))
        {
            var folder = WinPath.GetFileName(dir);
            if (ContainerFolderNames.Contains(folder)) break;
            if (!GenericFolderNames.Contains(folder)) return folder;
            dir = WinPath.GetDirectoryName(dir);
        }
        return WinPath.GetFileNameWithoutExtension(executablePath);
    }

    /// <summary>Nom lisible déduit d'un dossier d'installation (« …\VALORANT\live » → « VALORANT »).</summary>
    public static string DeriveNameFromDirectory(string directory)
    {
        var dir = WinPath.Normalize(directory);
        while (!string.IsNullOrEmpty(dir) && !WinPath.IsDriveRoot(dir))
        {
            var folder = WinPath.GetFileName(dir);
            if (!GenericFolderNames.Contains(folder)) return folder;
            dir = WinPath.GetDirectoryName(dir);
        }
        return WinPath.GetFileName(directory);
    }

    public static GameInfo Create(string id, string name, GameSource source, string? installDirectory, string? executablePath, IEnumerable<string>? executableNames = null)
    {
        var names = new List<string>();
        void AddName(string? n)
        {
            var normalized = GameSignatureDatabase.Normalize(n);
            if (normalized.Length > 4 && !names.Contains(normalized, StringComparer.Ordinal)) names.Add(normalized);
        }
        if (executablePath is not null) AddName(executablePath);
        if (executableNames is not null) foreach (var n in executableNames) AddName(n);

        return new GameInfo
        {
            Id = id,
            Name = name.Trim(),
            Source = source,
            InstallDirectory = string.IsNullOrWhiteSpace(installDirectory) ? null : WinPath.Normalize(installDirectory),
            ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? null : WinPath.Normalize(executablePath),
            ExecutableNames = names,
        };
    }
}
