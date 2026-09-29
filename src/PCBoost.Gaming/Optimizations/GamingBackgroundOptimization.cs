using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Gaming.Common;
using PCBoost.Gaming.Detection;

namespace PCBoost.Gaming.Optimizations;

/// <summary>
/// Ralentit (mode efficacité si pris en charge, sinon priorité « Inférieure à la normale ») les programmes utilisateur non
/// essentiels pendant la session : synchronisation cloud, mises à jour, navigateurs hors premier plan, et processus sans
/// fenêtre mesurés au-dessus de 2 % du processeur. Jamais : processus critiques ou sensibles, le jeu et ses processus,
/// les exclusions de l'utilisateur, l'anti-triche, les lanceurs, les pilotes audio/graphiques, la communication vocale,
/// la diffusion, l'accessibilité, ni la fenêtre au premier plan. Au plus 15 processus. Rien n'est jamais fermé.
/// </summary>
public sealed class GamingBackgroundOptimization : GamingOptimizationBase
{
    private readonly IProcessProvider _processes;
    private readonly IProcessControl _control;
    private readonly ICriticalProcessProtection _protection;
    private readonly IForegroundWindowProvider _foreground;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly ISettingsService _settings;
    private readonly IFileSystemProvider _fileSystem;
    private readonly GameSignatureDatabase _signatures;
    private readonly IClock _clock;
    private readonly GamingOptions _options;
    private readonly ILogger<GamingBackgroundOptimization> _logger;
    private readonly Lock _cacheLock = new();
    private CachedCandidates? _cache;

