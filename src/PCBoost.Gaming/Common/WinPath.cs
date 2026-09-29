namespace PCBoost.Gaming.Common;

/// <summary>
/// Manipulation de chemins Windows indépendante de la plateforme d'exécution (les tests tournent aussi sous Linux,
/// où <see cref="System.IO.Path"/> ne reconnaît pas « \ » comme séparateur).
/// </summary>
internal static class WinPath
{
    /// <summary>Remplace « / » par « \ », supprime les guillemets et les séparateurs finaux (sauf pour une racine « C:\ »).</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var p = path.Trim().Trim('"').Replace('/', '\\');
        var unc = p.StartsWith(@"\\", StringComparison.Ordinal);
        while (p.Contains(@"\\", StringComparison.Ordinal))
            p = p.Replace(@"\\", @"\", StringComparison.Ordinal);
        if (unc) p = @"\" + p;
        p = p.TrimEnd('\\');
        if (p.Length == 2 && p[1] == ':') p += @"\";
        return p;
    }

    public static string Combine(string root, params string[] parts)
    {
        var result = Normalize(root);
        foreach (var part in parts)
        {
            var clean = Normalize(part).TrimStart('\\');
            if (clean.Length == 0) continue;
            result = result.Length == 0 ? clean : result.EndsWith('\\') ? result + clean : result + "\\" + clean;
        }
        return result;
    }

    public static string GetFileName(string? path)
    {
        var p = Normalize(path);
        var i = p.LastIndexOf('\\');
        return i < 0 ? p : p[(i + 1)..];
    }

    public static string GetFileNameWithoutExtension(string? path)
    {
        var name = GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }

    public static string? GetDirectoryName(string? path)
    {
        var p = Normalize(path);
        var i = p.LastIndexOf('\\');
        if (i < 0) return null;
        if (i == 2 && p[1] == ':') return p[..3];
        return i == 0 ? null : p[..i];
    }

    /// <summary>Le chemin <paramref name="path"/> est-il situé sous le dossier <paramref name="directory"/> (ou égal) ?</summary>
    public static bool IsUnder(string? path, string? directory)
    {
        var p = Normalize(path);
        var d = Normalize(directory);
        if (p.Length == 0 || d.Length == 0) return false;
        if (string.Equals(p, d, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = d.EndsWith('\\') ? d : d + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Clé de comparaison (normalisée, minuscules).</summary>
    public static string Key(string? path) => Normalize(path).ToLowerInvariant();

    /// <summary>Racine de lecteur (« C:\ ») ou chemin vide.</summary>
    public static bool IsDriveRoot(string? path)
    {
        var p = Normalize(path);
        return p.Length <= 3 && (p.Length < 2 || p[1] == ':');
    }

    /// <summary>Nombre de composants après la racine (« D:\Games\X » → 2).</summary>
    public static int Depth(string? path)
    {
        var p = Normalize(path);
        if (p.Length == 0) return 0;
        var parts = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts[0].EndsWith(':') ? parts.Length - 1 : parts.Length;
    }

    /// <summary>Remplace le préfixe du profil utilisateur par %USERPROFILE% (journalisation sans donnée personnelle).</summary>
    public static string ForLog(string? path, string? userProfile)
    {
        var p = Normalize(path);
        if (!string.IsNullOrEmpty(userProfile) && IsUnder(p, userProfile))
            return "%USERPROFILE%" + p[Normalize(userProfile).Length..];
        return p;
    }
}
