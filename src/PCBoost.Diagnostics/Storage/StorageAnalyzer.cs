using Microsoft.Extensions.Logging;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;

namespace PCBoost.Diagnostics.Storage;

/// <summary>
/// Répartition de l'espace du disque système (§25). Strictement en lecture seule : aucune suppression, aucune écriture.
/// <list type="bullet">
/// <item>Dossiers de l'utilisateur et Temporaire : mesurés seulement s'ils sont sur le disque système (sinon omis).</item>
/// <item>Applications : Program Files (+ x86) avec un budget de temps ; <c>Measured=false</c> si dépassé ou accès refusé.</item>
/// <item>Système : jamais mesuré (<c>Measured=false</c>), compris dans « Autre ».</item>
/// <item>Autre = espace utilisé − somme des catégories mesurées (jamais négatif).</item>
/// </list>
/// </summary>
public sealed class StorageAnalyzer : IStorageAnalyzer
{
    private static readonly (KnownFolder Folder, StorageCategoryKind Kind)[] UserFolders =
    [
        (KnownFolder.Documents, StorageCategoryKind.Documents),
        (KnownFolder.Downloads, StorageCategoryKind.Downloads),
        (KnownFolder.Desktop, StorageCategoryKind.Desktop),
        (KnownFolder.Pictures, StorageCategoryKind.Pictures),
        (KnownFolder.Videos, StorageCategoryKind.Videos),
        (KnownFolder.Music, StorageCategoryKind.Music),
        (KnownFolder.UserTemp, StorageCategoryKind.Temporary),
    ];

    private readonly ISystemInfoProvider _systemInfo;
    private readonly IFileSystemProvider _fileSystem;
    private readonly IClock _clock;
    private readonly ILogger<StorageAnalyzer> _logger;
    private readonly StorageAnalyzerOptions _options;

    public StorageAnalyzer(ISystemInfoProvider systemInfo, IFileSystemProvider fileSystem, IClock clock, ILogger<StorageAnalyzer> logger,
        StorageAnalyzerOptions? options = null)
    {
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new StorageAnalyzerOptions();
    }

    public Task<StorageBreakdown?> AnalyzeSystemDriveAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Analyze(cancellationToken), cancellationToken);

    private StorageBreakdown? Analyze(CancellationToken cancellationToken)
    {
        StorageDrive? drive;
        try
        {
            drive = _systemInfo.GetDrives().FirstOrDefault(d => d.IsSystemDrive);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Lecteurs illisibles : {ErrorType} {Message}", ex.GetType().Name, PrivacyRedactor.Redact(ex.Message));
            return null;
        }
        if (drive is null || drive.TotalBytes <= 0) return null;

        var profile = SafeKnownFolder(KnownFolder.UserProfile);
        var categories = new List<StorageCategoryUsage>();
        var measuredSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var (folder, kind) in UserFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = SafeKnownFolder(folder);
            if (path is null || !IsOnDrive(path, drive.RootPath)) continue;
            var size = Measure(path, cancellationToken, profile);
            categories.Add(new StorageCategoryUsage(kind, size ?? 0, size.HasValue, path));
            if (size is long bytes) measuredSizes[Normalize(path)] = bytes;
        }

        categories.Add(MeasureApplications(drive, cancellationToken, profile));

        var windows = SafeKnownFolder(KnownFolder.WindowsDirectory);
        categories.Add(new StorageCategoryUsage(StorageCategoryKind.System, 0, Measured: false, windows is not null && IsOnDrive(windows, drive.RootPath) ? windows : null));

        var measuredTotal = categories.Where(c => c.Measured).Sum(c => c.Bytes);
        categories.Add(new StorageCategoryUsage(StorageCategoryKind.Other, Math.Max(0, drive.UsedBytes - measuredTotal), Measured: true, null));

        var largest = profile is not null && IsOnDrive(profile, drive.RootPath)
            ? LargestProfileFolders(profile, measuredSizes, cancellationToken)
            : [];

        return new StorageBreakdown(drive, categories, largest, _clock.UtcNow);
    }

    private StorageCategoryUsage MeasureApplications(StorageDrive drive, CancellationToken cancellationToken, string? profile)
    {
        var roots = new[] { SafeKnownFolder(KnownFolder.ProgramFiles), SafeKnownFolder(KnownFolder.ProgramFilesX86) }
            .Where(p => p is not null && IsOnDrive(p, drive.RootPath))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0) return new StorageCategoryUsage(StorageCategoryKind.Applications, 0, Measured: false, null);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.ApplicationsTimeBudget);
        long total = 0;
        foreach (var root in roots)
        {
            var size = Measure(root, budget.Token, profile, rethrowOuterCancellation: cancellationToken);
            if (size is null)
            {
                _logger.LogInformation("Taille des applications non mesurée (budget de temps dépassé ou accès refusé).");
                return new StorageCategoryUsage(StorageCategoryKind.Applications, 0, Measured: false, roots[0]);
            }
            total += size.Value;
        }
        return new StorageCategoryUsage(StorageCategoryKind.Applications, total, Measured: true, roots[0]);
    }

    private IReadOnlyList<LargeFolder> LargestProfileFolders(string profile, Dictionary<string, long> alreadyMeasured, CancellationToken cancellationToken)
    {
        IEnumerable<string> directories;
        try
        {
            directories = _fileSystem.EnumerateDirectories(profile).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Profil utilisateur illisible : {ErrorType} {Message}", ex.GetType().Name, PrivacyRedactor.Redact(ex.Message, profile));
            return [];
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.LargestFoldersTimeBudget);
        var sizes = new List<LargeFolder>();
        foreach (var directory in directories)
        {
            if (budget.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Liste des plus gros dossiers partielle : budget de temps dépassé.");
                break;
            }
            if (alreadyMeasured.TryGetValue(Normalize(directory), out var known))
            {
                sizes.Add(new LargeFolder(directory, known));
                continue;
            }
            if (Measure(directory, budget.Token, profile, rethrowOuterCancellation: cancellationToken) is long bytes)
                sizes.Add(new LargeFolder(directory, bytes));
        }

        return sizes.Where(f => f.Bytes > 0)
            .OrderByDescending(f => f.Bytes)
            .Take(Math.Max(0, _options.LargestFolderCount))
            .ToList();
    }

    /// <summary>Taille d'un dossier ; null si inaccessible ou interrompue par le budget. L'annulation de l'appelant est propagée.</summary>
    private long? Measure(string path, CancellationToken token, string? profile, CancellationToken? rethrowOuterCancellation = null)
    {
        var outer = rethrowOuterCancellation ?? token;
        try
        {
            if (!_fileSystem.DirectoryExists(path)) return 0;
            return Math.Max(0, _fileSystem.GetDirectorySize(path, token).Bytes);
        }
        catch (OperationCanceledException) when (outer.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Dossier non mesuré ({Path}) : {ErrorType}", PrivacyRedactor.Redact(path, profile), ex.GetType().Name);
            return null;
        }
    }

    private string? SafeKnownFolder(KnownFolder folder)
    {
        try
        {
            var path = _fileSystem.GetKnownFolder(folder);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Dossier connu {Folder} illisible : {ErrorType}", folder, ex.GetType().Name);
            return null;
        }
    }

    internal static bool IsOnDrive(string path, string driveRoot)
    {
        var root = Normalize(driveRoot);
        var p = Normalize(path);
        return p.Equals(root, StringComparison.OrdinalIgnoreCase) || p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
}
