using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Cleanup;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Cleanup;

namespace PCBoost.Optimization.Modules;

/// <summary>
/// temp-files : suppression des fichiers temporaires des catégories SAFE du catalogue (irréversible par nature,
/// confirmation explicite exigée par le validateur). Un changement par catégorie disponible et non vide.
/// </summary>
public sealed class TemporaryFilesOptimization : OptimizationModuleBase
{
    private readonly SafeCleanupEngine _engine;
    private readonly IElevationService _elevation;

    public TemporaryFilesOptimization(SafeCleanupEngine engine, IElevationService elevation, IRollbackManager rollback) : base(rollback)
    {
        _engine = engine;
        _elevation = elevation;
    }

    public override string Id => OptimizationIds.TemporaryFiles;
    protected override string Key => "TempFiles";
    public override OptimizationCategory Category => OptimizationCategory.Cleanup;
    public override RiskLevel RiskLevel => RiskLevel.Low;
    public override ImpactLevel ImpactLevel => ImpactLevel.Medium;
    public override bool IsReversible => false;

    internal static IReadOnlyList<CleanupCategoryDefinition> SafeCategories
        => CleanupCatalog.All.Where(c => c.Safety == SafetyCategory.Safe && !c.IsRecycleBin).ToList();

    internal static string ChangeId(string categoryId) => $"{OptimizationIds.TemporaryFiles}:{categoryId}";

    public override async Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default)
    {
        var categories = SafeCategories;
        var scans = await _engine.ScanAsync(categories, cancellationToken).ConfigureAwait(false);
        var changes = new List<PlannedChange>();
        var requiresElevation = false;
        foreach (var scan in scans.Where(s => s.Available && s.Bytes > 0))
        {
            var definition = categories.First(c => c.Id == scan.CategoryId);
            requiresElevation |= definition.RequiresElevation && !_elevation.IsElevated;
            changes.Add(new PlannedChange(
                ChangeId(scan.CategoryId),
                TextRef.Of($"Cleanup_{scan.CategoryId}_Name"),
                "cleanup:" + scan.CategoryId,
                SelectedByDefault: true,
                Reversible: false,
                Risk: RiskLevel.Low,
                EstimatedBytes: scan.Bytes));
        }

        if (changes.Count == 0) return NotApplicable(TextRef.Of("Opt_TempFiles_NothingToClean"));
        var total = changes.Sum(c => c.EstimatedBytes ?? 0);
        var impact = total >= 2 * ByteSize.GiB ? ImpactLevel.High : total >= 500 * ByteSize.MiB ? ImpactLevel.Medium : ImpactLevel.Low;
        return Applicable(changes, impact, requiresElevation);
    }

    public override async Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default)
    {
        // Les identifiants sont déterministes : la sélection se résout sans nouvelle analyse.
        var selected = SafeCategories
            .Where(c => context.IsSelected(new PlannedChange(ChangeId(c.Id), TextRef.Of($"Cleanup_{c.Id}_Name"), "cleanup:" + c.Id, true, false, RiskLevel.Low)))
            .ToList();
        if (selected.Count == 0) return OptimizationResult.Skipped(Id, TextRef.Of("Opt_Result_NothingSelected"));

        var results = await _engine.CleanAsync(selected, null, cancellationToken).ConfigureAwait(false);
        await CleanupService.RecordAsync(recorder, Id, results).ConfigureAwait(false);

        var messages = new List<TextRef>();
        if (results.Any(r => r.Outcome.Error == OperationErrorKind.ElevationCancelled))
            messages.Add(TextRef.Of("Opt_TempFiles_ElevationCancelled"));
        return Summarize(results.Select(r => r.Outcome).ToList(), results.Sum(r => r.BytesFreed), extraMessages: messages);
    }
}
