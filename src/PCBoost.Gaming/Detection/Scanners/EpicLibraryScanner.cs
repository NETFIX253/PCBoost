using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Gaming;
using PCBoost.Gaming.Common;

namespace PCBoost.Gaming.Detection.Scanners;

/// <summary>
/// Jeux Epic Games : manifestes JSON <c>&lt;ProgramData&gt;\Epic\EpicGamesLauncher\Data\Manifests\*.item</c>
/// (DisplayName, InstallLocation, LaunchExecutable). Installations incomplètes, contenus additionnels et applications
/// hors catégorie « games » ignorés.
/// </summary>
public sealed class EpicLibraryScanner : IGameLibraryScanner
{
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private readonly IFileSystemProvider _fileSystem;
    private readonly ILogger<EpicLibraryScanner> _logger;

    public EpicLibraryScanner(IFileSystemProvider fileSystem, ILogger<EpicLibraryScanner>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? NullLogger<EpicLibraryScanner>.Instance;
    }

    public GameSource Source => GameSource.EpicGames;

    public Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken)
        => Task.FromResult(Scan(cancellationToken));

    private IReadOnlyList<GameInfo> Scan(CancellationToken cancellationToken)
    {
        var programData = _fileSystem.GetKnownFolder(KnownFolder.ProgramData);
        if (string.IsNullOrEmpty(programData)) return [];
        var manifests = WinPath.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!_fileSystem.DirectoryExists(manifests)) return [];

        var games = new List<GameInfo>();
        foreach (var file in _fileSystem.EnumerateFiles(manifests, recursive: false, cancellationToken))
        {
            if (!file.Path.EndsWith(".item", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var game = Parse(_fileSystem.ReadAllText(file.Path));
                if (game is not null && _fileSystem.DirectoryExists(game.InstallDirectory!)) games.Add(game);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Manifeste Epic illisible : ignoré");
            }
        }
        return games;
    }

    /// <summary>Analyse un manifeste .item ; null s'il ne décrit pas un jeu installé.</summary>
    internal static GameInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json, JsonOptions);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (GetBool(root, "bIsIncompleteInstall") == true) return null;

        if (root.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array)
        {
            var isGame = categories.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String
                && string.Equals(c.GetString(), "games", StringComparison.OrdinalIgnoreCase));
            if (!isGame) return null;
        }

        var appName = GetString(root, "AppName");
        var mainGame = GetString(root, "MainGameAppName");
        if (appName is not null && mainGame is not null && !string.Equals(appName, mainGame, StringComparison.OrdinalIgnoreCase))
            return null; // Contenu additionnel d'un autre jeu.

        var installLocation = GetString(root, "InstallLocation");
        if (installLocation is null || WinPath.IsDriveRoot(installLocation)) return null;
        var displayName = GetString(root, "DisplayName") ?? ScannerSupport.DeriveNameFromDirectory(installLocation);
        var launch = GetString(root, "LaunchExecutable");
        var exePath = launch is null ? null : WinPath.Combine(installLocation, launch);
        if (exePath is not null && !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exePath = null;

        var id = appName ?? GetString(root, "CatalogItemId") ?? WinPath.Key(installLocation);
        return ScannerSupport.Create($"epic:{id}", displayName, GameSource.EpicGames, installLocation, exePath);
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()!.Trim()
            : null;

    private static bool? GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) ? p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        } : null;
}