    public GamingBackgroundOptimization(
        IProcessProvider processes,
        IProcessControl control,
        ICriticalProcessProtection protection,
        IForegroundWindowProvider foreground,
        ISystemInfoProvider systemInfo,
        ISettingsService settings,
        IFileSystemProvider fileSystem,
        GameSignatureDatabase signatures,
        IClock clock,
        GamingOptions? options = null,
        IServiceProvider? services = null,
        ILogger<GamingBackgroundOptimization>? logger = null)
        : base(services)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new GamingOptions();
        _logger = logger ?? NullLogger<GamingBackgroundOptimization>.Instance;
    }

    public override string Id => GamingOptimizationIds.Background;

    public override TextRef Name => TextRef.Of("Game_Opt_Background_Name");

    public override TextRef Description => TextRef.Of("Game_Opt_Background_Description");

    public override OptimizationCategory Category => OptimizationCategory.Background;

    public override RiskLevel RiskLevel => RiskLevel.Medium;

    public override ImpactLevel ImpactLevel => ImpactLevel.Medium;

    public override async Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var candidates = await FindCandidatesAsync(context, cancellationToken).ConfigureAwait(false);
        lock (_cacheLock) _cache = new CachedCandidates(GetGameProcessId(context), _clock.UtcNow, candidates);
        return BuildPreview(candidates);
    }

    public override async Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(recorder);

        IReadOnlyList<BackgroundCandidate>? candidates = null;
        lock (_cacheLock)
        {
            if (_cache is { } c && c.GameProcessId == GetGameProcessId(context) && _clock.UtcNow - c.Timestamp <= _options.BackgroundPreviewReuse)
                candidates = c.Candidates;
            _cache = null;
        }
        candidates ??= await FindCandidatesAsync(context, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0) return OptimizationResult.Skipped(Id, TextRef.Of("Game_Background_NothingToDo"));

        int applied = 0, failed = 0, skipped = 0;
        var foreground = _foreground.GetForegroundProcessId();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = ToPlannedChange(candidate);
            if (!context.IsSelected(change)) continue;

            // Revalidation : processus toujours le même, pas passé au premier plan, priorité toujours « Normale ».
            if (!_processes.IsRunning(candidate.ProcessId, candidate.StartTime) || foreground == candidate.ProcessId)
            {
                skipped++;
                continue;
            }
            var priority = _control.GetPriority(candidate.ProcessId);
            if (!priority.Success || priority.Value != ProcessPriority.Normal)
            {
                skipped++;
                continue;
            }
            // Sonde sans effet (priorité identique) : vérifie le droit de modification sans rien consigner.
            if (!_control.SetPriority(candidate.ProcessId, ProcessPriority.Normal).Success)
            {
                skipped++;
                continue;
            }

            var outcome = await ApplyOneAsync(candidate, change, recorder, cancellationToken).ConfigureAwait(false);
            if (outcome.Success) applied++;
            else if (outcome.Error is OperationErrorKind.AccessDenied or OperationErrorKind.NotFound or OperationErrorKind.Blocked) skipped++;
            else failed++;
        }

        _logger.LogInformation("Mode Gaming : {Applied} processus d'arrière-plan ralentis, {Skipped} ignorés, {Failed} échecs", applied, skipped, failed);
        var messages = new List<TextRef> { TextRef.Of("Game_Background_Applied", applied) };
        if (skipped > 0) messages.Add(TextRef.Of("Game_Background_Skipped", skipped));
        if (failed > 0) messages.Add(TextRef.Of("Game_Background_Failed", failed));
        var result = failed > 0 && applied == 0
            ? OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Game_Background_Failed", failed))
            : OperationResult.Ok();
        return new OptimizationResult(Id, result, applied, failed, 0, false, messages);
    }

    private Task<OperationResult> ApplyOneAsync(BackgroundCandidate candidate, PlannedChange change, IChangeRecorder recorder, CancellationToken cancellationToken)
    {
        if (candidate.Mode == ThrottleMode.Efficiency)
        {
            var before = ChangeStateSerializer.Serialize(new ProcessEfficiencyState(candidate.ProcessId, candidate.Name, candidate.StartTime, Enabled: false));
            var pending = new PendingChange(ChangeKinds.ProcessEfficiency, Id, change.Target, change.Description, before, Reversible: true);
            return recorder.ApplyAsync(pending, _ => Task.FromResult(_control.SetEfficiencyMode(candidate.ProcessId, true)), cancellationToken);
        }
        else
        {
            var before = ChangeStateSerializer.Serialize(new ProcessPriorityState(candidate.ProcessId, candidate.Name, candidate.StartTime, ProcessPriority.Normal));
            var pending = new PendingChange(ChangeKinds.ProcessPriority, Id, change.Target, change.Description, before, Reversible: true);
            return recorder.ApplyAsync(pending, _ => Task.FromResult(_control.SetPriority(candidate.ProcessId, ProcessPriority.BelowNormal)), cancellationToken);
        }
    }

    private OptimizationPreview BuildPreview(IReadOnlyList<BackgroundCandidate> candidates)
        => candidates.Count == 0
            ? NotApplicable(TextRef.Of("Game_Background_NothingToDo"))
            : Applicable(candidates.Select(ToPlannedChange).ToList());

    private PlannedChange ToPlannedChange(BackgroundCandidate c)
        => new(
            $"{Id}:{c.Name}:{c.ProcessId}",
            TextRef.Of(c.Mode == ThrottleMode.Efficiency ? "Game_Background_ChangeEfficiency" : "Game_Background_ChangePriority", DisplayName(c.Name)),
            $"process:{c.Name} ({c.ProcessId})",
            SelectedByDefault: true,
            Reversible: true,
            RiskLevel.Low);

    /// <summary>Processus à ralentir, mesurés sur <see cref="GamingOptions.BackgroundCpuSampleDuration"/>.</summary>
    internal async Task<IReadOnlyList<BackgroundCandidate>> FindCandidatesAsync(OptimizationContext context, CancellationToken cancellationToken)
    {
        var gamePid = GetGameProcessId(context);
        var gamePath = GetGameExecutablePath(context);
        var gameDirectory = GetGameInstallDirectory(context) ?? WinPath.GetDirectoryName(gamePath);
        if (gameDirectory is not null && WinPath.IsDriveRoot(gameDirectory)) gameDirectory = null;
        var gameName = gamePath is null ? null : GameSignatureDatabase.Normalize(gamePath);
        var exclusions = new HashSet<string>(_settings.Current.Gaming.BackgroundExclusions.Select(GameSignatureDatabase.Normalize), StringComparer.OrdinalIgnoreCase);
        var windows = _fileSystem.GetKnownFolder(KnownFolder.WindowsDirectory);

        var first = _processes.GetProcesses();
        var duration = _options.BackgroundCpuSampleDuration;
        await _options.Delay(duration, cancellationToken).ConfigureAwait(false);
        var second = _processes.GetProcesses();
        var foreground = _foreground.GetForegroundProcessId();
        if (gamePid is not null && gameName is null)
            gameName = second.FirstOrDefault(p => p.ProcessId == gamePid)?.Name is { } n ? GameSignatureDatabase.Normalize(n) : null;

        var logical = Math.Max(1, _systemInfo.GetCpuInfo().LogicalProcessors);
        var firstById = new Dictionary<int, ProcessSnapshot>();
        foreach (var p in first) firstById[p.ProcessId] = p;
        var efficiency = _control.IsEfficiencyModeSupported;

        var candidates = new List<BackgroundCandidate>();
        foreach (var p in second)
        {
            var name = GameSignatureDatabase.Normalize(p.Name);
            if (!IsEligible(p, name, gamePid, gameName, gameDirectory, foreground, exclusions, windows)) continue;

            var cpu = 0d;
            if (firstById.TryGetValue(p.ProcessId, out var before) && before.StartTime == p.StartTime && duration > TimeSpan.Zero)
                cpu = Math.Max(0, (p.TotalProcessorTime - before.TotalProcessorTime).TotalMilliseconds / (duration.TotalMilliseconds * logical) * 100d);

            var known = BackgroundProcessCatalog.IsKnownNonEssential(name);
            var busyWithoutWindow = !p.HasMainWindow && cpu > _options.BackgroundCpuThresholdPercent;
            if (!known && !busyWithoutWindow) continue;
            // Uniquement depuis la priorité « Normale » : l'annulation rétablit exactement l'état d'origine.
            if (p.Priority != ProcessPriority.Normal) continue;

            var mode = ThrottleMode.Priority;
            if (efficiency)
            {
                var state = _control.GetEfficiencyMode(p.ProcessId);
                if (!state.Success || state.Value) continue; // illisible ou déjà en mode efficacité
                mode = ThrottleMode.Efficiency;
            }
            candidates.Add(new BackgroundCandidate(p.ProcessId, p.Name, p.StartTime, cpu, known, mode));
        }

        return candidates
            .OrderByDescending(c => c.CpuPercent)
            .ThenByDescending(c => c.KnownNonEssential)
            .ThenBy(c => c.ProcessId)
            .Take(Math.Max(0, _options.MaxBackgroundProcesses))
            .ToList();
    }

    private bool IsEligible(ProcessSnapshot p, string name, int? gamePid, string? gameName, string? gameDirectory, int? foreground,
        HashSet<string> exclusions, string? windows)
    {
        if (p.ProcessId <= 4 || p.ProcessId == _processes.CurrentProcessId) return false;
        if (!p.IsCurrentUser || p.SessionId != _processes.CurrentSessionId) return false;
        if (p.ProcessId == gamePid || p.ProcessId == foreground) return false;
        if (gameName is not null && string.Equals(name, gameName, StringComparison.OrdinalIgnoreCase)) return false;
        if (exclusions.Contains(name)) return false;
        if (_signatures.IsAntiCheat(name) || _signatures.IsLauncherOrUtility(name)) return false;
        if (BackgroundProcessCatalog.IsNeverThrottle(name)) return false;
        if (_protection.GetProtection(p.Name, p.ExecutablePath).Level != ProtectionLevel.None) return false;
        if (p.ExecutablePath is { } path)
        {
            if (windows is not null && WinPath.IsUnder(path, windows)) return false;
            if (path.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase)) return false;
            if (gameDirectory is not null && WinPath.IsUnder(path, gameDirectory)) return false; // processus du jeu
        }
        return true;
    }

    internal enum ThrottleMode { Efficiency, Priority }

    internal sealed record BackgroundCandidate(int ProcessId, string Name, DateTimeOffset? StartTime, double CpuPercent, bool KnownNonEssential, ThrottleMode Mode);

    private sealed record CachedCandidates(int? GameProcessId, DateTimeOffset Timestamp, IReadOnlyList<BackgroundCandidate> Candidates);
}
