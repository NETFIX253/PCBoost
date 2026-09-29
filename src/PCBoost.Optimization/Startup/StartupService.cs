using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Startup;

/// <summary>
/// Gestionnaire de démarrage (§12) : impact mesuré sur le processus actuellement lancé (jamais inventé), recommandation
/// expliquée, activation/désactivation via StartupApproved (réversible, visible dans le Gestionnaire des tâches).
/// Ne désactive jamais rien automatiquement.
/// </summary>
public sealed class StartupService : IStartupService
{
    internal const long HighWorkingSet = 300 * ByteSize.MiB;
    internal const long MediumWorkingSet = 100 * ByteSize.MiB;
    internal static readonly TimeSpan HighCpu = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan MediumCpu = TimeSpan.FromSeconds(15);
    internal const long HighIoRead = 500 * ByteSize.MiB;
    internal const long MediumIoRead = 100 * ByteSize.MiB;

    private readonly IStartupProvider _provider;
    private readonly IProcessProvider _processes;
    private readonly StartupToggler _toggler;
    private readonly IRollbackManager _rollback;
    private readonly IActivityJournal _journal;
    private readonly ILogger<StartupService> _logger;

    internal StartupService(IStartupProvider provider, IProcessProvider processes, StartupToggler toggler, IRollbackManager rollback,
        IActivityJournal journal, ILogger<StartupService>? logger = null)
    {
        _provider = provider;
        _processes = processes;
        _toggler = toggler;
        _rollback = rollback;
        _journal = journal;
        _logger = logger ?? NullLogger<StartupService>.Instance;
    }

    public StartupService(IStartupProvider provider, IProcessProvider processes, IRegistryProvider registry, IScheduledTaskProvider tasks,
        IElevationService elevation, IRollbackManager rollback, IActivityJournal journal, IClock clock, ILogger<StartupService>? logger = null)
        : this(provider, processes,
            new StartupToggler(registry, new RegistryWriter(registry, elevation), tasks, new ScheduledTaskWriter(tasks, elevation), clock),
            rollback, journal, logger)
    {
    }

    public async Task<IReadOnlyList<StartupEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _provider.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProcessSnapshot> snapshots;
        try
        {
            snapshots = _processes.GetProcesses();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Instantané des processus indisponible : impact non mesuré");
            snapshots = [];
        }

        var byPath = snapshots
            .Where(p => !string.IsNullOrWhiteSpace(p.ExecutablePath))
            .GroupBy(p => p.ExecutablePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        return entries.Select(e => Enrich(e, byPath)).ToList();
    }

    internal static StartupEntry Enrich(StartupEntry entry, IReadOnlyDictionary<string, List<ProcessSnapshot>> processesByPath)
    {
        var (impact, evidence) = MeasureImpact(entry, processesByPath);
        var (recommendation, reason) = Recommend(entry);
        return entry with { Impact = impact, Evidence = evidence, Recommendation = recommendation, RecommendationReason = reason };
    }

    /// <summary>Impact mesuré à partir des processus lancés depuis le même exécutable ; non lancé → NotMeasured.</summary>
    internal static (StartupImpact Impact, StartupImpactEvidence Evidence) MeasureImpact(StartupEntry entry, IReadOnlyDictionary<string, List<ProcessSnapshot>> processesByPath)
    {
        if (string.IsNullOrWhiteSpace(entry.ExecutablePath) || !processesByPath.TryGetValue(entry.ExecutablePath, out var processes) || processes.Count == 0)
            return (StartupImpact.NotMeasured, new StartupImpactEvidence(false, null, null, null));

        var workingSet = processes.Sum(p => p.WorkingSetBytes);
        var cpu = TimeSpan.FromTicks(processes.Sum(p => p.TotalProcessorTime.Ticks));
        var ioRead = processes.Sum(p => p.IoReadBytes);
        var evidence = new StartupImpactEvidence(true, workingSet, cpu, ioRead);

        if (workingSet > HighWorkingSet || cpu > HighCpu || ioRead > HighIoRead) return (StartupImpact.High, evidence);
        if (workingSet > MediumWorkingSet || cpu > MediumCpu || ioRead > MediumIoRead) return (StartupImpact.Medium, evidence);
        return (StartupImpact.Low, evidence);
    }

