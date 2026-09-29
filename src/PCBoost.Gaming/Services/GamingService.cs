using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;
using PCBoost.Gaming.Metrics;
using PCBoost.Gaming.Settings;

namespace PCBoost.Gaming.Services;

/// <summary>
/// Mode Gaming (§16, §69) : Inactive → Activating → Active → Restoring → Inactive.
/// <para>Activation : profil matériel/charge, jeu (paramètre ou détection), plan selon les réglages, session de restauration
/// + <see cref="GamingSession"/> « Active » persistée AVANT toute modification (récupération après plantage), application des
/// modules (aperçu puis application consignée), surveillance (moniteur actif, capture d'images si autorisée, métriques
/// chaque seconde). À la sortie du jeu, restauration automatique si <see cref="GamingSettings.AutoRestore"/>.</para>
/// <para>La session de restauration reste « en cours » pendant la partie : en cas de plantage, le RecoveryManager la
/// propose au démarrage suivant. Thread-safe et idempotent.</para>
/// </summary>
public sealed class GamingService : IGamingService, IDisposable, IAsyncDisposable
{
    private readonly IGameDetectionService _detection;
    private readonly IRollbackManager _rollback;
    private readonly IGamingSessionRepository _sessions;
    private readonly ISettingsService _settings;
    private readonly IPerformanceMonitor _monitor;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly IProcessProvider _processes;
    private readonly IFrameTimeSource _frames;
    private readonly INotificationService _notifications;
    private readonly WindowsGameSettingsChecker _settingsChecker;
    private readonly IReadOnlyList<IOptimization> _optimizations;
    private readonly IClock _clock;
    private readonly GamingOptions _options;
    private readonly IActivityJournal? _journal;
    private readonly ILogger<GamingService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();
    private GamingState _state = GamingState.Inactive;
    private ActiveSession? _active;
    private GamingLiveMetrics? _liveMetrics;
    private TextRef? _frameCaptureError;
    private Task _autoRestoreTask = Task.CompletedTask;
    private int _disposed;

    public GamingService(
        IGameDetectionService detection,
        IRollbackManager rollback,
        IGamingSessionRepository sessions,
        ISettingsService settings,
        IPerformanceMonitor monitor,
        ISystemInfoProvider systemInfo,
        IProcessProvider processes,
        IFrameTimeSource frames,
        INotificationService notifications,
        WindowsGameSettingsChecker settingsChecker,
        IEnumerable<IOptimization> optimizations,
        IClock clock,
        GamingOptions? options = null,
        IActivityJournal? journal = null,
        ILogger<GamingService>? logger = null)
    {
        _detection = detection ?? throw new ArgumentNullException(nameof(detection));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _settingsChecker = settingsChecker ?? throw new ArgumentNullException(nameof(settingsChecker));
        ArgumentNullException.ThrowIfNull(optimizations);
        // Seuls les modules Gaming sont utilisés par la session (les autres modules IOptimization sont ignorés).
        _optimizations = optimizations.Where(o => GamingOptimizationIds.All.Contains(o.Id, StringComparer.Ordinal)).ToList();
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new GamingOptions();
        _journal = journal;
        _logger = logger ?? NullLogger<GamingService>.Instance;
        _detection.GameExited += OnGameExited;
    }

    public event EventHandler? StateChanged;

    public event EventHandler<GamingLiveMetrics>? LiveMetricsUpdated;

    public GamingState State
    {
        get { lock (_stateLock) return _state; }
    }

    public GamingSession? CurrentSession
    {
        get { lock (_stateLock) return _active?.Session; }
    }

    public DetectedGameProcess? CurrentGame
    {
        get { lock (_stateLock) return _active?.Game; }
    }

    public GamingLiveMetrics? LiveMetrics
    {
        get { lock (_stateLock) return _liveMetrics; }
    }

    /// <summary>Raison du dernier échec de démarrage ou de l'arrêt de la capture d'images (UAC refusé, assistant arrêté…).</summary>
    public TextRef? FrameCaptureError
    {
        get { lock (_stateLock) return _frameCaptureError; }
    }

