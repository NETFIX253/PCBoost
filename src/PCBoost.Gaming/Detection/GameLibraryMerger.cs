using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Gaming;
using PCBoost.Gaming.Common;

namespace PCBoost.Gaming.Detection;

/// <summary>
/// Refuse les dossiers d'installation trop larges (racine de disque, Program Files, profil utilisateur, Windows…) :
/// une entrée de registre incorrecte ne doit jamais faire passer tous les programmes d'un dossier pour un jeu.
/// </summary>
internal sealed class InstallDirectoryGuard
{
    private readonly HashSet<string> _forbidden = new(StringComparer.Ordinal);
    private readonly string? _windows;

    public InstallDirectoryGuard(IFileSystemProvider fileSystem)
    {
        foreach (var folder in Enum.GetValues<KnownFolder>())
        {
            var path = fileSystem.GetKnownFolder(folder);
            // Le dossier connu et tous ses parents (« C:\Users\x\AppData\Local » → « C:\Users\x\AppData », « C:\Users\x », « C:\Users »).
            for (var p = WinPath.Normalize(path); !string.IsNullOrEmpty(p) && !WinPath.IsDriveRoot(p); p = WinPath.GetDirectoryName(p) ?? string.Empty)
                _forbidden.Add(WinPath.Key(p));
        }
        _windows = fileSystem.GetKnownFolder(KnownFolder.WindowsDirectory);
    }

    public bool IsPlausible(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || WinPath.IsDriveRoot(directory)) return false;
        var key = WinPath.Key(directory);
        if (_forbidden.Contains(key)) return false;
        if (_windows is not null && WinPath.IsUnder(directory, _windows)) return false;
        // Racine d'une bibliothèque Steam (« …\steamapps\common ») ou dossier XboxGames.
        var name = WinPath.GetFileName(directory);
        return !name.Equals("common", StringComparison.OrdinalIgnoreCase) && !name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("XboxGames", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Fusion des résultats des scanners : dédoublonnage par dossier d'installation ou exécutable.</summary>
internal static class GameLibraryMerger
{
    private static int Priority(GameSource source) => source switch
    {
        GameSource.Custom => 0,
        GameSource.Steam => 1,
        GameSource.EpicGames => 2,
        GameSource.Gog => 3,
        GameSource.Ubisoft => 4,
        GameSource.EA => 5,
        GameSource.BattleNet => 6,
        GameSource.Riot => 7,
        GameSource.Xbox => 8,
        GameSource.WindowsGameConfig => 9,
        _ => 10,
    };

    public static IReadOnlyList<GameInfo> Merge(IEnumerable<GameInfo> games, Func<string?, bool> isPlausibleDirectory)
    {
        var result = new List<GameInfo>();
        foreach (var game in games.OrderBy(g => Priority(g.Source)).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            var index = FindDuplicate(result, game, isPlausibleDirectory);
            if (index < 0)
            {
                var clean = game.InstallDirectory is not null && !isPlausibleDirectory(game.InstallDirectory)
                    ? game with { InstallDirectory = null }
                    : game;
                result.Add(clean);
            }
            else
            {
                result[index] = Combine(result[index], game, isPlausibleDirectory);
            }
        }
        return result.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int FindDuplicate(List<GameInfo> existing, GameInfo game, Func<string?, bool> plausible)
    {
        for (var i = 0; i < existing.Count; i++)
        {
            var e = existing[i];
            if (game.InstallDirectory is not null && e.InstallDirectory is not null
                && string.Equals(WinPath.Key(game.InstallDirectory), WinPath.Key(e.InstallDirectory), StringComparison.Ordinal))
                return i;
            if (game.ExecutablePath is not null && e.ExecutablePath is not null
                && string.Equals(WinPath.Key(game.ExecutablePath), WinPath.Key(e.ExecutablePath), StringComparison.Ordinal))
                return i;
            // Exécutable connu (Game Bar, jeu personnalisé) situé dans le dossier d'un jeu déjà listé, et inversement.
            if (game.ExecutablePath is not null && e.InstallDirectory is not null && plausible(e.InstallDirectory)
                && WinPath.IsUnder(game.ExecutablePath, e.InstallDirectory))
                return i;
            if (e.ExecutablePath is not null && game.InstallDirectory is not null && plausible(game.InstallDirectory)
                && WinPath.IsUnder(e.ExecutablePath, game.InstallDirectory))
                return i;
        }
        return -1;
    }

    private static GameInfo Combine(GameInfo primary, GameInfo other, Func<string?, bool> plausible)
    {
        var names = primary.ExecutableNames.Concat(other.ExecutableNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var directory = primary.InstallDirectory ?? (plausible(other.InstallDirectory) ? other.InstallDirectory : null);
        return primary with
        {
            InstallDirectory = directory,
            ExecutablePath = primary.ExecutablePath ?? other.ExecutablePath,
            ExecutableNames = names,
        };
    }
}
