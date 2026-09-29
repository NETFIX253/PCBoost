using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;
using PCBoost.Gaming.Common;
using PCBoost.Gaming.Detection.Scanners;

namespace PCBoost.Gaming.Detection;

/// <summary>
/// Détection des jeux installés (bibliothèque en cache, rafraîchie sur demande ou toutes les heures) et du jeu en cours.
/// La surveillance n'utilise que l'énumération légère des processus (PID + nom) ; le chemin d'un exécutable n'est lu
/// qu'une fois par processus (cache par PID) et seulement si un dossier d'installation connu peut correspondre.
/// </summary>
public sealed class GameDetectionService : IGameDetectionService
{
    private readonly IReadOnlyList<IGameLibraryScanner> _scanners;
    private readonly GameSignatureDatabase _signatures;
    private readonly IProcessProvider _processes;
    private readonly ICriticalProcessProtection _protection;
    private readonly IClock _clock;
    private readonly GamingOptions _options;
    private readonly InstallDirectoryGuard _guard;
    private readonly string? _windowsDirectory;
    private readonly ILogger<GameDetectionService> _logger;

    private readonly SemaphoreSlim _libraryGate = new(1, 1);
    private readonly Lock _sync = new();
    private readonly Dictionary<int, CachedPath> _pathCache = new();
    private readonly Dictionary<string, TrackedGame> _tracked = new(StringComparer.Ordinal);
    private LibrarySnapshot? _library;
    private CancellationTokenSource? _watchCts;
    private Task? _watchTask;
    private bool _disposed;

