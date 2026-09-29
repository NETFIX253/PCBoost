using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Files;
using PCBoost.Core.Services;

namespace PCBoost.Optimization.Files;

/// <summary>
/// Gros fichiers et doublons des dossiers personnels. Les doublons sont confirmés en trois étapes (même taille, même
/// début et fin de fichier, puis même empreinte SHA-256 du contenu complet) ; le volume comparé est borné. Les fichiers
/// cachés, système, en ligne (non présents sur le disque) et les liens ne sont ni lus ni proposés.
/// </summary>
public sealed class FileCleanupService : IFileCleanupService
{
    public const long LargeFileThreshold = 256L * 1024 * 1024;
    public const int MaxLargeFiles = 100;
    public const long DuplicateMinimumSize = 1024 * 1024;
    public const int MaxDuplicateGroups = 200;
    public const long MaxComparedBytes = 20L * 1024 * 1024 * 1024;
    internal const int SampleBytes = 64 * 1024;
    internal const int MaxListedFiles = 500_000;
    internal static readonly TimeSpan WriteTimeTolerance = TimeSpan.FromSeconds(2);

    private static readonly KnownFolder[] Folders =
        [KnownFolder.Documents, KnownFolder.Downloads, KnownFolder.Desktop, KnownFolder.Pictures, KnownFolder.Videos, KnownFolder.Music];

    private readonly IFileSystemProvider _files;
    private readonly IActivityJournal _journal;
    private readonly IClock _clock;
    private readonly ILogger<FileCleanupService> _logger;

    public FileCleanupService(IFileSystemProvider files, IActivityJournal journal, IClock clock, ILogger<FileCleanupService>? logger = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? NullLogger<FileCleanupService>.Instance;
    }

    public Task<FileScanResult> ScanAsync(IProgress<FileScanProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(progress, cancellationToken), cancellationToken);

