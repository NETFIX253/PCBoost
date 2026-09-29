using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Security;

namespace PCBoost.Core.Cleanup;

public sealed record CleanupScanTotals(long Bytes, int FileCount, int RootsFound);

public sealed record CleanupDeleteTotals(long BytesFreed, int FilesDeleted, int FilesSkipped, int FilesFailed);

/// <summary>
/// Analyse et suppression des fichiers d'une catégorie du catalogue, via <see cref="IFileSystemProvider"/>.
/// Garde-fous : racines issues de dossiers connus uniquement, chemin résolu contenu dans la racine,
/// politique de chemins interdits, âge minimal, fichiers verrouillés ignorés, liens non suivis.
/// </summary>
public sealed class CleanupExecutor
{
    private readonly IFileSystemProvider _fileSystem;
    private readonly IClock _clock;

    public CleanupExecutor(IFileSystemProvider fileSystem, IClock clock)
    {
        _fileSystem = fileSystem;
        _clock = clock;
    }

    /// <summary>Résout les dossiers réels d'une catégorie (segment « * » développé), en vérifiant leur confinement.</summary>
    public IReadOnlyList<(string Directory, CleanupTargetSpec Spec)> ResolveRoots(CleanupCategoryDefinition category)
    {
        var result = new List<(string, CleanupTargetSpec)>();
        foreach (var spec in category.Targets)
        {
            var baseFolder = _fileSystem.GetKnownFolder(spec.Root);
            if (string.IsNullOrWhiteSpace(baseFolder)) continue;
            baseFolder = _fileSystem.GetFullPath(baseFolder).TrimEnd('\\');

            foreach (var candidate in Expand(baseFolder, spec.RelativePath))
            {
                var full = _fileSystem.GetFullPath(candidate).TrimEnd('\\');
                if (!IsContained(full, baseFolder)) continue;
                if (ForbiddenTargetPolicy.IsForbiddenFilePath(full + "\\")) continue;
                if (!_fileSystem.DirectoryExists(full)) continue;
                result.Add((full, spec));
            }
        }
        return result;
    }

    public CleanupScanTotals Scan(CleanupCategoryDefinition category, CancellationToken cancellationToken = default)
    {
        long bytes = 0;
        int files = 0;
        var roots = ResolveRoots(category);
        foreach (var (dir, spec) in roots)
        {
            foreach (var file in EligibleFiles(dir, spec, category.MinimumFileAge, cancellationToken))
            {
                bytes += file.Size;
                files++;
            }
        }
        return new CleanupScanTotals(bytes, files, roots.Count);
    }

    public CleanupDeleteTotals Delete(CleanupCategoryDefinition category, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        long freed = 0;
        int deleted = 0, skipped = 0, failed = 0;
        var roots = ResolveRoots(category);
        for (var i = 0; i < roots.Count; i++)
        {
            var (dir, spec) = roots[i];
            foreach (var file in EligibleFiles(dir, spec, category.MinimumFileAge, cancellationToken).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.IsSystem || file.IsReadOnly)
                {
                    skipped++;
                    continue;
                }
                var r = _fileSystem.DeleteFile(file.Path);
                if (r.Success)
                {
                    freed += file.Size;
                    deleted++;
                }
                else if (r.Error is OperationErrorKind.InUse or OperationErrorKind.AccessDenied or OperationErrorKind.NotFound)
                {
                    skipped++;
                }
                else
                {
                    failed++;
                }
            }
            if (spec.Recursive) _fileSystem.DeleteEmptySubdirectories(dir);
            progress?.Report((i + 1) * 100d / Math.Max(1, roots.Count));
        }
        return new CleanupDeleteTotals(freed, deleted, skipped, failed);
    }

    private IEnumerable<FileEntry> EligibleFiles(string directory, CleanupTargetSpec spec, TimeSpan minimumAge, CancellationToken cancellationToken)
    {
        var cutoff = _clock.UtcNow - minimumAge;
        foreach (var file in _fileSystem.EnumerateFiles(directory, spec.Recursive, cancellationToken))
        {
            if (!IsContained(file.Path, directory)) continue;
            if (minimumAge > TimeSpan.Zero && file.LastWriteUtc > cutoff) continue;
            if (spec.FilePatterns is { Count: > 0 } patterns && !patterns.Any(p => MatchesPattern(FileName(file.Path), p))) continue;
            if (ForbiddenTargetPolicy.IsForbiddenFilePath(file.Path)) continue;
            yield return file;
        }
    }

    private IEnumerable<string> Expand(string baseFolder, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            yield return baseFolder;
            yield break;
        }
        var segments = relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or ".."))
            yield break;

        var starIndex = Array.IndexOf(segments, "*");
        if (starIndex < 0)
        {
            yield return baseFolder + "\\" + string.Join('\\', segments);
            yield break;
        }

        var prefix = baseFolder + (starIndex > 0 ? "\\" + string.Join('\\', segments[..starIndex]) : string.Empty);
        if (!_fileSystem.DirectoryExists(prefix)) yield break;
        var suffix = string.Join('\\', segments[(starIndex + 1)..]);
        foreach (var dir in _fileSystem.EnumerateDirectories(prefix))
        {
            yield return suffix.Length == 0 ? dir : dir.TrimEnd('\\') + "\\" + suffix;
        }
    }

    public static bool IsContained(string path, string root)
    {
        var p = path.Replace('/', '\\');
        var r = root.Replace('/', '\\').TrimEnd('\\') + "\\";
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase) || string.Equals(p.TrimEnd('\\') + "\\", r, StringComparison.OrdinalIgnoreCase);
    }

    private static string FileName(string path)
    {
        var i = path.Replace('/', '\\').LastIndexOf('\\');
        return i >= 0 ? path[(i + 1)..] : path;
    }

    /// <summary>Correspondance simple avec « * » (insensible à la casse).</summary>
    public static bool MatchesPattern(string name, string pattern)
    {
        if (pattern == "*" || pattern == "*.*") return true;
        var parts = pattern.Split('*');
        if (parts.Length == 1) return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
        var position = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;
            var index = name.IndexOf(part, position, StringComparison.OrdinalIgnoreCase);
            if (index < 0 || (i == 0 && index != 0)) return false;
            position = index + part.Length;
        }
        return parts[^1].Length == 0 || name.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase);
    }
}
