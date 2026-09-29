using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Programs;
using PCBoost.Core.Programs;
using PCBoost.Core.Services;

namespace PCBoost.Optimization.Programs;

/// <summary>
/// Inventaire des programmes (clés de désinstallation 64 bits, 32 bits et utilisateur) et désinstallation assistée.
/// La dernière utilisation provient des exécutions enregistrées par Windows quand elles ont été lues, sinon du dernier
/// accès aux fichiers programmes (valeur approximative, qui ne peut que surestimer l'usage récent : un programme n'est
/// donc jamais signalé « peu utilisé » à tort pour cette raison).
/// </summary>
public sealed class ProgramInventoryService : IProgramInventoryService
{
    internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string LastRunKey = "programs.lastrun";
    internal const int MaxExecutablesPerProgram = 16;
    internal static readonly TimeSpan RemovalPollInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan RemovalWaitLimit = TimeSpan.FromMinutes(10);

    private static readonly (RegistryHiveKind Hive, RegistryViewKind View, ProgramScope Scope)[] Views =
    [
        (RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, ProgramScope.Machine),
        (RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, ProgramScope.Machine),
        (RegistryHiveKind.CurrentUser, RegistryViewKind.Default, ProgramScope.User),
    ];

    private readonly IRegistryProvider _registry;
    private readonly IFileSystemProvider _files;
    private readonly IUninstallerLauncher _launcher;
    private readonly IElevationService _elevation;
    private readonly IKeyValueStore _store;
    private readonly IActivityJournal _journal;
    private readonly IAppInfo _app;
    private readonly IClock _clock;
    private readonly ILogger<ProgramInventoryService> _logger;

    public ProgramInventoryService(IRegistryProvider registry, IFileSystemProvider files, IUninstallerLauncher launcher, IElevationService elevation,
        IKeyValueStore store, IActivityJournal journal, IAppInfo app, IClock clock, ILogger<ProgramInventoryService>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? NullLogger<ProgramInventoryService>.Instance;
    }

    /// <summary>Dernières exécutions lues avec autorisation (nom d'exécutable en minuscules → date).</summary>
    public sealed record LastRunCache(DateTimeOffset ReadAt, Dictionary<string, DateTimeOffset> Entries);

