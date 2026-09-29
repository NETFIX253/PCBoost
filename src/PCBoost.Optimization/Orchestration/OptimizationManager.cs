using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;
using PCBoost.Optimization.Modules;

namespace PCBoost.Optimization.Orchestration;

/// <summary>
/// Orchestrateur (§9, §38) : aperçus (dry-run), validation de sûreté, session de restauration, application des seuls
/// changements sélectionnés, vérification, rapport (§71). Une optimisation qui échoue n'interrompt pas les autres.
/// </summary>
public sealed class OptimizationManager : IOptimizationManager
{
    private readonly IReadOnlyList<IOptimization> _optimizations;
    private readonly IOptimizationSafetyValidator _validator;
    private readonly IRollbackManager _rollback;
    private readonly IActivityJournal _journal;
    private readonly IPowerProvider _power;
    private readonly IRegistryProvider _registry;
    private readonly ILogger<OptimizationManager> _logger;

    public OptimizationManager(IEnumerable<IOptimization> optimizations, IOptimizationSafetyValidator validator, IRollbackManager rollback,
        IActivityJournal journal, IPowerProvider power, IRegistryProvider registry, ILogger<OptimizationManager>? logger = null)
    {
        _optimizations = optimizations.GroupBy(o => o.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        _validator = validator;
        _rollback = rollback;
        _journal = journal;
        _power = power;
        _registry = registry;
        _logger = logger ?? NullLogger<OptimizationManager>.Instance;
    }

    public IReadOnlyList<IOptimization> Optimizations => _optimizations;

    public IOptimization? Find(string optimizationId)
        => _optimizations.FirstOrDefault(o => string.Equals(o.Id, optimizationId, StringComparison.Ordinal));

    /// <summary>Modules du plan « Optimiser mon PC » pour une analyse donnée.</summary>
    internal static IReadOnlyList<string> OneClickOptimizationIds(SystemAnalysisReport? analysis)
    {
        var ids = new List<string> { OptimizationIds.TemporaryFiles, OptimizationIds.StartupApps, OptimizationIds.PowerPlan };
        if (VisualEffectsOptimization.IsSmallConfiguration(analysis?.HardwareProfile?.Tier)) ids.Add(OptimizationIds.VisualEffects);
        return ids;
    }

    public Task<OptimizationPlan> BuildOneClickPlanAsync(SystemAnalysisReport analysis, CancellationToken cancellationToken = default)
        => BuildPlanAsync(SessionType.OneClick, OneClickOptimizationIds(analysis), analysis, null, cancellationToken);

    public Task<OptimizationPlan> BuildPlanAsync(SessionType sessionType, IReadOnlyCollection<string> optimizationIds, SystemAnalysisReport? analysis,
        string? profileId = null, CancellationToken cancellationToken = default)
        => BuildPlanAsync(sessionType, optimizationIds, analysis, profileId, null, cancellationToken);

    /// <summary>Variante interne acceptant des données de contexte supplémentaires pour les aperçus.</summary>
    internal async Task<OptimizationPlan> BuildPlanAsync(SessionType sessionType, IReadOnlyCollection<string> optimizationIds, SystemAnalysisReport? analysis,
        string? profileId, IReadOnlyDictionary<string, object>? items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(optimizationIds);
        var previews = new List<OptimizationPreview>();
        foreach (var id in optimizationIds.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var optimization = Find(id);
            if (optimization is null)
            {
                _logger.LogWarning("Optimisation inconnue demandée : {Id}", id);
                continue;
            }
            var context = CreateContext(sessionType, analysis, null, profileId, items);
            previews.Add(await PreviewSafeAsync(optimization, context, cancellationToken).ConfigureAwait(false));
        }

        var selected = previews.Where(p => p.Applicable).SelectMany(p => p.Changes).Where(c => c.SelectedByDefault)
            .Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        return new OptimizationPlan(sessionType, previews, selected, profileId);
    }

    private async Task<OptimizationPreview> PreviewSafeAsync(IOptimization optimization, OptimizationContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await optimization.PreviewAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Aperçu de {Id} impossible", optimization.Id);
            return OptimizationPreview.NotApplicable(optimization.Id, optimization.Name, TextRef.Of("Opt_Preview_Failed"),
                optimization.RiskLevel, optimization.ImpactLevel, optimization.IsReversible);
        }
    }

    /// <summary>
    /// Contexte d'exécution. Les effets visuels sont « forcés » pour l'assistant ancien PC et le profil Économie d'énergie
    /// (règle unique partagée par l'aperçu et l'application).
    /// </summary>
    internal static OptimizationContext CreateContext(SessionType sessionType, SystemAnalysisReport? analysis, IReadOnlySet<string>? selected,
        string? profileId, IReadOnlyDictionary<string, object>? items = null)
    {
        var context = new OptimizationContext(analysis, selected, profileId);
        if (sessionType == SessionType.OldPcAssistant || string.Equals(profileId, PerformanceProfile.PowerSaverId, StringComparison.Ordinal))
            context.Items[OptimizationIds.ForceItemKey] = true;
        if (items is not null)
            foreach (var (key, value) in items) context.Items[key] = value;
        return context;
    }

