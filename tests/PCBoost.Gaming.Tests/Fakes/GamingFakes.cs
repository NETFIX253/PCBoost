using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.TestUtilities;

namespace PCBoost.Gaming.Tests.Fakes;

/// <summary>Consignation write-ahead en mémoire : la modification est enregistrée (Pending) AVANT l'exécution.</summary>
public sealed class FakeChangeRecorder : IChangeRecorder
{
    private readonly FakeRollbackManager? _owner;

    public FakeChangeRecorder(Guid sessionId, FakeRollbackManager? owner = null)
    {
        SessionId = sessionId;
        _owner = owner;
    }

    public Guid SessionId { get; }

    public List<ChangeRecord> Changes { get; } = [];

    /// <summary>Appelé juste avant l'exécution d'une modification (vérifications d'ordre).</summary>
    public Action<PendingChange>? BeforeApply { get; set; }

    public async Task<OperationResult> ApplyAsync(PendingChange change, Func<CancellationToken, Task<OperationResult>> apply, CancellationToken cancellationToken = default)
    {
        var record = new ChangeRecord
        {
            Id = Guid.NewGuid(),
            SessionId = SessionId,
            OptimizationId = change.OptimizationId,
            Kind = change.Kind,
            Target = change.Target,
            Description = change.Description,
            BeforeState = change.BeforeState,
            Reversible = change.Reversible,
            Status = ChangeStatus.Pending,
            RecordedAt = DateTimeOffset.UtcNow,
            Sequence = Changes.Count + 1,
        };
        lock (Changes) Changes.Add(record);
        (BeforeApply ?? _owner?.BeforeApply)?.Invoke(change);
        OperationResult outcome;
        try
        {
            outcome = await apply(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = OperationResult.FromException(ex);
        }
        Update(record.Id, r => r with { Status = outcome.Success ? ChangeStatus.Applied : ChangeStatus.Failed, ErrorDetail = outcome.TechnicalDetail });
        return outcome;
    }

    public Task RecordIrreversibleAsync(PendingChange change, OperationResult outcome, long bytesFreed, CancellationToken cancellationToken = default)
    {
        lock (Changes)
        {
            Changes.Add(new ChangeRecord
            {
                Id = Guid.NewGuid(), SessionId = SessionId, OptimizationId = change.OptimizationId, Kind = change.Kind, Target = change.Target,
                Description = change.Description, Reversible = false, Status = ChangeStatus.Irreversible, RecordedAt = DateTimeOffset.UtcNow,
                Sequence = Changes.Count + 1,
            });
        }
        return Task.CompletedTask;
    }

    public void Update(Guid id, Func<ChangeRecord, ChangeRecord> update)
    {
        lock (Changes)
        {
            var i = Changes.FindIndex(c => c.Id == id);
            if (i >= 0) Changes[i] = update(Changes[i]);
        }
    }
}

/// <summary>
/// Gestionnaire de restauration en mémoire avec de vrais gestionnaires d'annulation minimaux (alimentation, priorité,
/// mode efficacité, registre) branchés sur les faux fournisseurs.
/// </summary>
public sealed class FakeRollbackManager : IRollbackManager
{
    private readonly Dictionary<Guid, (OptimizationSession Session, FakeChangeRecorder Recorder)> _sessions = new();

    public FakePowerProvider? Power { get; set; }
    public FakeProcessProvider? Processes { get; set; }
    public InMemoryRegistryProvider? Registry { get; set; }

    public List<Guid> RestoredSessions { get; } = [];
    public List<Guid> CompletedSessions { get; } = [];
    public List<Guid> UndoneChanges { get; } = [];
    public bool FailRestore { get; set; }
    public Action<PendingChange>? BeforeApply { get; set; }
    public Action<SessionType>? OnBeginSession { get; set; }

    public event EventHandler<Guid>? SessionChanged;

    public IReadOnlyList<OptimizationSession> Sessions => _sessions.Values.Select(v => Hydrate(v.Session, v.Recorder)).ToList();

    public FakeChangeRecorder GetRecorder(Guid sessionId) => _sessions[sessionId].Recorder;