    public async Task<ProgramInventory> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        var lastRun = await LoadLastRunAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Build(lastRun, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> ReadLastRunAsync(CancellationToken cancellationToken = default)
    {
        var response = await _elevation.RunAsync(new ElevatedRequest(ElevatedHealthOperations.AppsLastRun, new Dictionary<string, string>()), cancellationToken)
            .ConfigureAwait(false);
        if (!response.Outcome.Success) return response.Outcome;
        var entries = HealthElevatedData.DecodeLastRun(response.Data);
        await _store.SetAsync(LastRunKey, new LastRunCache(_clock.UtcNow, new Dictionary<string, DateTimeOffset>(entries, StringComparer.OrdinalIgnoreCase)), cancellationToken)
            .ConfigureAwait(false);
        return entries.Count > 0 ? OperationResult.Ok() : OperationResult.Ok(TextRef.Of("Programs_LastRun_None"));
    }

    public async Task<UninstallResult> UninstallAsync(InstalledProgram program, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (program.Uninstall is null) return new UninstallResult(UninstallOutcome.Failed, OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Programs_Uninstall_UseSettings")));

        // Relecture de la clé : la commande exécutée est celle que Windows déclare au moment du clic, identique à l'aperçu.
        if (ParseId(program.Id) is not { } key || !_registry.KeyExists(key)) return new UninstallResult(UninstallOutcome.Removed);
        var fresh = UninstallCommandParser.Parse(ReadString(key, "UninstallString"));
        if (fresh is null || fresh != program.Uninstall)
            return new UninstallResult(UninstallOutcome.Failed, OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Programs_Uninstall_Changed")));

        await LogAsync(ActivityKind.Cleanup, TextRef.Of("Programs_Journal_Started", program.Name), cancellationToken).ConfigureAwait(false);
        var run = await _launcher.RunAsync(fresh, cancellationToken).ConfigureAwait(false);
        if (!run.Success) return new UninstallResult(UninstallOutcome.Failed, run.ToResult());

        // Beaucoup de programmes de désinstallation se relancent depuis un dossier temporaire : on attend la disparition
        // de la clé (l'utilisateur peut arrêter d'attendre ; le programme de désinstallation n'est jamais interrompu).
        var deadline = _clock.UtcNow + RemovalWaitLimit;
        while (_registry.KeyExists(key))
        {
            if (_clock.UtcNow >= deadline) return new UninstallResult(UninstallOutcome.StillInstalled);
            try
            {
                await Task.Delay(RemovalPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new UninstallResult(UninstallOutcome.StillInstalled);
            }
        }

        _logger.LogInformation("Programme désinstallé par son programme officiel");
        await LogAsync(ActivityKind.Cleanup, TextRef.Of("Programs_Journal_Removed", program.Name), CancellationToken.None).ConfigureAwait(false);
        return new UninstallResult(UninstallOutcome.Removed);
    }

    private ProgramInventory Build(LastRunCache? lastRun, CancellationToken cancellationToken)
    {
        var windows = _files.GetKnownFolder(KnownFolder.WindowsDirectory);
        string?[] roots =
        [
            _files.GetKnownFolder(KnownFolder.ProgramFiles), _files.GetKnownFolder(KnownFolder.ProgramFilesX86), _files.GetKnownFolder(KnownFolder.UserProfile),
            _files.GetKnownFolder(KnownFolder.LocalAppData), _files.GetKnownFolder(KnownFolder.RoamingAppData), _files.GetKnownFolder(KnownFolder.ProgramData),
        ];

        var programs = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hidden = 0;
        foreach (var entry in ReadEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ProgramClassifier.IsCandidate(entry)) continue;
            if (ProgramClassifier.IsProtected(entry, _app.ProductName))
            {
                hidden++;
                continue;
            }
            var name = entry.DisplayName!.Trim();
            if (!seen.Add($"{name}\u0001{entry.DisplayVersion?.Trim()}")) continue;

            var location = ProgramClassifier.IsUsableInstallLocation(entry.InstallLocation, windows, roots) ? entry.InstallLocation!.Trim().Trim('"').TrimEnd('\\') : null;
            var executables = Executables(entry, location, cancellationToken);
            var (lastUsed, source) = LastUse(executables, lastRun);
            var (size, fromRegistry) = Size(entry, location, cancellationToken);
            programs.Add(new InstalledProgram(
                Id(entry.Key),
                name,
                Clean(entry.Publisher),
                Clean(entry.DisplayVersion),
                ProgramClassifier.ParseInstallDate(entry.InstallDate),
                size,
                fromRegistry,
                location,
                entry.Key.Hive == RegistryHiveKind.CurrentUser ? ProgramScope.User : ProgramScope.Machine,
                executables.FirstOrDefault(),
                lastUsed,
                source,
                UninstallCommandParser.Parse(entry.UninstallString)));
        }

        return new ProgramInventory(_clock.UtcNow, programs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), hidden, lastRun?.ReadAt);
    }

    private IEnumerable<UninstallRegistryEntry> ReadEntries()
    {
        foreach (var (hive, view, _) in Views)
        {
            var root = new RegistryLocation(hive, UninstallKey, view);
            IReadOnlyList<string> names;
            try
            {
                names = _registry.GetSubKeyNames(root);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "Clés de désinstallation illisibles ({Hive}, {View})", hive, view);
                continue;
            }
            foreach (var name in names)
            {
                var key = root with { KeyPath = $@"{UninstallKey}\{name}" };
                yield return new UninstallRegistryEntry(
                    key,
                    ReadString(key, "DisplayName"),
                    ReadString(key, "Publisher"),
                    ReadString(key, "DisplayVersion"),
                    ReadString(key, "InstallDate"),
                    ReadInt(key, "EstimatedSize"),
                    ReadString(key, "InstallLocation"),
                    ReadString(key, "DisplayIcon"),
                    ReadString(key, "UninstallString"),
                    ReadInt(key, "SystemComponent"),
                    ReadString(key, "ParentKeyName"),
                    ReadString(key, "ReleaseType"),
                    ReadInt(key, "NoRemove"));
            }
        }
    }

    /// <summary>Exécutable de l'icône en premier, puis exécutables du dossier d'installation (hors programmes d'installation et utilitaires).</summary>
    private List<string> Executables(UninstallRegistryEntry entry, string? location, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        if (ProgramClassifier.ExecutableFromIcon(entry.DisplayIcon) is { } icon && !ProgramClassifier.IsHelperExecutable(FileName(icon)) && _files.FileExists(icon))
            result.Add(icon);
        if (location is null || !_files.DirectoryExists(location)) return result;
        try
        {
            foreach (var file in _files.EnumerateFiles(location, recursive: false, cancellationToken)
                         .Where(f => f.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !ProgramClassifier.IsHelperExecutable(FileName(f.Path)))
                         .OrderByDescending(f => f.Size)
                         .Take(MaxExecutablesPerProgram))
            {
                if (!result.Contains(file.Path, StringComparer.OrdinalIgnoreCase)) result.Add(file.Path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Dossier d'installation illisible");
        }
        return result;
    }

    private (DateTimeOffset? When, LastUseSource Source) LastUse(IReadOnlyList<string> executables, LastRunCache? lastRun)
    {
        if (executables.Count == 0) return (null, LastUseSource.Unknown);
        if (lastRun is not null)
        {
            DateTimeOffset? best = null;
            foreach (var exe in executables)
            {
                if (lastRun.Entries.TryGetValue(FileName(exe), out var t) && (best is null || t > best)) best = t;
            }
            if (best is not null) return (best, LastUseSource.WindowsPrefetch);
        }

        DateTimeOffset? access = null;
        foreach (var exe in executables)
        {
            if (_files.GetLastAccessTimeUtc(exe) is { } t && t <= _clock.UtcNow.AddDays(1) && (access is null || t > access)) access = t;
        }
        return access is null ? (null, LastUseSource.Unknown) : (access, LastUseSource.FileAccess);
    }

    private (long? Bytes, bool FromRegistry) Size(UninstallRegistryEntry entry, string? location, CancellationToken cancellationToken)
    {
        if (entry.EstimatedSizeKb is > 0 and < int.MaxValue) return (entry.EstimatedSizeKb.Value * 1024L, true);
        if (location is null || !_files.DirectoryExists(location)) return (null, false);
        try
        {
            var size = _files.GetDirectorySize(location, cancellationToken);
            return size.FileCount > 0 ? (size.Bytes, false) : (null, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Taille du dossier d'installation non mesurée");
            return (null, false);
        }
    }

    private async Task<LastRunCache?> LoadLastRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cache = await _store.GetAsync<LastRunCache>(LastRunKey, cancellationToken).ConfigureAwait(false);
            return cache is null ? null : cache with { Entries = new Dictionary<string, DateTimeOffset>(cache.Entries, StringComparer.OrdinalIgnoreCase) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Dernières exécutions enregistrées illisibles");
            return null;
        }
    }

    private async Task LogAsync(ActivityKind kind, TextRef message, CancellationToken cancellationToken)
    {
        try
        {
            await _journal.LogAsync(kind, message, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Journal : désinstallation non inscrite");
        }
    }

    private string? ReadString(RegistryLocation key, string name)
        => _registry.GetValue(key, name) is { Value: string s } && !string.IsNullOrWhiteSpace(s) ? s : null;

    private int? ReadInt(RegistryLocation key, string name) => _registry.GetValue(key, name)?.Value switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) => v,
        _ => null,
    };

    internal static string Id(RegistryLocation key) => $"{key.Hive}|{key.View}|{key.KeyPath[(UninstallKey.Length + 1)..]}";

    internal static RegistryLocation? ParseId(string id)
    {
        var parts = id.Split('|', 3);
        if (parts.Length != 3 || parts[2].Length == 0 || parts[2].Contains('\\', StringComparison.Ordinal)) return null;
        if (!Enum.TryParse<RegistryHiveKind>(parts[0], out var hive) || !Enum.IsDefined(hive)) return null;
        if (!Enum.TryParse<RegistryViewKind>(parts[1], out var view) || !Enum.IsDefined(view)) return null;
        return new RegistryLocation(hive, $@"{UninstallKey}\{parts[2]}", view);
    }

    private static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..].ToLowerInvariant();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