    /// <summary>Restauration automatique en cours ou terminée (sortie du jeu).</summary>
    internal Task AutoRestoreTask
    {
        get { lock (_stateLock) return _autoRestoreTask; }
    }

    public IReadOnlyList<GameSettingCheck> CheckWindowsGameSettings(DetectedGameProcess? game) => _settingsChecker.Check(game);

    /// <summary>Applique la correction réversible d'un paramètre de jeu Windows (<see cref="GameSettingCheck.CanFixAutomatically"/>).</summary>
    public Task<OperationResult> FixWindowsGameSettingAsync(string checkId, DetectedGameProcess? game, CancellationToken cancellationToken = default)
        => _settingsChecker.FixAsync(checkId, game, cancellationToken);

    /// <summary>
    /// Aperçu : chaque module du plan avec <see cref="ActiveGamingOptimization.Applied"/> = sera appliqué avec la sélection
    /// par défaut, et le détail (modification prévue ou raison de non-application). Rien n'est modifié.
    /// </summary>
    public async Task<IReadOnlyList<ActiveGamingOptimization>> PreviewAsync(DetectedGameProcess? game, CancellationToken cancellationToken = default)
    {
        game ??= await _detection.DetectRunningGameAsync(cancellationToken).ConfigureAwait(false);
        var context = BuildContext(game);
        var result = new List<ActiveGamingOptimization>();
        foreach (var (id, enabled) in BuildPlan(_settings.Current.Gaming.ForGame(game?.Game.Id)))
        {
            var module = Find(id);
            if (module is null) continue;
            if (!enabled)
            {
                result.Add(new ActiveGamingOptimization(id, module.Name, false, TextRef.Of("Game_Opt_DisabledInSettings")));
                continue;
            }
            var preview = await SafePreviewAsync(module, context, cancellationToken).ConfigureAwait(false);
            result.Add(DescribePreview(module, preview, context));
        }
        return result;
    }

