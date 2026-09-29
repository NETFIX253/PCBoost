namespace PCBoost.Platform.Elevation;

/// <summary>
/// Emplacements utilisés par l'élévation ponctuelle. Le fichier de résultat est toujours
/// <c>%LOCALAPPDATA%\PCBoost\elevation\&lt;GUID&gt;.json</c> : l'Elevator refuse tout autre chemin.
/// </summary>
internal static class ElevationPaths
{
    public const string DataFolderName = "PCBoost";
    public const string ElevationFolderName = "elevation";
    public const string LogFolderName = "Logs";
    public const string ElevatorExecutableName = "PCBoost.Elevator.exe";
    public const string ElevatorLogFileName = "elevator.log";

    /// <summary>Suffixe imposé (insensible à la casse) du dossier contenant le fichier de résultat.</summary>
    public const string ResultDirectorySuffix = @"\AppData\Local\" + DataFolderName + @"\" + ElevationFolderName + @"\";

    public const string LogDirectorySuffix = @"\AppData\Local\" + DataFolderName + @"\" + LogFolderName + @"\";

    private const int MaxPathLength = 400;

    public static string ElevatorPath => Path.Combine(AppContext.BaseDirectory, ElevatorExecutableName);

    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);

    public static string ElevationDirectory => Path.Combine(LocalAppData, DataFolderName, ElevationFolderName);

    public static string LogDirectory => Path.Combine(LocalAppData, DataFolderName, LogFolderName);

    public static string NewResultPath() => Path.Combine(ElevationDirectory, Guid.NewGuid().ToString("D") + ".json");

    /// <summary>
    /// Chemin local absolu (lettre de lecteur), sans UNC ni préfixe \\?\, sans « . », « .. », segment vide, flux de données
    /// alternatif, guillemet ou caractère de contrôle, se terminant par <see cref="ResultDirectorySuffix"/> + &lt;GUID&gt;.json.
    /// </summary>
    public static bool IsValidResultPath(string? path)
    {
        if (!IsStrictLocalPath(path)) return false;
        var separator = path!.LastIndexOf('\\');
        var directory = path[..(separator + 1)];
        var fileName = path[(separator + 1)..];
        if (!directory.EndsWith(ResultDirectorySuffix, StringComparison.OrdinalIgnoreCase)) return false;
        if (directory.Length <= ResultDirectorySuffix.Length + 2) return false; // Il faut au moins « X:\<profil> » avant AppData.
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        return Guid.TryParseExact(fileName[..^".json".Length], "D", out _);
    }

    /// <summary>Chemin final (résolu) d'un fichier de résultat : doit rester dans un dossier …\AppData\Local\PCBoost\elevation\.</summary>
    public static bool IsAcceptableResolvedResultPath(string resolvedPath, string requestedPath)
        => IsValidResultPath(resolvedPath)
           && string.Equals(WindowsFileName(resolvedPath), WindowsFileName(requestedPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Nom de fichier d'un chemin Windows (indépendant du séparateur de la plateforme d'exécution).</summary>
    internal static string WindowsFileName(string path) => path[(path.LastIndexOf('\\') + 1)..];

    public static bool IsAcceptableResolvedLogPath(string resolvedPath)
        => IsStrictLocalPath(resolvedPath)
           && resolvedPath.EndsWith(LogDirectorySuffix + ElevatorLogFileName, StringComparison.OrdinalIgnoreCase);

    internal static bool IsStrictLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength) return false;
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
        if (path.IndexOf(':', 2) >= 0) return false; // Flux de données alternatifs.
        if (path.Contains('/') || path.Contains('"') || path.Any(c => char.IsControl(c) || c is '<' or '>' or '|' or '?' or '*')) return false;
        var segments = path[3..].Split('\\');
        return segments.All(s => s.Length > 0 && s != "." && s != ".." && !s.EndsWith('.') && !s.EndsWith(' '));
    }

    /// <summary>Supprime les fichiers de résultat abandonnés (plus d'un jour).</summary>
    public static void DeleteStaleResults()
    {
        try
        {
            var directory = new DirectoryInfo(ElevationDirectory);
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return;
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var file in directory.EnumerateFiles("*.json"))
            {
                if (file.LastWriteTimeUtc < cutoff && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                    file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Nettoyage opportuniste : sans conséquence en cas d'échec.
        }
    }
}
