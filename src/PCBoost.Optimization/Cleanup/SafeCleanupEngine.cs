using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Cleanup;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Cleanup;

namespace PCBoost.Optimization.Cleanup;

/// <summary>
/// Moteur de nettoyage sûr (§11) sur le catalogue fermé <see cref="CleanupCatalog"/> :
/// catégories non élevées via <see cref="CleanupExecutor"/> ; catégories système en UNE seule demande d'élévation groupée
/// (<see cref="ElevatedOperations.CleanupCategory"/>, identifiants uniquement) ; corbeille via <see cref="IRecycleBinProvider"/>.
/// Les documents, images, vidéos, téléchargements et fichiers inconnus ne figurent dans aucune catégorie.
/// </summary>
public sealed class SafeCleanupEngine
{
    private static readonly IReadOnlyDictionary<string, string> BrowserNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge.exe"] = "Microsoft Edge",
        ["chrome.exe"] = "Google Chrome",
        ["firefox.exe"] = "Mozilla Firefox",
    };

    private readonly CleanupExecutor _executor;
    private readonly IRecycleBinProvider _recycleBin;
    private readonly IProcessProvider _processes;
    private readonly IElevationService _elevation;
    private readonly ILogger<SafeCleanupEngine> _logger;

    public SafeCleanupEngine(IFileSystemProvider fileSystem, IClock clock, IRecycleBinProvider recycleBin, IProcessProvider processes,
        IElevationService elevation, ILogger<SafeCleanupEngine>? logger = null)
    {
        _executor = new CleanupExecutor(fileSystem, clock);
        _recycleBin = recycleBin;
        _processes = processes;
        _elevation = elevation;
        _logger = logger ?? NullLogger<SafeCleanupEngine>.Instance;
    }

    public IReadOnlyList<CleanupCategory> Categories { get; } = CleanupCatalog.All.Select(c => c.ToCategory()).ToList();

    /// <summary>Définitions correspondant aux identifiants (inconnus ignorés), ou tout le catalogue.</summary>
    public static IReadOnlyList<CleanupCategoryDefinition> Resolve(IReadOnlyCollection<string>? categoryIds)
        => categoryIds is null
            ? CleanupCatalog.All
            : CleanupCatalog.All.Where(c => categoryIds.Contains(c.Id, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Analyse (lecture seule). Les catégories système sont lues partiellement sans élévation.</summary>
    public async Task<IReadOnlyList<CleanupScanResult>> ScanAsync(IEnumerable<CleanupCategoryDefinition> categories, CancellationToken cancellationToken = default)
    {
        var list = categories.ToList();
        var running = RunningProcessNames();
        var results = new List<CleanupScanResult>(list.Count);
        foreach (var category in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await Task.Run(() => ScanOne(category, running, cancellationToken), cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    private CleanupScanResult ScanOne(CleanupCategoryDefinition category, HashSet<string> running, CancellationToken cancellationToken)
    {
        var requiresElevation = category.RequiresElevation && !_elevation.IsElevated;
        try
        {
            if (category.IsRecycleBin)
            {
                var query = _recycleBin.Query();
                return query.Success
                    ? new CleanupScanResult(category.Id, query.Value.Bytes, (int)Math.Min(int.MaxValue, query.Value.Items), true, null, false)
                    : new CleanupScanResult(category.Id, 0, 0, false, TextRef.Of("Opt_Cleanup_RecycleBinUnavailable"), false);
            }

            var totals = _executor.Scan(category, cancellationToken);
            var blocker = FindBlockingProcess(category, running);
            if (blocker is not null)
                return new CleanupScanResult(category.Id, totals.Bytes, totals.FileCount, false, TextRef.Of("Opt_Cleanup_CloseApp", blocker), requiresElevation);

            return new CleanupScanResult(category.Id, totals.Bytes, totals.FileCount, true, null, requiresElevation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Analyse de la catégorie {Category} impossible", category.Id);
            return new CleanupScanResult(category.Id, 0, 0, false, TextRef.Of("Opt_Cleanup_ScanFailed"), requiresElevation);
        }
    }

    /// <summary>
    /// Supprime le contenu des catégories (irréversible ; confirmation obtenue par l'appelant).
    /// Ordre : catégories utilisateur, catégories système (une seule invite UAC), corbeille.
    /// En cas d'annulation, les catégories restantes sont renvoyées « annulées » (jamais d'exception) : ce qui a déjà été
    /// supprimé reste consignable par l'appelant.
    /// </summary>
    public async Task<IReadOnlyList<CleanupExecutionResult>> CleanAsync(IEnumerable<CleanupCategoryDefinition> categories, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var list = categories.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var results = new Dictionary<string, CleanupExecutionResult>(StringComparer.OrdinalIgnoreCase);
        var running = RunningProcessNames();
        var total = Math.Max(1, list.Count);
        var done = 0;
        void Step() => progress?.Report(Math.Min(100d, ++done * 100d / total));

        var localCategories = list.Where(c => !c.IsRecycleBin && (!c.RequiresElevation || _elevation.IsElevated)).ToList();
        var elevatedCategories = list.Where(c => !c.IsRecycleBin && c.RequiresElevation && !_elevation.IsElevated).ToList();

        foreach (var category in localCategories)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                results[category.Id] = await Task.Run(() => CleanLocal(category, running, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results[category.Id] = Cancelled(category.Id);
                break;
            }
            Step();
        }

        if (elevatedCategories.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            foreach (var result in await CleanElevatedAsync(elevatedCategories, cancellationToken).ConfigureAwait(false))
            {
                results[result.CategoryId] = result;
                Step();
            }
        }

        foreach (var category in list.Where(c => c.IsRecycleBin))
        {
            if (cancellationToken.IsCancellationRequested) break;
            results[category.Id] = EmptyRecycleBin(category);
            Step();
        }

        progress?.Report(100);
        return list.Select(c => results.TryGetValue(c.Id, out var r) ? r : Cancelled(c.Id)).ToList();
    }

    private static CleanupExecutionResult Cancelled(string categoryId)
        => new(categoryId, 0, 0, 0, OperationResult.Fail(OperationErrorKind.Cancelled, TextRef.Of("Opt_Cleanup_Cancelled")));

    private CleanupExecutionResult CleanLocal(CleanupCategoryDefinition category, HashSet<string> running, CancellationToken cancellationToken)
    {
        var blocker = FindBlockingProcess(category, running);
        if (blocker is not null)
            return new CleanupExecutionResult(category.Id, 0, 0, 0, OperationResult.Fail(OperationErrorKind.InUse, TextRef.Of("Opt_Cleanup_CloseApp", blocker)));

        try
        {
            var totals = _executor.Delete(category, null, cancellationToken);
            _logger.LogInformation("Nettoyage {Category} : {Deleted} supprimé(s), {Skipped} conservé(s), {Bytes} octets",
                category.Id, totals.FilesDeleted, totals.FilesSkipped + totals.FilesFailed, totals.BytesFreed);
            // Les fichiers récents, verrouillés ou protégés sont volontairement conservés : ce n'est pas un échec.
            return new CleanupExecutionResult(category.Id, totals.BytesFreed, totals.FilesDeleted, totals.FilesSkipped + totals.FilesFailed, OperationResult.Ok());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Nettoyage de la catégorie {Category} impossible", category.Id);
            return new CleanupExecutionResult(category.Id, 0, 0, 0, OperationResult.FromException(ex) with { Message = TextRef.Of("Opt_Cleanup_Failed") });
        }
    }

    private async Task<IReadOnlyList<CleanupExecutionResult>> CleanElevatedAsync(IReadOnlyList<CleanupCategoryDefinition> categories, CancellationToken cancellationToken)
    {
        // Seuls des identifiants du catalogue sont transmis : l'Elevator résout lui-même les chemins.
        var ids = categories.Select(c => c.Id).Where(id => CleanupCatalog.Find(id) is { RequiresElevation: true, IsRecycleBin: false }).ToList();
        var request = new ElevatedRequest(ElevatedOperations.CleanupCategory,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["categories"] = string.Join(',', ids) });

        ElevatedResponse response;
        try
        {
            response = await _elevation.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Annulation ou échec du lancement : l'issue réelle est inconnue, aucune donnée n'est inventée.
            response = new ElevatedResponse(OperationResult.FromException(ex), new Dictionary<string, string>());
        }

        var results = new List<CleanupExecutionResult>(categories.Count);
        foreach (var category in categories)
        {
            if (response.Outcome.Error == OperationErrorKind.ElevationCancelled)
            {
                results.Add(new CleanupExecutionResult(category.Id, 0, 0, 0,
                    OperationResult.Fail(OperationErrorKind.ElevationCancelled, TextRef.Of("Opt_Error_ElevationCancelled"))));
                continue;
            }

            var hasData = TryRead(response.Data, $"{category.Id}.bytes", out var bytes);
            TryRead(response.Data, $"{category.Id}.deleted", out var deleted);
            TryRead(response.Data, $"{category.Id}.skipped", out var skipped);
            var outcome = hasData
                ? OperationResult.Ok()
                : response.Outcome.Success
                    ? OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Opt_Cleanup_Failed"), "réponse incomplète")
                    : response.Outcome with { Message = response.Outcome.Message ?? TextRef.Of("Opt_Cleanup_Failed") };
            results.Add(new CleanupExecutionResult(category.Id, Math.Max(0, bytes), (int)Math.Clamp(deleted, 0, int.MaxValue), (int)Math.Clamp(skipped, 0, int.MaxValue), outcome));
        }
        _logger.LogInformation("Nettoyage élevé ({Categories}) : {Outcome}", string.Join(',', ids), response.Outcome.Error);
        return results;
    }

    private CleanupExecutionResult EmptyRecycleBin(CleanupCategoryDefinition category)
    {
        var before = _recycleBin.Query();
        var outcome = _recycleBin.Empty();
        var bytes = outcome.Success && before.Success ? before.Value.Bytes : 0;
        var items = outcome.Success && before.Success ? (int)Math.Min(int.MaxValue, before.Value.Items) : 0;
        return new CleanupExecutionResult(category.Id, bytes, items, 0,
            outcome.Success ? outcome : outcome with { Message = outcome.Message ?? TextRef.Of("Opt_Cleanup_RecycleBinUnavailable") });
    }

    private HashSet<string> RunningProcessNames()
    {
        try
        {
            return _processes.GetProcessIdentities().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Liste des processus indisponible");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Nom affichable du premier processus bloquant en cours d'exécution, ou null.</summary>
    private static string? FindBlockingProcess(CleanupCategoryDefinition category, HashSet<string> running)
    {
        foreach (var name in category.BlockingProcesses)
        {
            var normalized = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
            if (running.Contains(normalized))
                return BrowserNames.TryGetValue(normalized, out var display) ? display : normalized;
        }
        return null;
    }

    private static bool TryRead(IReadOnlyDictionary<string, string>? data, string key, out long value)
    {
        value = 0;
        return data is not null && data.TryGetValue(key, out var text)
               && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