    public async Task<GamingActivationReport> ActivateAsync(DetectedGameProcess? game, CancellationToken cancellationToken = default)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new GamingActivationReport(null, OperationResult.Fail(OperationErrorKind.Cancelled), game, [], FrameCaptureAvailability.Disabled);
        }

        ActiveSession? session = null;
        IChangeRecorder? recorder = null;
        try
        {
            var existing = _active;
            if (existing is not null)
                return new GamingActivationReport(existing.Session.Id, OperationResult.Ok(TextRef.Of("Game_AlreadyActive")), existing.Game, existing.Session.Optimizations, existing.FrameCapture);

            SetState(GamingState.Activating);

            // 1) Profil matériel et charge actuelle (journal technique, aucune donnée personnelle).
            LogHardwareProfile();

            // 2) Jeu : paramètre ou détection.
            game ??= await _detection.DetectRunningGameAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset? gameStart = null;
            if (game is not null)
            {
                var process = _processes.GetProcess(game.ProcessId);
                if (process is null)
                {
                    SetState(GamingState.Inactive);
                    return new GamingActivationReport(null, OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Game_NotRunning", game.Game.Name)), game, [], FrameCaptureAvailability.Disabled);
                }
                gameStart = process.StartTime;
            }

            // 3) Plan selon les réglages, ceux du jeu remplaçant les réglages généraux (aperçus : rien n'est encore modifié).
            var settings = _settings.Current.Gaming.ForGame(game?.Game.Id);
            var context = BuildContext(game);
            var entries = new List<ActiveGamingOptimization>();
            var plan = new List<(IOptimization Module, OptimizationPreview? Preview)>();
            foreach (var (id, enabled) in BuildPlan(settings))
            {
                var module = Find(id);
                if (module is null) continue;
                if (!enabled)
                {
                    entries.Add(new ActiveGamingOptimization(id, module.Name, false, TextRef.Of("Game_Opt_DisabledInSettings")));
                    continue;
                }
                plan.Add((module, await SafePreviewAsync(module, context, cancellationToken).ConfigureAwait(false)));
            }

            // 4) Session de restauration + GamingSession « Active » persistée immédiatement (avant toute modification).
            var title = game is null ? TextRef.Of("Game_SessionTitle_NoGame") : TextRef.Of("Game_SessionTitle", game.Game.Name);
            recorder = await _rollback.BeginSessionAsync(SessionType.Gaming, title, null, cancellationToken).ConfigureAwait(false);
            var gamingSession = new GamingSession
            {
                Id = Guid.NewGuid(),
                StartedAt = _clock.UtcNow,
                Status = GamingSessionStatus.Active,
                GameId = game?.Game.Id,
                GameName = game?.Game.Name,
                ProcessId = game?.ProcessId,
                OptimizationSessionId = recorder.SessionId,
            };
            await _sessions.SaveAsync(gamingSession, cancellationToken).ConfigureAwait(false);
            session = new ActiveSession(gamingSession, recorder.SessionId, game, gameStart, _options.LiveFrameWindow);

            // 5) Application des modules (chaque modification est consignée avant d'être appliquée).
            foreach (var (module, preview) in plan)
                entries.Add(await ApplyModuleAsync(module, preview, context, recorder, cancellationToken).ConfigureAwait(false));
            var order = BuildPlan(settings).Select(p => p.Id).ToList();
            session.Session = session.Session with { Optimizations = entries.OrderBy(e => order.IndexOf(e.OptimizationId)).ToList() };
            await _sessions.SaveAsync(session.Session, CancellationToken.None).ConfigureAwait(false);

            // 6) Surveillance : moniteur actif, capture d'images si autorisée, métriques en direct.
            StartMonitoring(session, settings);
            session.FrameCapture = await StartFrameCaptureAsync(session, settings, cancellationToken).ConfigureAwait(false);

            lock (_stateLock) _active = session;
            StartLoop(session);
            SetState(GamingState.Active);
            _logger.LogInformation("Mode Gaming activé ({Applied} optimisation(s) appliquée(s), capture d'images : {Capture})",
                session.Session.Optimizations.Count(o => o.Applied), session.FrameCapture);
            await JournalAsync(game is null ? TextRef.Of("Game_Journal_ActivatedNoGame") : TextRef.Of("Game_Journal_Activated", game.Game.Name)).ConfigureAwait(false);
            return new GamingActivationReport(session.Session.Id, OperationResult.Ok(), game, session.Session.Optimizations, session.FrameCapture);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AbortActivationAsync(session, recorder).ConfigureAwait(false);
            return new GamingActivationReport(session?.Session.Id, OperationResult.Fail(OperationErrorKind.Cancelled), game, session?.Session.Optimizations ?? [], FrameCaptureAvailability.Disabled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Activation du mode Gaming impossible : restauration de ce qui a été modifié");
            await AbortActivationAsync(session, recorder).ConfigureAwait(false);
            return new GamingActivationReport(session?.Session.Id, OperationResult.FromException(ex), game, session?.Session.Optimizations ?? [], FrameCaptureAvailability.Disabled);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<GamingRestoreReport> DeactivateAsync(CancellationToken cancellationToken = default)
        => DeactivateCoreAsync(expected: null, cancellationToken);

    /// <summary>Désactive la session <paramref name="expected"/> seulement si elle est toujours la session courante.</summary>
    private async Task<GamingRestoreReport> DeactivateCoreAsync(ActiveSession? expected, CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new GamingRestoreReport(null, 0, 0, [TextRef.Of("Game_Restore_Cancelled")]);
        }

        try
        {
            var session = _active;
            if (session is null || (expected is not null && !ReferenceEquals(session, expected)))
                return new GamingRestoreReport(null, 0, 0, []); // Déjà inactif : idempotent.

            SetState(GamingState.Restoring);
            // À partir d'ici, la restauration va jusqu'au bout (jamais interrompue à mi-chemin).
            session.StopLoop();
            var frameStats = await StopFrameCaptureAsync(session).ConfigureAwait(false);
            var rollback = await RestoreChangesAsync(session.OptimizationSessionId).ConfigureAwait(false);

            var final = session.Session with
            {
                EndedAt = _clock.UtcNow,
                Status = rollback.Success ? GamingSessionStatus.Restored : GamingSessionStatus.RestoreFailed,
                FrameStats = frameStats.HasData ? frameStats : null,
            };
            try
            {
                await _sessions.SaveAsync(final, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Session Gaming : état final non enregistré");
            }

            StopMonitoring(session);
            lock (_stateLock)
            {
                _active = null;
                _liveMetrics = null;
            }

            var messages = rollback.Errors.Where(e => e.Message is not null).Select(e => e.Message!).ToList();
            _notifications.Show(new NotificationRequest(
                TextRef.Of("Game_Deactivated_Title"),
                rollback.Success ? TextRef.Of("Game_Deactivated_Body") : TextRef.Of("Game_Deactivated_PartialBody", rollback.Failed),
                Tag: GamingActions.NotificationTag));
            _logger.LogInformation("Mode Gaming désactivé : {Restored} modification(s) restaurée(s), {Failed} échec(s)", rollback.Restored, rollback.Failed);
            await JournalAsync(rollback.Success
                ? TextRef.Of("Game_Journal_Restored", rollback.Restored)
                : TextRef.Of("Game_Journal_RestoreFailed", rollback.Failed)).ConfigureAwait(false);
            SetState(GamingState.Inactive);
            return new GamingRestoreReport(final.Id, rollback.Restored, rollback.Failed, messages);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RollbackResult> RestoreChangesAsync(Guid optimizationSessionId)
    {
        try
        {
            await _rollback.CompleteSessionAsync(optimizationSessionId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Session de restauration Gaming : clôture impossible, restauration tentée malgré tout");
        }
        try
        {
            return await _rollback.RestoreSessionAsync(optimizationSessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Restauration de la session Gaming impossible");
            return new RollbackResult(0, 1, 0, [OperationResult.FromException(ex) with { Message = TextRef.Of("Game_Restore_Failed") }]);
        }
    }

    /// <summary>Activation interrompue : ce qui a déjà été modifié est restauré, l'état revient à Inactive.</summary>
    private async Task AbortActivationAsync(ActiveSession? session, IChangeRecorder? recorder)
    {
        try
        {
            if (session is not null)
            {
                session.StopLoop();
                await StopFrameCaptureAsync(session).ConfigureAwait(false);
                StopMonitoring(session);
            }
            if (recorder is not null)
            {
                var rollback = await RestoreChangesAsync(recorder.SessionId).ConfigureAwait(false);
                if (session is not null)
                {
                    var final = session.Session with
                    {
                        EndedAt = _clock.UtcNow,
                        Status = rollback.Success ? GamingSessionStatus.Restored : GamingSessionStatus.RestoreFailed,
                    };
                    await _sessions.SaveAsync(final, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Annulation de l'activation du mode Gaming incomplète");
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_active, session)) _active = null;
                _liveMetrics = null;
            }
            SetState(GamingState.Inactive);
        }
    }

    private async Task<ActiveGamingOptimization> ApplyModuleAsync(IOptimization module, OptimizationPreview? preview, OptimizationContext context,
        IChangeRecorder recorder, CancellationToken cancellationToken)
    {
        if (preview is null) return new ActiveGamingOptimization(module.Id, module.Name, false, TextRef.Of("Game_Opt_Failed"));
        if (!preview.Applicable) return new ActiveGamingOptimization(module.Id, module.Name, false, preview.NotApplicableReason);
        try
        {
            var result = await module.ApplyAsync(context, recorder, cancellationToken).ConfigureAwait(false);
            var detail = result.Messages.FirstOrDefault() ?? result.Outcome.Message;
            return new ActiveGamingOptimization(module.Id, module.Name, result.ChangesApplied > 0, detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Module Gaming {Module} en erreur : ignoré", module.Id);
            return new ActiveGamingOptimization(module.Id, module.Name, false, TextRef.Of("Game_Opt_Failed"));
        }
    }

    private async Task<OptimizationPreview?> SafePreviewAsync(IOptimization module, OptimizationContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await module.PreviewAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Aperçu du module Gaming {Module} en erreur", module.Id);
            return null;
        }
    }

    private static ActiveGamingOptimization DescribePreview(IOptimization module, OptimizationPreview? preview, OptimizationContext context)
    {
        if (preview is null) return new ActiveGamingOptimization(module.Id, module.Name, false, TextRef.Of("Game_Opt_Failed"));
        if (!preview.Applicable) return new ActiveGamingOptimization(module.Id, module.Name, false, preview.NotApplicableReason);
        var selected = preview.Changes.Where(context.IsSelected).ToList();
        if (selected.Count == 0)
            return new ActiveGamingOptimization(module.Id, module.Name, false, preview.Changes.FirstOrDefault()?.Description ?? TextRef.Of("Game_Opt_NotSelectedByDefault"));
        return new ActiveGamingOptimization(module.Id, module.Name, true,
            selected.Count == 1 ? selected[0].Description : TextRef.Of("Game_Opt_ChangeCount", selected.Count));
    }

    private static IReadOnlyList<(string Id, bool Enabled)> BuildPlan(GamingSettings settings) =>
    [
        (GamingOptimizationIds.Power, settings.SwitchPowerPlan),
        (GamingOptimizationIds.Priority, settings.RaiseGamePriority),
        (GamingOptimizationIds.Background, settings.ThrottleBackgroundApps),
    ];

    private IOptimization? Find(string id) => _optimizations.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal));

    private static OptimizationContext BuildContext(DetectedGameProcess? game)
    {
        var context = new OptimizationContext(null);
        if (game is null) return context;
        context.Items[GamingContextKeys.GameProcessId] = game.ProcessId;
        var path = game.ExecutablePath ?? game.Game.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(path)) context.Items[GamingContextKeys.GameExecutablePath] = path;
        if (!string.IsNullOrWhiteSpace(game.Game.InstallDirectory)) context.Items[GamingContextKeys.GameInstallDirectory] = game.Game.InstallDirectory;
        return context;
    }

    private void LogHardwareProfile()
    {
        try
        {
            var cpu = _systemInfo.GetCpuInfo();
            var memory = _systemInfo.GetMemoryInfo();
            var gpus = _systemInfo.GetGpus();
            var power = _systemInfo.GetPowerStatus();
            var latest = _monitor.Latest;
            _logger.LogInformation(
                "Mode Gaming : {Logical} processeurs logiques, {RamGiB:F1} Gio de RAM, {GpuCount} GPU, alimentation {Power}, charge CPU {Cpu} %, RAM {Ram} %",
                cpu.LogicalProcessors, memory.TotalBytes / (double)ByteSize.GiB, gpus.Count(g => !g.IsSoftwareAdapter), power.Source,
                latest?.CpuPercent.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) ?? "?",
                latest?.MemoryUsedPercent.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) ?? "?");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Profil matériel indisponible");
        }
    }

    private void StartMonitoring(ActiveSession session, GamingSettings settings)
    {
        if (!settings.MonitorDuringSession) return;
        try
        {
            session.PreviousMonitorMode = _monitor.Mode;
            if (!_monitor.IsRunning)
            {
                _monitor.Start();
                session.StartedMonitor = true;
            }
            if (_monitor.Mode != MonitoringMode.Active)
            {
                _monitor.SetMode(MonitoringMode.Active);
                session.ChangedMonitorMode = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Surveillance pendant la session Gaming impossible à démarrer");
        }
    }

    /// <summary>Rend le moniteur dans l'état trouvé, seulement si la session l'avait changé (et que personne ne l'a changé depuis).</summary>
    private void StopMonitoring(ActiveSession session)
    {
        try
        {
            if (session.ChangedMonitorMode && _monitor.Mode == MonitoringMode.Active) _monitor.SetMode(session.PreviousMonitorMode);
            if (session.StartedMonitor) _monitor.Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Moniteur non remis dans son état précédent");
        }
        session.ChangedMonitorMode = false;
        session.StartedMonitor = false;
    }

    private async Task<FrameCaptureAvailability> StartFrameCaptureAsync(ActiveSession session, GamingSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.MeasureFrameRate) return FrameCaptureAvailability.Disabled;
        if (session.Game is null) return FrameCaptureAvailability.NotSupported;

        var availability = _frames.GetAvailability();
        if (availability != FrameCaptureAvailability.Available) return availability;

        IFrameCaptureSession? capture;
        try
        {
            capture = await _frames.StartAsync(session.Game.ProcessId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Capture d'images impossible à démarrer");
            lock (_stateLock) _frameCaptureError = TextRef.Of("Game_Frames_Unavailable");
            return FrameCaptureAvailability.NotSupported;
        }

        if (capture is null)
        {
            // Autorisation administrateur refusée ou non obtenue : FPS « non disponibles », jamais estimés.
            lock (_stateLock) _frameCaptureError = _frames.LastError ?? TextRef.Of("Game_Frames_Unavailable");
            return FrameCaptureAvailability.RequiresElevation;
        }

        lock (_stateLock) _frameCaptureError = null;
        session.AttachCapture(capture, _clock);
        return FrameCaptureAvailability.Available;
    }

    private async Task<FrameStats> StopFrameCaptureAsync(ActiveSession session)
    {
        var capture = session.DetachCapture();
        if (capture is not null)
        {
            try
            {
                await capture.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Arrêt de la capture d'images incomplet");
            }
        }
        return session.ComputeSessionFrames();
    }

    private void StartLoop(ActiveSession session)
    {
        var cts = new CancellationTokenSource();
        session.LoopCancellation = cts;
        _ = Task.Run(() => LoopAsync(session, cts), CancellationToken.None);
    }

    private async Task LoopAsync(ActiveSession session, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            using var timer = new PeriodicTimer(_options.LiveMetricsInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(session).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Mise à jour des métriques Gaming en erreur");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session terminée.
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>Un cycle de surveillance de la session courante (métriques en direct, sortie du jeu).</summary>
    internal Task TickAsync()
    {
        ActiveSession? session;
        lock (_stateLock) session = _active;
        return session is null ? Task.CompletedTask : TickAsync(session);
    }

    private async Task TickAsync(ActiveSession session)
    {
        lock (_stateLock)
        {
            if (!ReferenceEquals(_active, session)) return;
        }
        UpdateLiveMetrics(session);
        if (session.Game is { } game && !_processes.IsRunning(game.ProcessId, session.GameStartTime))
            await HandleGameExitAsync(session).ConfigureAwait(false);
    }

    private void UpdateLiveMetrics(ActiveSession session)
    {
        var sample = _monitor.Latest;
        if (sample is null) return; // Aucune mesure disponible : rien n'est inventé.

        var frames = session.ComputeLiveFrames(_clock.UtcNow, _options.FrameStaleAfter, out var captureError);
        if (captureError is not null)
        {
            lock (_stateLock) _frameCaptureError = captureError;
        }
        var temperatures = _monitor.Temperatures;
        var metrics = new GamingLiveMetrics(
            frames,
            sample.CpuPercent,
            sample.GpuPercent,
            sample.MemoryUsedPercent,
            sample.GpuDedicatedMemoryUsedBytes,
            temperatures.Cpu,
            temperatures.Gpu,
            session.FrameCapture);
        lock (_stateLock)
        {
            if (!ReferenceEquals(_active, session)) return;
            _liveMetrics = metrics;
        }
        Raise(LiveMetricsUpdated, metrics);
    }

    private void OnGameExited(object? sender, DetectedGameProcess game)
    {
        ActiveSession? session;
        lock (_stateLock) session = _active;
        if (session?.Game is null || session.Game.ProcessId != game.ProcessId) return;
        var task = Task.Run(() => HandleGameExitAsync(session));
        lock (_stateLock) _autoRestoreTask = task;
    }

    /// <summary>Sortie du jeu (une seule fois par session) : restauration automatique si l'utilisateur l'a choisie.</summary>
    private async Task HandleGameExitAsync(ActiveSession session)
    {
        if (!session.TryMarkGameExited()) return;
        _logger.LogInformation("Mode Gaming : le jeu s'est fermé");
        try
        {
            if (_settings.Current.Gaming.AutoRestore)
            {
                await DeactivateCoreAsync(session, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await StopFrameCaptureAsync(session).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Restauration automatique après la fermeture du jeu en erreur");
        }
    }

    private void SetState(GamingState state)
    {
        lock (_stateLock)
        {
            if (_state == state) return;
            _state = state;
        }
        Raise(StateChanged);
    }

    private void Raise(EventHandler? handler)
    {
        if (handler is null) return;
        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler>())
        {
            try { subscriber(this, EventArgs.Empty); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _logger.LogWarning(ex, "Abonné StateChanged en erreur"); }
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null) return;
        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try { subscriber(this, args); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _logger.LogWarning(ex, "Abonné aux métriques Gaming en erreur"); }
        }
    }

    private async Task JournalAsync(TextRef message)
    {
        if (_journal is null) return;
        try
        {
            await _journal.LogAsync(ActivityKind.Gaming, message, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Journal d'activité indisponible");
        }
    }

    /// <summary>Arrêt de l'application : les réglages modifiés sont restaurés avant la fermeture.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _detection.GameExited -= OnGameExited;
        if (CurrentSession is not null) await DeactivateAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Libération synchrone : arrête la surveillance sans restaurer (la session de restauration reste « en cours » et sera
    /// proposée par la récupération au prochain démarrage). Préférer <see cref="DisposeAsync"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _detection.GameExited -= OnGameExited;
        ActiveSession? session;
        lock (_stateLock) session = _active;
        if (session is null) return;
        session.StopLoop();
        var capture = session.DetachCapture();
        if (capture is not null) _ = capture.DisposeAsync().AsTask();
    }

    /// <summary>État d'une session Gaming en cours.</summary>
    private sealed class ActiveSession
    {
        private readonly Lock _frameLock = new();
        private readonly RollingFrameWindow _window;
        private readonly FrameStatsAccumulator _accumulator = new();
        private IFrameCaptureSession? _capture;
        private EventHandler<FrameBatch>? _handler;
        private DateTimeOffset? _lastFrameAt;
        private int _gameExited;

        public ActiveSession(GamingSession session, Guid optimizationSessionId, DetectedGameProcess? game, DateTimeOffset? gameStartTime, TimeSpan frameWindow)
        {
            Session = session;
            OptimizationSessionId = optimizationSessionId;
            Game = game;
            GameStartTime = gameStartTime;
            _window = new RollingFrameWindow(frameWindow);
        }

        public GamingSession Session { get; set; }
        public Guid OptimizationSessionId { get; }
        public DetectedGameProcess? Game { get; }
        public DateTimeOffset? GameStartTime { get; }
        public FrameCaptureAvailability FrameCapture { get; set; } = FrameCaptureAvailability.Disabled;
        public MonitoringMode PreviousMonitorMode { get; set; }
        public bool StartedMonitor { get; set; }
        public bool ChangedMonitorMode { get; set; }
        public CancellationTokenSource? LoopCancellation { get; set; }

        public bool TryMarkGameExited() => Interlocked.Exchange(ref _gameExited, 1) == 0;

        public void StopLoop()
        {
            var cts = LoopCancellation;
            LoopCancellation = null;
            try { cts?.Cancel(); }
            catch (ObjectDisposedException) { /* boucle déjà terminée */ }
        }

        public void AttachCapture(IFrameCaptureSession capture, IClock clock)
        {
            var pid = Game?.ProcessId;
            _handler = (_, batch) =>
            {
                if (batch.ProcessId != pid) return;
                lock (_frameLock)
                {
                    _window.Add(batch.PresentTimestampsMs);
                    _accumulator.Add(batch.PresentTimestampsMs);
                    _lastFrameAt = clock.UtcNow;
                }
            };
            capture.FramesReceived += _handler;
            _capture = capture;
        }

        public IFrameCaptureSession? DetachCapture()
        {
            lock (_frameLock)
            {
                var capture = _capture;
                _capture = null;
                if (capture is not null && _handler is not null) capture.FramesReceived -= _handler;
                return capture;
            }
        }

        /// <summary>FPS de la fenêtre glissante, ou <see cref="FrameStats.Empty"/> si la capture est arrêtée ou sans image récente.</summary>
        public FrameStats ComputeLiveFrames(DateTimeOffset now, TimeSpan staleAfter, out TextRef? captureError)
        {
            lock (_frameLock)
            {
                captureError = _capture is { IsActive: false } ? _capture.Error : null;
                if (_capture is not { IsActive: true } || _lastFrameAt is not { } last || now - last > staleAfter) return FrameStats.Empty;
                return _window.Compute();
            }
        }

        public FrameStats ComputeSessionFrames()
        {
            lock (_frameLock) return _accumulator.Compute();
        }
    }
}
