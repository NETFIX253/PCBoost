using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Gaming;
using PCBoost.Gaming.Common;

namespace PCBoost.Gaming.Detection.Scanners;

/// <summary>Entrée « Programmes et fonctionnalités ».</summary>
internal sealed record UninstallEntry(string KeyName, string DisplayName, string? Publisher, string InstallLocation);

/// <summary>Lecture des clés de désinstallation (HKLM 64 et 32 bits, HKCU).</summary>
internal static class UninstallRegistry
{
    public const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly RegistryLocation[] Roots =
    [
        new(RegistryHiveKind.LocalMachine, UninstallKeyPath, RegistryViewKind.Registry64),
        new(RegistryHiveKind.LocalMachine, UninstallKeyPath, RegistryViewKind.Registry32),
        new(RegistryHiveKind.CurrentUser, UninstallKeyPath),
    ];

    public static IEnumerable<UninstallEntry> Enumerate(IRegistryProvider registry, IFileSystemProvider fileSystem, CancellationToken cancellationToken)
    {
        foreach (var root in Roots)
        {
            foreach (var sub in registry.GetSubKeyNames(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = root with { KeyPath = root.KeyPath + "\\" + sub };
                if (ScannerSupport.GetNumber(registry, key, "SystemComponent") == 1) continue;
                if (ScannerSupport.GetString(registry, key, "ParentKeyName") is not null) continue; // mise à jour d'un autre produit
                var name = ScannerSupport.GetString(registry, key, "DisplayName");
                var location = ScannerSupport.GetString(registry, key, "InstallLocation", fileSystem);
                if (name is null || location is null) continue;
                yield return new UninstallEntry(sub, name, ScannerSupport.GetString(registry, key, "Publisher"), WinPath.Normalize(location));
            }
        }
    }
}

/// <summary>Jeux d'un éditeur, lus dans les clés de désinstallation (le lanceur de l'éditeur est exclu).</summary>
public abstract class PublisherUninstallScanner : IGameLibraryScanner
{
    private readonly IRegistryProvider _registry;
    private readonly IFileSystemProvider _fileSystem;

    protected PublisherUninstallScanner(IRegistryProvider registry, IFileSystemProvider fileSystem)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public abstract GameSource Source { get; }

    /// <summary>Préfixes de l'éditeur (valeur « Publisher »).</summary>
    protected abstract IReadOnlyList<string> Publishers { get; }

    /// <summary>Noms exacts à exclure (lanceurs).</summary>
    protected abstract IReadOnlyList<string> ExcludedNames { get; }

    /// <summary>Fragments de noms à exclure (lanceur, anti-triche, redistribuables…).</summary>
    protected virtual IReadOnlyList<string> ExcludedNameFragments { get; } = ["Launcher", "Redistributable", "Anti-Cheat", "AntiCheat", "Uninstall"];

    protected IFileSystemProvider FileSystem => _fileSystem;

    public virtual Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken)
        => Task.FromResult(ScanUninstallEntries(cancellationToken));

