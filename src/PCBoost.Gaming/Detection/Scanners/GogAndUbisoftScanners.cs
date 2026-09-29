using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Gaming;
using PCBoost.Gaming.Common;

namespace PCBoost.Gaming.Detection.Scanners;

/// <summary>Jeux GOG : <c>HKLM\SOFTWARE\GOG.com\Games\*</c> (vue 32 bits : gameName, path, exe). Contenus additionnels (dependsOn) ignorés.</summary>
public sealed class GogLibraryScanner : IGameLibraryScanner
{
    internal static readonly RegistryLocation GamesKey = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\GOG.com\Games", RegistryViewKind.Registry32);

    private readonly IRegistryProvider _registry;
    private readonly IFileSystemProvider _fileSystem;

    public GogLibraryScanner(IRegistryProvider registry, IFileSystemProvider fileSystem)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public GameSource Source => GameSource.Gog;

    public Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken)
    {
        var games = new List<GameInfo>();
        foreach (var id in _registry.GetSubKeyNames(GamesKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = GamesKey with { KeyPath = GamesKey.KeyPath + "\\" + id };
            if (ScannerSupport.GetString(_registry, key, "dependsOn") is not null) continue;
            var path = ScannerSupport.GetString(_registry, key, "path", _fileSystem);
            if (path is null || WinPath.IsDriveRoot(path) || !_fileSystem.DirectoryExists(WinPath.Normalize(path))) continue;
            var exe = ScannerSupport.CleanExecutablePath(ScannerSupport.GetString(_registry, key, "exe", _fileSystem));
            var name = ScannerSupport.GetString(_registry, key, "gameName") ?? ScannerSupport.DeriveNameFromDirectory(path);
            games.Add(ScannerSupport.Create($"gog:{id}", name, GameSource.Gog, path, exe));
        }
        return Task.FromResult<IReadOnlyList<GameInfo>>(games);
    }
}

/// <summary>
/// Jeux Ubisoft Connect : <c>HKLM\SOFTWARE\Ubisoft\Launcher\Installs\*\InstallDir</c> (vue 32 bits) ; nom lu dans la clé de
/// désinstallation « Uplay Install &lt;id&gt; », sinon déduit du dossier.
/// </summary>
public sealed class UbisoftLibraryScanner : IGameLibraryScanner
{
    internal static readonly RegistryLocation InstallsKey = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\Ubisoft\Launcher\Installs", RegistryViewKind.Registry32);

    private readonly IRegistryProvider _registry;
    private readonly IFileSystemProvider _fileSystem;

    public UbisoftLibraryScanner(IRegistryProvider registry, IFileSystemProvider fileSystem)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public GameSource Source => GameSource.Ubisoft;

    public Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken)
    {
        var games = new List<GameInfo>();
        foreach (var id in _registry.GetSubKeyNames(InstallsKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = InstallsKey with { KeyPath = InstallsKey.KeyPath + "\\" + id };
            var dir = ScannerSupport.GetString(_registry, key, "InstallDir", _fileSystem);
            if (dir is null || WinPath.IsDriveRoot(dir) || !_fileSystem.DirectoryExists(WinPath.Normalize(dir))) continue;
            var name = FindUninstallName(id) ?? ScannerSupport.DeriveNameFromDirectory(dir);
            games.Add(ScannerSupport.Create($"ubisoft:{id}", name, GameSource.Ubisoft, dir, null));
        }
        return Task.FromResult<IReadOnlyList<GameInfo>>(games);
    }

    private string? FindUninstallName(string id)
    {
        foreach (var view in new[] { RegistryViewKind.Registry32, RegistryViewKind.Registry64 })
        {
            var key = new RegistryLocation(RegistryHiveKind.LocalMachine, UninstallRegistry.UninstallKeyPath + @"\Uplay Install " + id, view);
            var name = ScannerSupport.GetString(_registry, key, "DisplayName");
            if (name is not null) return name;
        }
        return null;
    }
}
