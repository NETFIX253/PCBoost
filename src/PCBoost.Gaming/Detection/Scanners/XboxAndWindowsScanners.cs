using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Services;
using PCBoost.Gaming.Common;

namespace PCBoost.Gaming.Detection.Scanners;

/// <summary>
/// Jeux Xbox / Microsoft Store installés dans le dossier par défaut : chaque disque fixe →
/// <c>XboxGames\*\Content\MicrosoftGame.config</c> (<c>ShellVisuals@DefaultDisplayName</c>, <c>ExecutableList/Executable@Name</c>).
/// </summary>
public sealed class XboxLibraryScanner : IGameLibraryScanner
{
    private readonly IFileSystemProvider _fileSystem;
    private readonly ILogger<XboxLibraryScanner> _logger;

    public XboxLibraryScanner(IFileSystemProvider fileSystem, ILogger<XboxLibraryScanner>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? NullLogger<XboxLibraryScanner>.Instance;
    }

    public GameSource Source => GameSource.Xbox;

    public Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken)
    {
        var games = new List<GameInfo>();
        foreach (var root in _fileSystem.GetFixedDriveRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xboxGames = WinPath.Combine(root, "XboxGames");
            if (!_fileSystem.DirectoryExists(xboxGames)) continue;
            foreach (var gameDir in _fileSystem.EnumerateDirectories(xboxGames))
            {
                var content = WinPath.Combine(gameDir, "Content");
                var config = WinPath.Combine(content, "MicrosoftGame.config");
                if (!_fileSystem.FileExists(config)) continue;
                try
                {
                    var game = Parse(_fileSystem.ReadAllText(config), content, WinPath.GetFileName(gameDir));
                    if (game is not null) games.Add(game);
                }
                catch (XmlException ex)
                {
                    _logger.LogDebug(ex, "MicrosoftGame.config illisible : ignoré");
                }
            }
        }
        return Task.FromResult<IReadOnlyList<GameInfo>>(games);
    }

    /// <summary>Analyse un MicrosoftGame.config (DTD interdite, aucune résolution externe).</summary>
    internal static GameInfo? Parse(string? xml, string contentDirectory, string folderName)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root;
        if (root is null) return null;

        var displayName = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals")?.Attribute("DefaultDisplayName")?.Value?.Trim();
        if (string.IsNullOrEmpty(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            displayName = folderName;
        var identity = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name")?.Value?.Trim();
        var executables = root.Descendants()
            .Where(e => e.Name.LocalName == "Executable")
            .Select(e => e.Attribute("Name")?.Value)
            .Where(n => !string.IsNullOrWhiteSpace(n) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Select(n => n!)
            .ToList();
        var mainExe = executables.Count > 0 ? WinPath.Combine(contentDirectory, executables[0]) : null;
        var id = string.IsNullOrEmpty(identity) ? folderName.ToLowerInvariant() : identity;
        return ScannerSupport.Create($"xbox:{id}", displayName, GameSource.Xbox, contentDirectory, mainExe, executables);
    }
}

/// <summary>
/// Jeux reconnus par Windows (Game Bar) : <c>HKCU\System\GameConfigStore\Children\*</c> → <c>MatchedExeFullPath</c>.
/// Entrées obsolètes (exécutable absent), lanceurs et utilitaires ignorés.
/// </summary>
public sealed class GameConfigStoreScanner : IGameLibraryScanner
{
    internal static readonly RegistryLocation ChildrenKey = new(RegistryHiveKind.CurrentUser, @"System\GameConfigStore\Children");

    private readonly IRegistryProvider _registry;
    private readonly IFileSystemProvider _fileSystem;
    private readonly GameSignatureDatabase _signatures;

    public GameConfigStoreScanner(IRegistryProvider registry, IFileSystemProvider fileSystem, GameSignatureDatabase signatures)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
    }

    public GameSource Source => GameSource.WindowsGameConfig;

    public Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken)
    {
        var windows = _fileSystem.GetKnownFolder(KnownFolder.WindowsDirectory);
        var games = new List<GameInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in _registry.GetSubKeyNames(ChildrenKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ChildrenKey with { KeyPath = ChildrenKey.KeyPath + "\\" + child };
            var exe = ScannerSupport.CleanExecutablePath(ScannerSupport.GetString(_registry, key, "MatchedExeFullPath", _fileSystem));
            if (exe is null || !seen.Add(WinPath.Key(exe))) continue;
            if (windows is not null && WinPath.IsUnder(exe, windows)) continue;
            if (_signatures.IsExcluded(WinPath.GetFileName(exe))) continue;
            if (!_fileSystem.FileExists(exe)) continue;
            var name = _signatures.TryGetGameName(WinPath.GetFileName(exe), out var known) ? known : ScannerSupport.DeriveName(exe);
            games.Add(ScannerSupport.Create($"gcs:{child.ToLowerInvariant()}", name, GameSource.WindowsGameConfig, null, exe));
        }
        return Task.FromResult<IReadOnlyList<GameInfo>>(games);
    }
}

/// <summary>Jeux ajoutés manuellement par l'utilisateur (<c>GamingSettings.CustomGames</c>).</summary>
public sealed class CustomGamesScanner : IGameLibraryScanner
{
    private readonly ISettingsService _settings;

    public CustomGamesScanner(ISettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public GameSource Source => GameSource.Custom;

    public Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken)
    {
        var games = new List<GameInfo>();
        foreach (var entry in _settings.Current.Gaming.CustomGames)
        {
            var exe = WinPath.Normalize(entry.ExecutablePath);
            if (exe.Length == 0 || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var name = string.IsNullOrWhiteSpace(entry.Name) ? WinPath.GetFileNameWithoutExtension(exe) : entry.Name;
            games.Add(ScannerSupport.Create($"custom:{WinPath.Key(exe)}", name, GameSource.Custom, null, exe));
        }
        return Task.FromResult<IReadOnlyList<GameInfo>>(games);
    }
}