    public Task<IChangeRecorder> BeginSessionAsync(SessionType type, TextRef title, string? profileId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnBeginSession?.Invoke(type);
        var id = Guid.NewGuid();
        var recorder = new FakeChangeRecorder(id, this);
        _sessions[id] = (new OptimizationSession { Id = id, Type = type, StartedAt = DateTimeOffset.UtcNow, Status = SessionStatus.InProgress, Title = title, ProfileId = profileId }, recorder);
        SessionChanged?.Invoke(this, id);
        return Task.FromResult<IChangeRecorder>(recorder);
    }

    public Task CompleteSessionAsync(Guid sessionId, long bytesFreed = 0, bool requiresRestart = false, CancellationToken cancellationToken = default)
    {
        CompletedSessions.Add(sessionId);
        var (s, r) = _sessions[sessionId];
        _sessions[sessionId] = (s with { Status = SessionStatus.Completed, CompletedAt = DateTimeOffset.UtcNow }, r);
        return Task.CompletedTask;
    }

    public Task<OperationResult> UndoChangeAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        foreach (var (_, recorder) in _sessions.Values)
        {
            var change = recorder.Changes.FirstOrDefault(c => c.Id == changeId);
            if (change is null) continue;
            var result = Undo(change);
            recorder.Update(changeId, c => c with { Status = result.Success ? ChangeStatus.RolledBack : ChangeStatus.RollbackFailed });
            UndoneChanges.Add(changeId);
            return Task.FromResult(result);
        }
        return Task.FromResult(OperationResult.Fail(OperationErrorKind.NotFound));
    }

    public async Task<RollbackResult> RestoreSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        RestoredSessions.Add(sessionId);
        if (FailRestore) return new RollbackResult(0, 1, 0, [OperationResult.Fail(OperationErrorKind.AccessDenied, TextRef.Of("Test_RestoreFailed"))]);
        var (session, recorder) = _sessions[sessionId];
        int restored = 0, failed = 0;
        var errors = new List<OperationResult>();
        foreach (var change in recorder.Changes.Where(c => c.Reversible && c.Status is ChangeStatus.Applied or ChangeStatus.Pending).OrderByDescending(c => c.Sequence).ToList())
        {
            var r = await UndoChangeAsync(change.Id, cancellationToken);
            if (r.Success) restored++; else { failed++; errors.Add(r); }
        }
        _sessions[sessionId] = (session with { Status = failed == 0 ? SessionStatus.RolledBack : SessionStatus.PartiallyRolledBack }, recorder);
        return new RollbackResult(restored, failed, 0, errors);
    }

    public bool CanRollback(OptimizationSession session) => session.ReversibleChangeCount > 0;

    public Task<IReadOnlyList<OptimizationSession>> GetHistoryAsync(int limit = 100, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OptimizationSession>>(Sessions.Take(limit).ToList());

    public Task<OptimizationSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(_sessions.TryGetValue(sessionId, out var v) ? Hydrate(v.Session, v.Recorder) : null);

    private static OptimizationSession Hydrate(OptimizationSession s, FakeChangeRecorder r)
    {
        lock (r.Changes) return s with { Changes = r.Changes.ToList() };
    }

    private OperationResult Undo(ChangeRecord change)
    {
        switch (change.Kind)
        {
            case ChangeKinds.PowerScheme when Power is not null:
                var power = ChangeStateSerializer.Deserialize<PowerSchemeState>(change.BeforeState)!;
                return Power.SetActiveScheme(power.SchemeId);
            case ChangeKinds.ProcessPriority when Processes is not null:
                var priority = ChangeStateSerializer.Deserialize<ProcessPriorityState>(change.BeforeState)!;
                return Processes.IsRunning(priority.ProcessId) ? Processes.SetPriority(priority.ProcessId, priority.Priority) : OperationResult.Ok();
            case ChangeKinds.ProcessEfficiency when Processes is not null:
                var efficiency = ChangeStateSerializer.Deserialize<ProcessEfficiencyState>(change.BeforeState)!;
                return Processes.IsRunning(efficiency.ProcessId) ? Processes.SetEfficiencyMode(efficiency.ProcessId, efficiency.Enabled) : OperationResult.Ok();
            case ChangeKinds.RegistryValue when Registry is not null:
                var reg = ChangeStateSerializer.Deserialize<RegistryValueState>(change.BeforeState)!;
                var data = reg.ToData();
                return data is null ? Registry.DeleteValue(reg.Location, reg.ValueName) : Registry.SetValue(reg.Location, reg.ValueName, data);
            default:
                return OperationResult.Ok();
        }
    }
}

