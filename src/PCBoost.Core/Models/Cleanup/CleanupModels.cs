using PCBoost.Core.Common;

namespace PCBoost.Core.Models.Cleanup;

/// <summary>Catégorie de nettoyage (fichiers temporaires, corbeille, caches…).</summary>
public sealed record CleanupCategory(
    string Id,
    TextRef Name,
    TextRef Description,
    SafetyCategory Safety,
    bool RequiresElevation,
    bool SelectedByDefault,
    // Âge minimal des fichiers supprimés (évite de toucher aux fichiers en cours d’utilisation).
    TimeSpan MinimumFileAge);

public sealed record CleanupScanResult(
    string CategoryId,
    long Bytes,
    int FileCount,
    bool Available,
    TextRef? UnavailableReason,
    bool RequiresElevation);

public sealed record CleanupItemError(string Path, OperationErrorKind Error);

public sealed record CleanupExecutionResult(
    string CategoryId,
    long BytesFreed,
    int FilesDeleted,
    int FilesSkipped,
    OperationResult Outcome);

public sealed record CleanupSummary(
    IReadOnlyList<CleanupExecutionResult> Results)
{
    public long TotalBytesFreed => Results.Sum(r => r.BytesFreed);
    public int TotalFilesDeleted => Results.Sum(r => r.FilesDeleted);
    public int TotalFilesSkipped => Results.Sum(r => r.FilesSkipped);
}