    protected IReadOnlyList<GameInfo> ScanUninstallEntries(CancellationToken cancellationToken)
    {
        var games = new List<GameInfo>();
        var prefix = Source.ToString().ToLowerInvariant();
        foreach (var entry in UninstallRegistry.Enumerate(_registry, _fileSystem, cancellationToken))
        {
            if (entry.Publisher is null || !Publishers.Any(p => entry.Publisher.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (IsExcludedName(entry.DisplayName)) continue;
            if (WinPath.IsDriveRoot(entry.InstallLocation) || !_fileSystem.DirectoryExists(entry.InstallLocation)) continue;
            games.Add(ScannerSupport.Create($"{prefix}:{entry.KeyName}", entry.DisplayName, Source, entry.InstallLocation, null));
        }
        return games;
    }

    protected bool IsExcludedName(string displayName)
        => ExcludedNames.Any(n => string.Equals(n, displayName.Trim(), StringComparison.OrdinalIgnoreCase))
            || ExcludedNameFragments.Any(f => displayName.Contains(f, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Jeux EA (application EA / Origin) via les clés de désinstallation « Electronic Arts ».</summary>
public sealed class EaLibraryScanner(IRegistryProvider registry, IFileSystemProvider fileSystem) : PublisherUninstallScanner(registry, fileSystem)
{
    public override GameSource Source => GameSource.EA;

    protected override IReadOnlyList<string> Publishers { get; } = ["Electronic Arts"];

    protected override IReadOnlyList<string> ExcludedNames { get; } = ["EA app", "EA", "Origin", "EA Desktop", "EA Desktop App"];
}

/// <summary>Jeux Battle.net via les clés de désinstallation « Blizzard Entertainment ».</summary>
public sealed class BattleNetLibraryScanner(IRegistryProvider registry, IFileSystemProvider fileSystem) : PublisherUninstallScanner(registry, fileSystem)
{
    public override GameSource Source => GameSource.BattleNet;

    protected override IReadOnlyList<string> Publishers { get; } = ["Blizzard Entertainment"];

    protected override IReadOnlyList<string> ExcludedNames { get; } = ["Battle.net", "Blizzard Battle.net", "Blizzard App"];
}

/// <summary>
/// Jeux Riot : clés de désinstallation « Riot Games, Inc » et métadonnées
/// <c>&lt;ProgramData&gt;\Riot Games\Metadata\*\*.product_settings.yaml</c> (ligne <c>product_install_full_path:</c>).
/// Le client Riot et Vanguard sont exclus.
/// </summary>
public sealed class RiotLibraryScanner(IRegistryProvider registry, IFileSystemProvider fileSystem) : PublisherUninstallScanner(registry, fileSystem)
{
    private static readonly Dictionary<string, string> ProductNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["valorant"] = "VALORANT",
        ["league_of_legends"] = "League of Legends",
        ["bacon"] = "Legends of Runeterra",
    };

    public override GameSource Source => GameSource.Riot;

    protected override IReadOnlyList<string> Publishers { get; } = ["Riot Games"];

    protected override IReadOnlyList<string> ExcludedNames { get; } = ["Riot Client", "Riot Vanguard"];

    protected override IReadOnlyList<string> ExcludedNameFragments { get; } = ["Vanguard", "Riot Client", "Uninstall"];

    public override Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken)
    {
        var games = ScanUninstallEntries(cancellationToken).ToList();
        var known = new HashSet<string>(games.Select(g => WinPath.Key(g.InstallDirectory)), StringComparer.Ordinal);
        foreach (var game in ScanProductSettings(cancellationToken))
        {
            if (known.Add(WinPath.Key(game.InstallDirectory))) games.Add(game);
        }
        return Task.FromResult<IReadOnlyList<GameInfo>>(games);
    }

    private IEnumerable<GameInfo> ScanProductSettings(CancellationToken cancellationToken)
    {
        var programData = FileSystem.GetKnownFolder(KnownFolder.ProgramData);
        if (string.IsNullOrEmpty(programData)) yield break;
        var metadata = WinPath.Combine(programData, "Riot Games", "Metadata");
        if (!FileSystem.DirectoryExists(metadata)) yield break;

        foreach (var productDir in FileSystem.EnumerateDirectories(metadata))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = WinPath.GetFileName(productDir);
            var product = folder.Split('.')[0];
            if (product.Length == 0 || product.StartsWith("riot_client", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in FileSystem.EnumerateFiles(productDir, recursive: false, cancellationToken))
            {
                if (!file.Path.EndsWith(".product_settings.yaml", StringComparison.OrdinalIgnoreCase)) continue;
                var installPath = ReadInstallPath(FileSystem.ReadAllText(file.Path));
                if (installPath is null || WinPath.IsDriveRoot(installPath) || !FileSystem.DirectoryExists(installPath)) continue;
                var name = ProductNames.TryGetValue(product, out var known) ? known : ScannerSupport.DeriveNameFromDirectory(installPath);
                yield return ScannerSupport.Create($"riot:{folder.ToLowerInvariant()}", name, GameSource.Riot, installPath, null);
                break;
            }
        }
    }

    /// <summary>Valeur de la ligne <c>product_install_full_path:</c> (guillemets retirés).</summary>
    internal static string? ReadInstallPath(string? yaml)
    {
        if (string.IsNullOrEmpty(yaml)) return null;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.Trim();
            const string key = "product_install_full_path:";
            if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[key.Length..].Trim().Trim('"', '\'');
            return value.Length == 0 ? null : WinPath.Normalize(value);
        }
        return null;
    }
}