public sealed class FakePerformanceMonitor : IPerformanceMonitor
{
    private readonly List<SystemMetricsSample> _history = [];

    public MonitoringMode Mode { get; private set; } = MonitoringMode.Background;
    public bool IsRunning { get; private set; }
    public SystemMetricsSample? Latest { get; set; }
    public TemperatureReadings Temperatures { get; set; } = TemperatureReadings.None;
    public TimeSpan CurrentInterval => Mode == MonitoringMode.Active ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
    public List<MonitoringMode> ModeChanges { get; } = [];
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }

    public event EventHandler<SystemMetricsSample>? SampleAvailable;
    public event EventHandler? ModeChanged;

    public void Start() { IsRunning = true; StartCalls++; }
    public void Stop() { IsRunning = false; StopCalls++; }

    public void SetMode(MonitoringMode mode)
    {
        Mode = mode;
        ModeChanges.Add(mode);
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetRunning(bool running, MonitoringMode mode)
    {
        IsRunning = running;
        Mode = mode;
    }

    public void Publish(SystemMetricsSample sample)
    {
        Latest = sample;
        lock (_history) _history.Add(sample);
        SampleAvailable?.Invoke(this, sample);
    }

    public IReadOnlyList<SystemMetricsSample> GetHistory(TimeSpan window)
    {
        lock (_history) return _history.ToList();
    }

    public MetricStatistics GetStatistics(MetricKind metric, TimeSpan window) => new(metric, null, null, null, 0);

    public void Dispose() { }

    public static SystemMetricsSample Sample(DateTimeOffset at, double cpu, double ram, double? gpu = null, double? disk = null)
        => new(at, cpu, ram, (long)(8L * ByteSize.GiB * ram / 100), 8L * ByteSize.GiB, disk, 0, 0, gpu, null, 0, 0, 150);
}

public sealed class FakeFrameCaptureSession : IFrameCaptureSession
{
    public FakeFrameCaptureSession(int processId) => ProcessId = processId;

    public int ProcessId { get; }
    public bool IsActive { get; set; } = true;
    public TextRef? Error { get; set; }
    public bool Disposed { get; private set; }

    public event EventHandler<FrameBatch>? FramesReceived;

    public void Push(params double[] timestamps) => FramesReceived?.Invoke(this, new FrameBatch(ProcessId, timestamps));

    public bool HasSubscribers => FramesReceived is not null;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        IsActive = false;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeFrameTimeSource : IFrameTimeSource
{
    public FrameCaptureAvailability Availability { get; set; } = FrameCaptureAvailability.Available;
    /// <summary>False : StartAsync renvoie null (UAC refusé).</summary>
    public bool Grant { get; set; } = true;
    public List<int> StartRequests { get; } = [];
    public FakeFrameCaptureSession? LastSession { get; private set; }
    public TextRef? LastError { get; private set; }

    public FrameCaptureAvailability GetAvailability() => Availability;

    public Task<IFrameCaptureSession?> StartAsync(int processId, CancellationToken cancellationToken = default)
    {
        StartRequests.Add(processId);
        if (!Grant)
        {
            LastError = TextRef.Of("Sys_FrameCaptureCancelled");
            return Task.FromResult<IFrameCaptureSession?>(null);
        }
        LastError = null;
        LastSession = new FakeFrameCaptureSession(processId);
        return Task.FromResult<IFrameCaptureSession?>(LastSession);
    }
}

public sealed class FakeForegroundWindowProvider : IForegroundWindowProvider
{
    public int? ForegroundProcessId { get; set; }
    public bool Fullscreen { get; set; }
    public int? GetForegroundProcessId() => ForegroundProcessId;
    public bool IsForegroundFullscreen() => Fullscreen;
}

/// <summary>Service de détection scripté (tests du mode Gaming et du mode automatique).</summary>
public sealed class FakeGameDetectionService : IGameDetectionService
{
    public DetectedGameProcess? Running { get; set; }
    public List<GameInfo> Installed { get; } = [];
    public int StartWatchingCalls { get; private set; }
    public int StopWatchingCalls { get; private set; }
    public bool IsWatching { get; private set; }

