using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Gaming;
using PCBoost.Gaming.Common;

namespace PCBoost.Gaming.Detection.Scanners;

/// <summary>
/// Jeux Steam : dossier Steam (registre) → <c>steamapps\libraryfolders.vdf</c> (toutes les bibliothèques) →
/// <c>appmanifest_*.acf</c> → <c>steamapps\common\&lt;installdir&gt;</c>. Les outils (redistribuables, Proton, SteamVR…) sont exclus.
/// </summary>
public sealed class SteamLibraryScanner : IGameLibraryScanner
{
    private static readonly RegistryLocation UserSteamKey = new(RegistryHiveKind.CurrentUser, @"Software\Valve\Steam");
    private static readonly RegistryLocation MachineSteamKey32 = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\Valve\Steam", RegistryViewKind.Registry32);
    private static readonly RegistryLocation MachineSteamKey = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\Valve\Steam");

    /// <summary>Applications Steam qui ne sont pas des jeux.</summary>
    internal static readonly HashSet<string> ToolAppIds = new(StringComparer.Ordinal)
    {
        "228980",  // Steamworks Common Redistributables
        "250820",  // SteamVR
        "1070560", // Steam Linux Runtime
        "1391110", // Steam Linux Runtime - Soldier
        "1628350", // Steam Linux Runtime - Sniper
        "1493710", // Proton Experimental
        "2180100", // Proton Hotfix
        "1826330", // Proton EasyAntiCheat Runtime
        "1161040", // Proton BattlEye Runtime
        "858280", "930400", "961940", "996510", "1054830", "1113280", "1245040", "1420170", "1580130", "1887720", "2348590", "2805730", // Proton x.y
        "243730",  // Source SDK Base 2013 Singleplayer
        "243750",  // Source SDK Base 2013 Multiplayer
        "1007",    // Steamworks SDK Redist
        // Logiciels distribués par Steam qui ne sont pas des jeux (fonds d'écran, capture, création, utilitaires).
        "431960",  // Wallpaper Engine
        "1905180", // OBS Studio
        "365670",  // Blender
        "993090",  // Lossless Scaling
        "629520",  // Soundpad
        "431730",  // Aseprite
        "1325860", // VTube Studio
        "227260",  // DisplayFusion
        "388080",  // Borderless Gaming
        "1840",    // Source Filmmaker
    };

    private static readonly string[] ToolNamePrefixes =
    [
        "Proton ", "Proton-", "Steam Linux Runtime", "Steamworks", "SteamVR", "Steam Audio", "Source SDK", "Steam Controller",
        "Wallpaper Engine", "OBS Studio",
    ];

    private static readonly string[] ToolNameFragments = ["Redistributable", "Dedicated Server", " SDK"];

    private readonly IRegistryProvider _registry;
    private readonly IFileSystemProvider _fileSystem;
    private readonly ILogger<SteamLibraryScanner> _logger;

    public SteamLibraryScanner(IRegistryProvider registry, IFileSystemProvider fileSystem, ILogger<SteamLibraryScanner>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? NullLogger<SteamLibraryScanner>.Instance;
    }

    public GameSource Source => GameSource.Steam;

    public Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken)
        => Task.FromResult(Scan(cancellationToken));

    private IReadOnlyList<GameInfo> Scan(CancellationToken cancellationToken)
    {
        var steamPath = FindSteamPath();
        if (steamPath is null) return [];

        var games = new List<GameInfo>();
        var seenApps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var library in GetLibraryFolders(steamPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var steamApps = WinPath.Combine(library, "steamapps");
            if (!_fileSystem.DirectoryExists(steamApps)) continue;
            foreach (var file in _fileSystem.EnumerateFiles(steamApps, recursive: false, cancellationToken))
            {
                var fileName = WinPath.GetFileName(file.Path);
                if (!fileName.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase)
                    || !fileName.EndsWith(".acf", StringComparison.OrdinalIgnoreCase)) continue;
                var game = ReadManifest(file.Path, steamApps);
                if (game is not null && seenApps.Add(game.Id)) games.Add(game);
            }
        }
        _logger.LogDebug("Steam : {Count} jeu(x) trouvé(s)", games.Count);
        return games;
    }

    private string? FindSteamPath()
    {
        foreach (var (location, value) in new[] { (UserSteamKey, "SteamPath"), (MachineSteamKey32, "InstallPath"), (MachineSteamKey, "InstallPath") })
        {
            var path = ScannerSupport.GetString(_registry, location, value, _fileSystem);
            if (path is null) continue;
            var normalized = WinPath.Normalize(path);
            if (_fileSystem.DirectoryExists(normalized)) return normalized;
        }
        return null;
    }

    /// <summary>Bibliothèques Steam : le dossier Steam lui-même + celles de libraryfolders.vdf (ancien et nouveau format).</summary>
    internal IReadOnlyList<string> GetLibraryFolders(string steamPath)
    {
        var result = new List<string> { WinPath.Normalize(steamPath) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { WinPath.Key(steamPath) };
        foreach (var vdfPath in new[] { WinPath.Combine(steamPath, "steamapps", "libraryfolders.vdf"), WinPath.Combine(steamPath, "config", "libraryfolders.vdf") })
        {
            if (!_fileSystem.FileExists(vdfPath)) continue;
            foreach (var folder in ParseLibraryFolders(_fileSystem.ReadAllText(vdfPath)))
            {
                if (seen.Add(WinPath.Key(folder))) result.Add(WinPath.Normalize(folder));
                // SteamPath est souvent enregistré en minuscules : la casse de libraryfolders.vdf est plus lisible.
                else if (string.Equals(WinPath.Key(folder), WinPath.Key(result[0]), StringComparison.Ordinal)) result[0] = WinPath.Normalize(folder);
            }
        }
        return result;
    }

    /// <summary>Extrait les chemins de bibliothèques d'un libraryfolders.vdf (blocs « "0" { "path" … } » ou valeurs « "1" "D:\\Lib" »).</summary>
    internal static IReadOnlyList<string> ParseLibraryFolders(string? vdfText)
    {
        var root = VdfParser.Parse(vdfText);
        var block = root["libraryfolders"] ?? root.Children.FirstOrDefault(c => c.IsBlock);
        if (block is null) return [];
        var folders = new List<string>();
        foreach (var child in block.Children)
        {
            if (child.Key.Length == 0 || !child.Key.All(char.IsAsciiDigit)) continue;
            var path = child.IsBlock ? child.GetValue("path") : child.Value;
            if (!string.IsNullOrWhiteSpace(path)) folders.Add(path);
        }
        return folders;
    }

    private GameInfo? ReadManifest(string manifestPath, string steamApps)
    {
        var app = VdfParser.Parse(_fileSystem.ReadAllText(manifestPath))["AppState"];
        if (app is null) return null;
        var appId = app.GetValue("appid")?.Trim();
        var name = app.GetValue("name")?.Trim();
        var installDir = app.GetValue("installdir")?.Trim();
        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(installDir)) return null;
        if (IsTool(appId, name)) return null;

        var directory = WinPath.Combine(steamApps, "common", installDir);
        if (!_fileSystem.DirectoryExists(directory)) return null;
        return ScannerSupport.Create($"steam:{appId}", string.IsNullOrEmpty(name) ? installDir : name, GameSource.Steam, directory, null);
    }

    internal static bool IsTool(string appId, string? name)
    {
        if (ToolAppIds.Contains(appId)) return true;
        if (string.IsNullOrEmpty(name)) return false;
        return ToolNamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || ToolNameFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
    }
}