    public async Task<RecycleReport> MoveToRecycleBinAsync(FileScanResult scan, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(paths);
        var report = await Task.Run(() => Recycle(scan, paths, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (report.Moved > 0)
        {
            try
            {
                await _journal.LogAsync(ActivityKind.Cleanup,
                    TextRef.Of("Files_Journal_Recycled", report.Moved, Math.Round(report.MovedBytes / 1024d / 1024d, 1)), null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "Journal : envoi à la Corbeille non inscrit");
            }
        }
        return report;
    }

    // ---- Analyse ----

    private FileScanResult Scan(IProgress<FileScanProgress>? progress, CancellationToken cancellationToken)
    {
        var roots = Roots();
        var entries = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var inaccessible = 0;
        foreach (var root in roots)
        {
            try
            {
                foreach (var entry in _files.EnumerateFiles(root, recursive: true, cancellationToken))
                {
                    if (entry.IsHidden || entry.IsSystem || entry.IsOffline || entry.Size <= 0) continue;
                    entries.TryAdd(entry.Path, entry);
                    if (entries.Count % 2000 == 0) progress?.Report(new FileScanProgress(FileScanStage.Listing, entries.Count, 0, 0));
                    if (entries.Count >= MaxListedFiles) break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                inaccessible++;
                _logger.LogDebug(ex, "Dossier personnel illisible");
            }
        }

        var large = entries.Values.Where(e => e.Size >= LargeFileThreshold)
            .OrderByDescending(e => e.Size).Take(MaxLargeFiles)
            .Select(e => new LargeFile(e.Path, e.Size, e.LastWriteUtc)).ToList();

        var candidates = entries.Values.Where(e => e.Size >= DuplicateMinimumSize)
            .GroupBy(e => e.Size).Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Key).ToList();
        var toCompare = candidates.Sum(g => g.Count());
        var compared = 0;
        long budget = MaxComparedBytes;
        var limited = false;
        var groups = new List<DuplicateGroup>();
        foreach (var sameSize in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (groups.Count >= MaxDuplicateGroups) break;

            // Étape 2 : échantillons du début et de la fin (lecture brève) ; étape 3 : empreinte complète.
            var bySample = new Dictionary<string, List<FileEntry>>(StringComparer.Ordinal);
            foreach (var file in sameSize)
            {
                compared++;
                if (Sample(file.Path, file.Size, cancellationToken) is { } sample)
                {
                    if (!bySample.TryGetValue(sample, out var list)) bySample[sample] = list = [];
                    list.Add(file);
                }
            }
            progress?.Report(new FileScanProgress(FileScanStage.Comparing, entries.Count, compared, toCompare));

            foreach (var similar in bySample.Values.Where(l => l.Count > 1))
            {
                if (budget < similar.Count * sameSize.Key)
                {
                    limited = true;
                    continue;
                }
                var byHash = new Dictionary<string, List<FileEntry>>(StringComparer.Ordinal);
                foreach (var file in similar)
                {
                    budget -= file.Size;
                    if (FullHash(file.Path, cancellationToken) is not { } hash) continue;
                    if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = [];
                    list.Add(file);
                }
                foreach (var (hash, same) in byHash.Where(p => p.Value.Count > 1))
                {
                    groups.Add(new DuplicateGroup(hash, sameSize.Key,
                        same.OrderBy(f => f.LastWriteUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                            .Select(f => new DuplicateFile(f.Path, f.LastWriteUtc)).ToList()));
                }
            }
            if (budget <= 0)
            {
                limited = true;
                break;
            }
        }

        progress?.Report(new FileScanProgress(FileScanStage.Done, entries.Count, compared, toCompare));
        return new FileScanResult(_clock.UtcNow, roots, large, groups.OrderByDescending(g => g.RecoverableBytes).ToList(), entries.Count, inaccessible, limited);
    }

    /// <summary>Dossiers personnels existants, sans doublon ni dossier inclus dans un autre.</summary>
    private List<string> Roots()
    {
        var roots = Folders.Select(f => _files.GetKnownFolder(f)).OfType<string>()
            .Select(p => p.TrimEnd('\\'))
            .Where(p => p.Length > 3 && _files.DirectoryExists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return roots.Where(r => !roots.Any(o => !ReferenceEquals(o, r) && !o.Equals(r, StringComparison.OrdinalIgnoreCase) && IsUnder(r, o))).ToList();
    }

    private string? Sample(string path, long size, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = _files.OpenRead(path);
            if (stream is null) return null;
            var buffer = new byte[SampleBytes * 2];
            var head = ReadFully(stream, buffer.AsSpan(0, SampleBytes));
            var tail = 0;
            var length = stream.CanSeek ? stream.Length : size;
            if (length > SampleBytes * 2 && stream.CanSeek)
            {
                stream.Seek(-SampleBytes, SeekOrigin.End);
                tail = ReadFully(stream, buffer.AsSpan(SampleBytes, SampleBytes));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, SampleBytes + tail)[..(head + tail)]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private string? FullHash(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = _files.OpenRead(path);
            if (stream is null) return null;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    // ---- Corbeille ----

    private RecycleReport Recycle(FileScanResult scan, IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        var known = new Dictionary<string, (long Size, DateTimeOffset LastWrite)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in scan.LargeFiles) known[f.Path] = (f.Size, f.LastWriteUtc);
        foreach (var g in scan.DuplicateGroups)
            foreach (var f in g.Files) known[f.Path] = (g.Size, f.LastWriteUtc);

        var requested = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var failures = new List<(string, OperationResult)>();

        // Une copie de chaque groupe de doublons est toujours conservée, et elle doit encore exister.
        foreach (var group in scan.DuplicateGroups)
        {
            var selected = group.Files.Where(f => requested.Contains(f.Path)).ToList();
            if (selected.Count == 0) continue;
            var kept = group.Files.Where(f => !requested.Contains(f.Path)).ToList();
            if (kept.Count == 0)
            {
                requested.Remove(group.Files[0].Path);
                failures.Add((group.Files[0].Path, OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Files_KeptCopy"))));
                kept = [group.Files[0]];
            }
            if (!kept.Any(k => Unchanged(k.Path, group.Size, k.LastWriteUtc)))
            {
                foreach (var f in selected.Where(s => requested.Remove(s.Path)))
                    failures.Add((f.Path, OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Files_NoCopyLeft"))));
            }
        }

        var moved = 0;
        long bytes = 0;
        foreach (var path in requested)
        {
            if (cancellationToken.IsCancellationRequested) return new RecycleReport(moved, bytes, failures, Cancelled: true);
            if (!known.TryGetValue(path, out var expected) || !IsUnderAny(path, scan.Roots))
            {
                failures.Add((path, OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Files_NotInScan"))));
                continue;
            }
            if (!Unchanged(path, expected.Size, expected.LastWrite))
            {
                failures.Add((path, OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Files_Changed"))));
                continue;
            }

            var result = _files.MoveToRecycleBin(path);
            if (result.Success)
            {
                moved++;
                bytes += expected.Size;
            }
            else if (result.Error == OperationErrorKind.Cancelled)
            {
                // L'utilisateur a refusé une suppression définitive proposée par Windows : on s'arrête là.
                return new RecycleReport(moved, bytes, failures, Cancelled: true);
            }
            else
            {
                failures.Add((path, result));
            }
        }
        _logger.LogInformation("Corbeille : {Moved} fichier(s) envoyé(s), {Failed} non traité(s)", moved, failures.Count);
        return new RecycleReport(moved, bytes, failures, Cancelled: false);
    }

    private bool Unchanged(string path, long size, DateTimeOffset lastWrite)
        => _files.GetFileInfo(path) is { } info && info.Size == size && (info.LastWriteUtc - lastWrite).Duration() <= WriteTimeTolerance;

    private static bool IsUnderAny(string path, IEnumerable<string> roots) => roots.Any(r => IsUnder(path, r));

    private static bool IsUnder(string path, string root)
        => path.Length > root.Length + 1 && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
           && !path.Contains(@"\..\", StringComparison.Ordinal);
}