    public event EventHandler<DetectedGameProcess>? GameStarted;
    public event EventHandler<DetectedGameProcess>? GameExited;

    public Task<IReadOnlyList<GameInfo>> GetInstalledGamesAsync(bool refresh = false, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GameInfo>>(Installed);

    public Task<DetectedGameProcess?> DetectRunningGameAsync(CancellationToken cancellationToken = default) => Task.FromResult(Running);

    public void StartWatching() { StartWatchingCalls++; IsWatching = true; }
    public void StopWatching() { StopWatchingCalls++; IsWatching = false; }

    public void RaiseStarted(DetectedGameProcess game) => GameStarted?.Invoke(this, game);
    public void RaiseExited(DetectedGameProcess game) => GameExited?.Invoke(this, game);

    public void Dispose() { }
}

/// <summary>Mode Gaming scripté (tests du mode automatique).</summary>
public sealed class FakeGamingService : IGamingService
{
    public GamingState State { get; set; } = GamingState.Inactive;
    public GamingSession? CurrentSession { get; set; }
    public DetectedGameProcess? CurrentGame { get; set; }
    public GamingLiveMetrics? LiveMetrics => null;
    public List<DetectedGameProcess?> Activations { get; } = [];
    public int Deactivations { get; private set; }

    public event EventHandler? StateChanged;
    public event EventHandler<GamingLiveMetrics>? LiveMetricsUpdated;

    public Task<IReadOnlyList<ActiveGamingOptimization>> PreviewAsync(DetectedGameProcess? game, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ActiveGamingOptimization>>([]);

    public Task<GamingActivationReport> ActivateAsync(DetectedGameProcess? game, CancellationToken cancellationToken = default)
    {
        lock (Activations) Activations.Add(game);
        State = GamingState.Active;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new GamingActivationReport(Guid.NewGuid(), OperationResult.Ok(), game, [], FrameCaptureAvailability.Disabled));
    }

    public Task<GamingRestoreReport> DeactivateAsync(CancellationToken cancellationToken = default)
    {
        Deactivations++;
        State = GamingState.Inactive;
        return Task.FromResult(new GamingRestoreReport(null, 0, 0, []));
    }

    public IReadOnlyList<GameSettingCheck> CheckWindowsGameSettings(DetectedGameProcess? game) => [];

    public void RaiseMetrics(GamingLiveMetrics metrics) => LiveMetricsUpdated?.Invoke(this, metrics);
}

/// <summary>Fournisseur de processus qui compte les lectures de chemin (vérifie le cache par PID).</summary>
public sealed class CountingProcessProvider(FakeProcessProvider inner) : IProcessProvider
{
    public Dictionary<int, int> PathLookups { get; } = new();
    public int IdentityCalls { get; private set; }
    public int FullSnapshotCalls { get; private set; }

    public IReadOnlyList<ProcessSnapshot> GetProcesses() { FullSnapshotCalls++; return inner.GetProcesses(); }
    public IReadOnlyList<ProcessIdentity> GetProcessIdentities() { IdentityCalls++; return inner.GetProcessIdentities(); }
    public ProcessSnapshot? GetProcess(int processId) => inner.GetProcess(processId);
    public string? GetExecutablePath(int processId)
    {
        PathLookups[processId] = PathLookups.GetValueOrDefault(processId) + 1;
        return inner.GetExecutablePath(processId);
    }
    public bool IsRunning(int processId, DateTimeOffset? expectedStartTime = null) => inner.IsRunning(processId, expectedStartTime);
    public int CurrentProcessId => inner.CurrentProcessId;
    public int CurrentSessionId => inner.CurrentSessionId;
}

/// <summary>Fournisseur de services minimal pour la résolution différée du gestionnaire de restauration.</summary>
public sealed class SimpleServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = new();

    public SimpleServiceProvider Add<T>(T instance) where T : class
    {
        _services[typeof(T)] = instance;
        return this;
    }

    public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);
}
