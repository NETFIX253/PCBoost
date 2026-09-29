using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Orchestration;

/// <summary>
/// Profils d'utilisation (§15). Activer un profil restaure d'abord la session du profil précédent, puis applique le nouveau
/// plan dans une session <see cref="SessionType.Profile"/>. État persistant : « profiles.state » ; profil personnalisé : « profiles.custom ».
/// </summary>
public sealed class ProfileService : IProfileService
{
    public const string StateKey = "profiles.state";
    public const string CustomKey = "profiles.custom";

    private readonly IOptimizationManager _manager;
    private readonly IRollbackManager _rollback;
    private readonly IKeyValueStore _store;
    private readonly IActivityJournal _journal;
    private readonly IClock _clock;
    private readonly Func<SystemAnalysisReport?> _analysis;
    private readonly ILogger<ProfileService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<string> _customIds = [];

    public ProfileService(IOptimizationManager manager, IRollbackManager rollback, IKeyValueStore store, IActivityJournal journal, IClock clock,
        IServiceProvider services, ILogger<ProfileService>? logger = null)
        : this(manager, rollback, store, journal, clock, () => services.GetService<ISystemAnalyzer>()?.LastReport, logger)
    {
    }

    internal ProfileService(IOptimizationManager manager, IRollbackManager rollback, IKeyValueStore store, IActivityJournal journal, IClock clock,
        Func<SystemAnalysisReport?> analysis, ILogger<ProfileService>? logger)
    {
        _manager = manager;
        _rollback = rollback;
        _store = store;
        _journal = journal;
        _clock = clock;
        _analysis = analysis;
        _logger = logger ?? NullLogger<ProfileService>.Instance;
    }

    public ProfileState State { get; private set; } = new(null, null, null);

    public event EventHandler? StateChanged;

    /// <summary>Profils intégrés : modules, description documentée (ressources) et glyphe Segoe Fluent Icons.</summary>
    internal static IReadOnlyList<PerformanceProfile> BuiltInProfiles { get; } =
    [
        Profile(PerformanceProfile.BalancedId, "Balanced", [OptimizationIds.PowerPlan], ""),
        Profile(PerformanceProfile.ProductivityId, "Productivity", [OptimizationIds.PowerPlan, OptimizationIds.BackgroundApps, OptimizationIds.VisualEffects], ""),
        Profile(PerformanceProfile.GamingId, "Gaming", [OptimizationIds.PowerPlan, OptimizationIds.BackgroundApps], ""),
        Profile(PerformanceProfile.PowerSaverId, "PowerSaver", [OptimizationIds.PowerPlan, OptimizationIds.BackgroundApps, OptimizationIds.VisualEffects], ""),
    ];

    private static PerformanceProfile Profile(string id, string key, IReadOnlyList<string> ids, string glyph)
        => new(id, TextRef.Of($"Opt_Profile_{key}_Name"), TextRef.Of($"Opt_Profile_{key}_Description"), ids, IsBuiltIn: true, glyph);

    public IReadOnlyList<PerformanceProfile> GetProfiles()
        => [.. BuiltInProfiles, new PerformanceProfile(PerformanceProfile.CustomId, TextRef.Of("Opt_Profile_Custom_Name"),
            TextRef.Of("Opt_Profile_Custom_Description"), _customIds, IsBuiltIn: false, "")];

    private PerformanceProfile? FindProfile(string? profileId)
        => profileId is null ? null : GetProfiles().FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            State = await _store.GetAsync<ProfileState>(StateKey, cancellationToken).ConfigureAwait(false) ?? new ProfileState(null, null, null);
            var custom = await _store.GetAsync<List<string>>(CustomKey, cancellationToken).ConfigureAwait(false);
            _customIds = Sanitize(custom ?? []);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogWarning(ex, "Lecture de l'état des profils impossible");
        }
        RaiseStateChanged();
    }

    public async Task<OptimizationPlan> PreviewActivationAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var profile = FindProfile(profileId);
        if (profile is null) return new OptimizationPlan(SessionType.Profile, [], new HashSet<string>(StringComparer.Ordinal), profileId);
        return await _manager.BuildPlanAsync(SessionType.Profile, profile.OptimizationIds, _analysis(), profile.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OptimizationRunReport> ActivateAsync(OptimizationPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var profile = FindProfile(plan.ProfileId);
        if (profile is null)
        {
            return new OptimizationRunReport(Guid.Empty, SessionStatus.Failed, 0, 0, 0, 0, 0, false, plan.ProfileId, [],
                [TextRef.Of("Opt_Profile_Unknown")]);
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var warnings = new List<TextRef>();
            // 1) Revenir d'abord à l'état d'avant le profil précédent.
            if (State.ActiveSessionId is Guid previous)
            {
                var restore = await _rollback.RestoreSessionAsync(previous, cancellationToken).ConfigureAwait(false);
                if (!restore.Success) warnings.Add(TextRef.Of("Opt_Profile_PreviousRestorePartial", restore.Failed));
            }

            // 2) Appliquer le nouveau profil.
            var executable = plan with { SessionType = SessionType.Profile, ProfileId = profile.Id };
            var report = await _manager.ExecuteAsync(executable, _analysis(), null, cancellationToken).ConfigureAwait(false);

            await SetStateAsync(new ProfileState(profile.Id, report.SessionId == Guid.Empty ? null : report.SessionId, _clock.UtcNow)).ConfigureAwait(false);
            await _journal.TryLogAsync(ActivityKind.Optimization, TextRef.Of($"Opt_Journal_ProfileActivated_{profile.Id}"),
                $"session={report.SessionId}", _logger).ConfigureAwait(false);
            return warnings.Count == 0 ? report : report with { Warnings = [.. warnings, .. report.Warnings] };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<RollbackResult> DeactivateAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State.ActiveProfileId is null && State.ActiveSessionId is null) return RollbackResult.Empty;
            var result = State.ActiveSessionId is Guid session
                ? await _rollback.RestoreSessionAsync(session, cancellationToken).ConfigureAwait(false)
                : RollbackResult.Empty;
            await SetStateAsync(new ProfileState(null, null, null)).ConfigureAwait(false);
            await _journal.TryLogAsync(ActivityKind.Rollback, TextRef.Of("Opt_Journal_ProfileDeactivated", result.Restored), null, _logger).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveCustomProfileAsync(IReadOnlyList<string> optimizationIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(optimizationIds);
        var ids = Sanitize(optimizationIds);
        await _store.SetAsync(CustomKey, ids.ToList(), cancellationToken).ConfigureAwait(false);
        _customIds = ids;
        RaiseStateChanged();
    }

    /// <summary>Ne conserve que les modules enregistrés, sans doublon, dans l'ordre donné.</summary>
    private IReadOnlyList<string> Sanitize(IEnumerable<string> ids)
    {
        var known = _manager.Optimizations.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        return ids.Where(id => !string.IsNullOrWhiteSpace(id) && known.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task SetStateAsync(ProfileState state)
    {
        State = state;
        try
        {
            await _store.SetAsync(StateKey, state, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Enregistrement de l'état des profils impossible");
        }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Abonné StateChanged en erreur");
        }
    }
}