    /// <summary>Recommandation prudente et expliquée (TextRef).</summary>
    internal static (StartupRecommendation Recommendation, TextRef Reason) Recommend(StartupEntry entry)
    {
        if (entry.IsSecuritySoftware || KnownSoftware.IsSecuritySoftware(entry.Name, entry.ExecutablePath, entry.Publisher))
            return (StartupRecommendation.Keep, TextRef.Of("Opt_StartupReason_Security"));

        if (KnownSoftware.IsHardwareDriverUtility(entry.ItemName, entry.ExecutablePath, entry.Publisher)
            || KnownSoftware.IsHardwareDriverUtility(entry.Name, null, null))
            return (StartupRecommendation.Keep, TextRef.Of("Opt_StartupReason_Driver"));

        if (!entry.ExecutableExists)
            return (StartupRecommendation.Review, TextRef.Of("Opt_StartupReason_FileMissing"));

        if (entry.Signature.Status is SignatureStatus.Unsigned or SignatureStatus.Invalid)
            return (StartupRecommendation.Review, TextRef.Of(entry.Signature.Status == SignatureStatus.Invalid ? "Opt_StartupReason_InvalidSignature" : "Opt_StartupReason_Unsigned"));

        if (string.IsNullOrWhiteSpace(entry.Publisher) && string.IsNullOrWhiteSpace(entry.Signature.Signer))
            return (StartupRecommendation.Review, TextRef.Of("Opt_StartupReason_UnknownPublisher"));

        var optional = KnownSoftware.OptionalFamily(entry.ItemName, entry.ExecutablePath);
        if (optional == KnownAppFamily.None) optional = KnownSoftware.OptionalFamily(entry.Name, null);
        if (optional != KnownAppFamily.None)
            return (StartupRecommendation.Optional, TextRef.Of("Opt_StartupReason_" + optional));

        var family = KnownSoftware.DisableableFamily(entry.ItemName, entry.ExecutablePath);
        if (family == KnownAppFamily.None) family = KnownSoftware.DisableableFamily(entry.Name, null);
        if (family != KnownAppFamily.None)
            return (StartupRecommendation.CanDisable, TextRef.Of("Opt_StartupReason_" + family));

        if (entry.IsMicrosoft)
            return (StartupRecommendation.Keep, TextRef.Of("Opt_StartupReason_Microsoft"));

        return (StartupRecommendation.Optional, TextRef.Of("Opt_StartupReason_SignedOptional"));
    }

    public async Task<OperationResult> SetEnabledAsync(StartupEntry entry, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!enabled && entry.IsSecuritySoftware)
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Opt_Startup_SecurityBlocked"));

        // Une session par action utilisateur : chaque changement peut être annulé indépendamment.
        IChangeRecorder recorder;
        try
        {
            recorder = await _rollback.BeginSessionAsync(SessionType.Startup,
                TextRef.Of(enabled ? "Opt_Session_StartupEnable" : "Opt_Session_StartupDisable", entry.Name), null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogError(ex, "Journal de restauration indisponible : entrée de démarrage non modifiée");
            return OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Opt_Error_JournalUnavailable"), ex.GetType().Name);
        }

        OperationResult outcome;
        try
        {
            outcome = await _toggler.SetEnabledAsync(entry, enabled, recorder, OptimizationIds.StartupManual, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _rollback.CompleteSessionAsync(recorder.SessionId, 0, false, CancellationToken.None).ConfigureAwait(false);
        }

        await _journal.TryLogAsync(
            outcome.Success ? ActivityKind.Startup : ActivityKind.Warning,
            TextRef.Of(outcome.Success
                ? enabled ? "Opt_Journal_StartupEnabled" : "Opt_Journal_StartupDisabled"
                : "Opt_Journal_StartupChangeFailed", entry.Name),
            $"{entry.Location} {outcome.Error}", _logger).ConfigureAwait(false);
        return outcome;
    }
}