    public async Task<OptimizationRunReport> ExecuteAsync(OptimizationPlan plan, SystemAnalysisReport? analysis, IProgress<OptimizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var warnings = new List<TextRef>();
        var results = new List<OptimizationResult>();
        Report(progress, OptimizationStage.Analysis, 0, null);

        // Préparation : validation de sûreté de chaque module sélectionné (un refus devient un avertissement).
        var toRun = new List<IOptimization>();
        foreach (var preview in plan.Previews)
        {
            var hasSelection = preview.Changes.Any(c => plan.SelectedChangeIds.Contains(c.Id));
            if (!hasSelection) continue;
            var optimization = Find(preview.OptimizationId);
            if (optimization is null)
            {
                warnings.Add(TextRef.Of("Opt_Warning_UnknownOptimization", preview.OptimizationId));
                continue;
            }
            var validation = _validator.Validate(optimization, preview, plan);
            if (!validation.Allowed)
            {
                var blocking = validation.Violations.Where(v => v.Blocking).ToList();
                warnings.AddRange(blocking.Select(v => v.Message));
                results.Add(new OptimizationResult(optimization.Id,
                    OperationResult.Fail(OperationErrorKind.Blocked, blocking.FirstOrDefault()?.Message, string.Join(", ", blocking.Select(v => v.Code))),
                    0, 0, 0, false, blocking.Select(v => v.Message).ToList()));
                _logger.LogInformation("Module {Id} refusé par la validation de sûreté : {Codes}", optimization.Id, string.Join(", ", blocking.Select(v => v.Code)));
                continue;
            }
            toRun.Add(optimization);
        }
        Report(progress, OptimizationStage.Preparation, 10, null);

        if (toRun.Count == 0)
        {
            Report(progress, OptimizationStage.Completed, 100, null);
            return new OptimizationRunReport(Guid.Empty, results.Count > 0 ? SessionStatus.Failed : SessionStatus.Completed,
                0, 0, 0, 0, 0, false, plan.ProfileId, results, warnings);
        }

        // Sauvegarde : ouverture de la session de restauration (write-ahead).
        IChangeRecorder recorder;
        try
        {
            recorder = await _rollback.BeginSessionAsync(plan.SessionType, TitleOf(plan), plan.ProfileId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // Sans journal de restauration, aucune modification n'est appliquée.
            _logger.LogError(ex, "Journal de restauration indisponible : optimisation annulée");
            warnings.Add(TextRef.Of("Opt_Error_JournalUnavailable"));
            Report(progress, OptimizationStage.Completed, 100, null);
            return new OptimizationRunReport(Guid.Empty, SessionStatus.Failed, 0, 0, 0, 0, 0, false, plan.ProfileId, results, warnings);
        }
        Report(progress, OptimizationStage.Backup, 15, null);

        var context = CreateContext(plan.SessionType, analysis, plan.SelectedChangeIds, plan.ProfileId);
        var cancelled = false;
        var moduleExceptions = 0;
        try
        {
            for (var i = 0; i < toRun.Count; i++)
            {
                var optimization = toRun[i];
                Report(progress, OptimizationStage.Optimization, 15 + 70d * i / toRun.Count, optimization.Name);
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                try
                {
                    results.Add(await optimization.ApplyAsync(context, recorder, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    moduleExceptions++;
                    _logger.LogError(ex, "L'optimisation {Id} a échoué", optimization.Id);
                    warnings.Add(TextRef.Of("Opt_Warning_ModuleFailed", optimization.Id));
                    results.Add(new OptimizationResult(optimization.Id, OperationResult.FromException(ex) with { Message = TextRef.Of("Opt_Result_Failed") },
                        0, 1, 0, false, [TextRef.Of("Opt_Result_Failed")]));
                }
            }
            if (cancelled) warnings.Add(TextRef.Of("Opt_Warning_Cancelled"));

            // Vérification : relecture de l'état réel des modifications appliquées.
            Report(progress, OptimizationStage.Verification, 90, null);
            var session = await _rollback.GetSessionAsync(recorder.SessionId, CancellationToken.None).ConfigureAwait(false);
            if (session is not null) warnings.AddRange(Verify(session.Changes));
        }
        finally
        {
            await _rollback.CompleteSessionAsync(recorder.SessionId, results.Sum(r => r.BytesFreed), results.Any(r => r.RequiresRestart), CancellationToken.None)
                .ConfigureAwait(false);
        }

        var completed = await _rollback.GetSessionAsync(recorder.SessionId, CancellationToken.None).ConfigureAwait(false);
        var changes = completed?.Changes ?? [];
        var report = new OptimizationRunReport(
            recorder.SessionId,
            completed?.Status ?? SessionStatus.Completed,
            ActionsPerformed: changes.Count(c => c.Status is ChangeStatus.Applied or ChangeStatus.Irreversible),
            ActionsFailed: changes.Count(c => c.Status is ChangeStatus.Failed or ChangeStatus.Pending) + moduleExceptions,
            BytesFreed: results.Sum(r => r.BytesFreed),
            StartupItemsDisabled: changes.Count(c => c.Status == ChangeStatus.Applied && c.OptimizationId == OptimizationIds.StartupApps),
            ProcessesAdjusted: changes.Count(c => c.Status == ChangeStatus.Applied && c.Kind is ChangeKinds.ProcessPriority or ChangeKinds.ProcessEfficiency),
            RequiresRestart: results.Any(r => r.RequiresRestart),
            ProfileId: plan.ProfileId,
            Results: results,
            Warnings: warnings.DistinctBy(w => w.ToString()).ToList());

        Report(progress, OptimizationStage.Completed, 100, null);
        await _journal.TryLogAsync(report.ActionsFailed == 0 ? ActivityKind.Optimization : ActivityKind.Warning,
            TextRef.Of("Opt_Journal_RunCompleted", report.ActionsPerformed, report.ActionsFailed, report.BytesFreed / (double)ByteSize.MiB),
            $"session={report.SessionId} type={plan.SessionType} status={report.Status}", _logger).ConfigureAwait(false);
        return report;
    }

    /// <summary>Relit l'état réel (plan actif, StartupApproved) et signale tout écart par un avertissement.</summary>
    private IEnumerable<TextRef> Verify(IReadOnlyList<ChangeRecord> changes)
    {
        var warnings = new List<TextRef>();
        foreach (var change in changes.Where(c => c.Status == ChangeStatus.Applied && c.AfterState is not null))
        {
            try
            {
                switch (change.Kind)
                {
                    case ChangeKinds.PowerScheme:
                        var expectedScheme = ChangeStateSerializer.Deserialize<PowerSchemeState>(change.AfterState);
                        if (expectedScheme is not null && _power.GetActiveScheme()?.Id != expectedScheme.SchemeId)
                            warnings.Add(TextRef.Of("Opt_Warning_VerifyPowerPlan"));
                        break;
                    case ChangeKinds.RegistryValue:
                        var expected = ChangeStateSerializer.Deserialize<RegistryValueState>(change.AfterState);
                        if (expected is null) break;
                        var actual = RegistryValueState.Capture(expected.Location, expected.ValueName, _registry.GetValue(expected.Location, expected.ValueName));
                        if (!SameValue(expected, actual)) warnings.Add(TextRef.Of("Opt_Warning_VerifyRegistry"));
                        break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "Vérification impossible pour {Kind}", change.Kind);
            }
        }
        return warnings;
    }

    internal static bool SameValue(RegistryValueState a, RegistryValueState b)
        => a.Existed == b.Existed
           && (!a.Existed || (a.Type == b.Type
                              && a.StringValue == b.StringValue
                              && a.NumericValue == b.NumericValue
                              && a.BinaryBase64 == b.BinaryBase64
                              && (a.MultiStringValue ?? []).SequenceEqual(b.MultiStringValue ?? [])));

    private static TextRef TitleOf(OptimizationPlan plan) => plan.SessionType switch
    {
        SessionType.OneClick => TextRef.Of("Opt_Session_OneClick"),
        SessionType.Profile => TextRef.Of("Opt_Session_Profile_" + (plan.ProfileId is PerformanceProfile.BalancedId or PerformanceProfile.ProductivityId
            or PerformanceProfile.GamingId or PerformanceProfile.PowerSaverId ? plan.ProfileId : PerformanceProfile.CustomId)),
        SessionType.OldPcAssistant => TextRef.Of("Opt_Session_OldPc"),
        SessionType.Gaming => TextRef.Of("Opt_Session_Gaming"),
        SessionType.Automatic => TextRef.Of("Opt_Session_Automatic"),
        SessionType.Cleanup => TextRef.Of("Opt_Session_Cleanup"),
        SessionType.Startup => TextRef.Of("Opt_Session_Startup"),
        _ => TextRef.Of("Opt_Session_Manual"),
    };

    private static void Report(IProgress<OptimizationProgress>? progress, OptimizationStage stage, double percent, TextRef? action)
        => progress?.Report(new OptimizationProgress(stage, Math.Clamp(percent, 0, 100), action));
}