    public GameDetectionService(
        IEnumerable<IGameLibraryScanner> scanners,
        GameSignatureDatabase signatures,
        IProcessProvider processes,
        ICriticalProcessProtection protection,
        IFileSystemProvider fileSystem,
        IClock clock,
        GamingOptions? options = null,
        ILogger<GameDetectionService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scanners);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _scanners = scanners.ToList();
        _signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new GamingOptions();
        _guard = new InstallDirectoryGuard(fileSystem);
        _windowsDirectory = fileSystem.GetKnownFolder(KnownFolder.WindowsDirectory);
        _logger = logger ?? NullLogger<GameDetectionService>.Instance;
    }

    public event EventHandler<DetectedGameProcess>? GameStarted;

    public event EventHandler<DetectedGameProcess>? GameExited;

    public bool IsWatching
    {
        get { lock (_sync) return _watchTask is not null; }
    }

    public async Task<IReadOnlyList<GameInfo>> GetInstalledGamesAsync(bool refresh = false, CancellationToken cancellationToken = default)
        => (await GetLibraryAsync(refresh, cancellationToken).ConfigureAwait(false)).Games;

    public async Task<DetectedGameProcess?> DetectRunningGameAsync(CancellationToken cancellationToken = default)
    {
        var result = await DetectAllAsync(cancellationToken).ConfigureAwait(false);
        return result.Games.OrderByDescending(g => g.WorkingSet).Select(g => g.Process).FirstOrDefault();
    }

    public void StartWatching()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watchTask is not null) return;
            _watchCts = new CancellationTokenSource();
            var token = _watchCts.Token;
            _watchTask = Task.Run(() => WatchLoopAsync(token), CancellationToken.None);
        }
        _logger.LogInformation("Surveillance des jeux démarrée (intervalle {Interval} s)", _options.WatchInterval.TotalSeconds);
    }

    public void StopWatching()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _watchCts;
            _watchCts = null;
            _watchTask = null;
            _tracked.Clear();
        }
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
        _logger.LogInformation("Surveillance des jeux arrêtée");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        StopWatching();
    }

    private async Task WatchLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.WatchInterval);
            do
            {
                try
                {
                    await PollAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "Détection des jeux : cycle en erreur");
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé.
        }
    }

    /// <summary>Un cycle de surveillance : un seul <see cref="GameStarted"/> par démarrage, un <see cref="GameExited"/> à la sortie.</summary>
    internal async Task PollAsync(CancellationToken cancellationToken)
    {
        var result = await DetectAllAsync(cancellationToken).ConfigureAwait(false);
        var alive = new Dictionary<int, string>();
        foreach (var identity in result.Identities) alive[identity.ProcessId] = identity.Name;

        var started = new List<DetectedGameProcess>();
        var exited = new List<DetectedGameProcess>();
        lock (_sync)
        {
            foreach (var (gameId, tracked) in _tracked.ToList())
            {
                if (alive.TryGetValue(tracked.ProcessId, out var name) && string.Equals(name, tracked.ProcessName, StringComparison.OrdinalIgnoreCase))
                    continue;
                _tracked.Remove(gameId);
                exited.Add(tracked.Detected);
            }

            var trackedPids = _tracked.Values.Select(t => t.ProcessId).ToHashSet();
            foreach (var candidate in result.Games)
            {
                var process = candidate.Process;
                if (_tracked.ContainsKey(process.Game.Id) || trackedPids.Contains(process.ProcessId)) continue;
                _tracked[process.Game.Id] = new TrackedGame(process.ProcessId, candidate.ProcessName, process);
                trackedPids.Add(process.ProcessId);
                started.Add(process);
            }
        }

        foreach (var game in exited)
        {
            _logger.LogInformation("Jeu fermé : {Game} (PID {ProcessId})", game.Game.Name, game.ProcessId);
            Raise(GameExited, game);
        }
        foreach (var game in started)
        {
            _logger.LogInformation("Jeu détecté : {Game} (PID {ProcessId}, source {Source})", game.Game.Name, game.ProcessId, game.Game.Source);
            Raise(GameStarted, game);
        }
    }

    private void Raise(EventHandler<DetectedGameProcess>? handler, DetectedGameProcess game)
    {
        if (handler is null) return;
        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<DetectedGameProcess>>())
        {
            try
            {
                subscriber(this, game);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Abonné aux événements de détection de jeu en erreur");
            }
        }
    }

    internal async Task<DetectionResult> DetectAllAsync(CancellationToken cancellationToken)
    {
        var library = await GetLibraryAsync(false, cancellationToken).ConfigureAwait(false);
        var identities = _processes.GetProcessIdentities();
        var candidates = new List<Candidate>();

        lock (_sync)
        {
            var alive = identities.ToDictionary(i => i.ProcessId, i => i.Name);
            foreach (var pid in _pathCache.Keys.ToList())
            {
                if (!alive.TryGetValue(pid, out var name) || !string.Equals(name, _pathCache[pid].Name, StringComparison.OrdinalIgnoreCase))
                    _pathCache.Remove(pid);
            }
        }

        foreach (var identity in identities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identity.ProcessId <= 4 || identity.ProcessId == _processes.CurrentProcessId) continue;
            var name = GameSignatureDatabase.Normalize(identity.Name);
            if (name.Length == 0 || _signatures.IsExcluded(name) || _protection.IsCritical(name)) continue;

            GameInfo? game = null;
            string? path = null;
            if (library.ByName.TryGetValue(name, out var byName))
            {
                game = byName;
            }
            else if (_signatures.TryGetGameName(name, out var display))
            {
                game = ScannerSupport.Create($"sig:{name}", display, GameSource.Signature, null, null, [name]);
            }

            if ((game is null || game.Source == GameSource.Signature) && library.Directories.Count > 0)
            {
                path = GetExecutablePathCached(identity);
                var byPath = path is null ? null : library.FindByPath(path);
                if (byPath is not null) game = byPath;
            }

            if (game is null) continue;
            path ??= GetExecutablePathCached(identity);
            if (path is not null && _windowsDirectory is not null && WinPath.IsUnder(path, _windowsDirectory)) continue;
            if (game.Source == GameSource.Signature && path is not null) game = game with { ExecutablePath = path };
            candidates.Add(new Candidate(game, identity.ProcessId, identity.Name, path));
        }

        var games = new List<DetectedCandidate>();
        var groups = candidates.GroupBy(c => c.Game.Id, StringComparer.Ordinal).ToList();
        // L'instantané complet d'un processus (mémoire) n'est lu que s'il faut départager plusieurs candidats.
        var needWorkingSet = groups.Count > 1 || groups.Any(g => g.Skip(1).Any());
        foreach (var group in groups)
        {
            if (!needWorkingSet)
            {
                var single = group.First();
                var at = GetTrackedDetectionTime(single.Game.Id, single.ProcessId) ?? _clock.UtcNow;
                games.Add(new DetectedCandidate(new DetectedGameProcess(single.Game, single.ProcessId, single.Path, at), single.ProcessName, 0));
                continue;
            }

            DetectedCandidate? best = null;
            foreach (var c in group)
            {
                var snapshot = _processes.GetProcess(c.ProcessId);
                if (snapshot is null) continue; // terminé entre-temps
                var workingSet = snapshot.WorkingSetBytes;
                if (best is null || workingSet > best.WorkingSet)
                {
                    var detectedAt = GetTrackedDetectionTime(c.Game.Id, c.ProcessId) ?? _clock.UtcNow;
                    best = new DetectedCandidate(
                        new DetectedGameProcess(c.Game, c.ProcessId, c.Path ?? snapshot.ExecutablePath, detectedAt),
                        c.ProcessName,
                        workingSet);
                }
            }
            if (best is not null) games.Add(best);
        }
        return new DetectionResult(games, identities);
    }

    private DateTimeOffset? GetTrackedDetectionTime(string gameId, int processId)
    {
        lock (_sync)
            return _tracked.TryGetValue(gameId, out var t) && t.ProcessId == processId ? t.Detected.DetectedAt : null;
    }

    private string? GetExecutablePathCached(ProcessIdentity identity)
    {
        lock (_sync)
        {
            if (_pathCache.TryGetValue(identity.ProcessId, out var cached) && string.Equals(cached.Name, identity.Name, StringComparison.OrdinalIgnoreCase))
                return cached.Path;
        }
        var path = _processes.GetExecutablePath(identity.ProcessId);
        var normalized = string.IsNullOrWhiteSpace(path) ? null : WinPath.Normalize(path);
        lock (_sync) _pathCache[identity.ProcessId] = new CachedPath(identity.Name, normalized);
        return normalized;
    }

    private async Task<LibrarySnapshot> GetLibraryAsync(bool refresh, CancellationToken cancellationToken)
    {
        var requestedAt = _clock.UtcNow;
        var current = Volatile.Read(ref _library);
        if (!refresh && IsFresh(current)) return current!;

        await _libraryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = _library;
            // Un autre appel vient peut-être de rafraîchir la bibliothèque pendant l'attente.
            if (current is not null && (refresh ? current.RefreshedAfter(requestedAt) : IsFresh(current)))
                return current;

            var scanned = await ScanAllAsync(cancellationToken).ConfigureAwait(false);
            var merged = GameLibraryMerger.Merge(scanned, _guard.IsPlausible);
            current = LibrarySnapshot.Create(merged, _clock.UtcNow, _guard);
            Volatile.Write(ref _library, current);
            _logger.LogInformation("Bibliothèque de jeux : {Count} jeu(x) installé(s)", merged.Count);
            return current;
        }
        finally
        {
            _libraryGate.Release();
        }
    }

    private bool IsFresh(LibrarySnapshot? snapshot)
        => snapshot is not null && _clock.UtcNow - snapshot.Timestamp < _options.LibraryCacheDuration;

    private async Task<List<GameInfo>> ScanAllAsync(CancellationToken cancellationToken)
    {
        var tasks = _scanners.Select(scanner => Task.Run(async () =>
        {
            try
            {
                return await scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Chaque scanner est isolé : une bibliothèque illisible n'empêche pas les autres.
                _logger.LogWarning(ex, "Scanner de jeux {Source} en erreur : ignoré", scanner.Source);
                return (IReadOnlyList<GameInfo>)[];
            }
        }, cancellationToken)).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(r => r).ToList();
    }

    private sealed record CachedPath(string Name, string? Path);

    private sealed record TrackedGame(int ProcessId, string ProcessName, DetectedGameProcess Detected);

    private sealed record Candidate(GameInfo Game, int ProcessId, string ProcessName, string? Path);

    internal sealed record DetectedCandidate(DetectedGameProcess Process, string ProcessName, long WorkingSet);

    internal sealed record DetectionResult(IReadOnlyList<DetectedCandidate> Games, IReadOnlyList<ProcessIdentity> Identities);

    /// <summary>Bibliothèque figée et ses index (noms d'exécutables non ambigus, dossiers d'installation).</summary>
    private sealed class LibrarySnapshot
    {
        private LibrarySnapshot(IReadOnlyList<GameInfo> games, DateTimeOffset timestamp, Dictionary<string, GameInfo> byName, List<(string Directory, GameInfo Game)> directories)
        {
            Games = games;
            Timestamp = timestamp;
            ByName = byName;
            Directories = directories;
        }

        public IReadOnlyList<GameInfo> Games { get; }
        public DateTimeOffset Timestamp { get; }
        public Dictionary<string, GameInfo> ByName { get; }
        public List<(string Directory, GameInfo Game)> Directories { get; }

        public bool RefreshedAfter(DateTimeOffset time) => Timestamp > time;

        public static LibrarySnapshot Create(IReadOnlyList<GameInfo> games, DateTimeOffset timestamp, InstallDirectoryGuard guard)
        {
            var byName = new Dictionary<string, GameInfo>(StringComparer.OrdinalIgnoreCase);
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in games)
            {
                foreach (var name in game.ExecutableNames)
                {
                    if (ambiguous.Contains(name)) continue;
                    if (byName.TryGetValue(name, out var other) && other.Id != game.Id)
                    {
                        // Nom partagé par plusieurs jeux (ex. gamelaunchhelper.exe) : seule la correspondance par chemin tranche.
                        byName.Remove(name);
                        ambiguous.Add(name);
                        continue;
                    }
                    byName[name] = game;
                }
            }
            var directories = games
                .Where(g => g.InstallDirectory is not null && guard.IsPlausible(g.InstallDirectory))
                .Select(g => (WinPath.Normalize(g.InstallDirectory), g))
                .OrderByDescending(d => d.Item1.Length)
                .ToList();
            return new LibrarySnapshot(games, timestamp, byName, directories);
        }

        /// <summary>Jeu dont le dossier d'installation contient ce chemin (le plus spécifique d'abord).</summary>
        public GameInfo? FindByPath(string path)
        {
            foreach (var (directory, game) in Directories)
                if (WinPath.IsUnder(path, directory)) return game;
            return null;
        }
    }
}
