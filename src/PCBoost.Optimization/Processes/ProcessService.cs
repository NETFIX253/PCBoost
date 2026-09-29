using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Processes;

/// <summary>
/// Gestionnaire de processus (§13) : CPU % = Δ temps processeur / (Δ temps × processeurs logiques) entre deux instantanés,
/// disque = Δ octets lus+écrits / s. Confiance via <see cref="ISecurityService"/>, protection via <see cref="ICriticalProcessProtection"/>.
/// Les processus critiques et PCBoost lui-même ne sont jamais fermés ni terminés.
/// </summary>
public sealed class ProcessService : IProcessService
{
    internal static readonly TimeSpan InitialSampleDelay = TimeSpan.FromMilliseconds(500);
    /// <summary>Au-delà, l'instantané précédent est trop ancien : un nouvel échantillonnage court est fait.</summary>
    internal static readonly TimeSpan MaxSampleAge = TimeSpan.FromSeconds(30);

    private readonly IProcessProvider _processes;
    private readonly IProcessControl _control;
    private readonly ISecurityService _security;
    private readonly ICriticalProcessProtection _protection;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly IActivityJournal _journal;
    private readonly IClock _clock;
    private readonly ILogger<ProcessService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _sampleLock = new(1, 1);
    private Dictionary<(int Pid, DateTimeOffset? Start), (TimeSpan Cpu, long Io)>? _previous;
    private DateTimeOffset _previousAt;
    private int _logicalProcessors;

    public ProcessService(IProcessProvider processes, IProcessControl control, ISecurityService security, ICriticalProcessProtection protection,
        ISystemInfoProvider systemInfo, IActivityJournal journal, IClock clock, ILogger<ProcessService>? logger = null)
        : this(processes, control, security, protection, systemInfo, journal, clock, logger, null)
    {
    }

    internal ProcessService(IProcessProvider processes, IProcessControl control, ISecurityService security, ICriticalProcessProtection protection,
        ISystemInfoProvider systemInfo, IActivityJournal journal, IClock clock, ILogger<ProcessService>? logger, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _processes = processes;
        _control = control;
        _security = security;
        _protection = protection;
        _systemInfo = systemInfo;
        _journal = journal;
        _clock = clock;
        _logger = logger ?? NullLogger<ProcessService>.Instance;
        _delay = delay ?? Task.Delay;
    }

    public async Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken cancellationToken = default)
    {
        await _sampleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_previous is null || _clock.UtcNow - _previousAt > MaxSampleAge)
            {
                Remember(_processes.GetProcesses(), _clock.UtcNow);
                await _delay(InitialSampleDelay, cancellationToken).ConfigureAwait(false);
            }

            var now = _clock.UtcNow;
            var snapshots = _processes.GetProcesses();
            var elapsed = now - _previousAt;
            var logical = LogicalProcessors();
            var previous = _previous!;
            var currentPid = _processes.CurrentProcessId;

            var result = new List<ProcessInfo>(snapshots.Count);
            foreach (var p in snapshots)
            {
                double cpuPercent = 0;
                double? diskPerSecond = null;
                if (elapsed > TimeSpan.Zero && previous.TryGetValue((p.ProcessId, p.StartTime), out var before))
                {
                    cpuPercent = ComputeCpuPercent(before.Cpu, p.TotalProcessorTime, elapsed, logical);
                    var io = p.IoReadBytes + p.IoWriteBytes - before.Io;
                    diskPerSecond = Math.Max(0, io) / elapsed.TotalSeconds;
                }

                result.Add(new ProcessInfo(
                    p.ProcessId,
                    p.Name,
                    p.ExecutablePath,
                    cpuPercent,
                    p.WorkingSetBytes,
                    diskPerSecond,
                    p.Priority,
                    p.HasMainWindow,
                    p.MainWindowTitle,
                    p.IsCurrentUser,
                    p.SessionId,
                    p.StartTime,
                    _security.Assess(p.ExecutablePath),
                    ProtectionOf(p.ProcessId, p.Name, p.ExecutablePath, currentPid)));
            }

            Remember(snapshots, now);
            return result;
        }
        finally
        {
            _sampleLock.Release();
        }
    }

    /// <summary>Pourcentage de la capacité totale du processeur (0–100).</summary>
    internal static double ComputeCpuPercent(TimeSpan cpuBefore, TimeSpan cpuAfter, TimeSpan elapsed, int logicalProcessors)
    {
        if (elapsed <= TimeSpan.Zero || logicalProcessors <= 0) return 0;
        var delta = (cpuAfter - cpuBefore).TotalMilliseconds;
        if (delta <= 0) return 0;
        return Math.Clamp(delta / (elapsed.TotalMilliseconds * logicalProcessors) * 100d, 0, 100);
    }

    public OperationResult CloseApplication(int processId)
    {
        var check = CheckActionAllowed(processId, out var snapshot);
        if (!check.Success) return check;
        var result = _control.RequestClose(processId);
        _journal.LogInBackground(result.Success ? ActivityKind.Info : ActivityKind.Warning,
            TextRef.Of(result.Success ? "Opt_Journal_ProcessCloseRequested" : "Opt_Journal_ProcessActionFailed", snapshot!.Name),
            result.Success ? null : result.Error.ToString(), _logger);
        return result;
    }

    public OperationResult TerminateProcess(int processId)
    {
        var check = CheckActionAllowed(processId, out var snapshot);
        if (!check.Success) return check;
        var result = _control.Terminate(processId);
        _journal.LogInBackground(ActivityKind.Warning,
            TextRef.Of(result.Success ? "Opt_Journal_ProcessTerminated" : "Opt_Journal_ProcessActionFailed", snapshot!.Name),
            result.Success ? null : result.Error.ToString(), _logger);
        _logger.LogInformation("Terminaison demandée par l'utilisateur : {Name} ({ProcessId}) → {Success}", snapshot.Name, processId, result.Success);
        return result;
    }

    public ProtectionInfo GetProtection(string processName, string? executablePath)
        => ProtectionOf(-1, processName, executablePath, _processes.CurrentProcessId);

    private OperationResult CheckActionAllowed(int processId, out ProcessSnapshot? snapshot)
    {
        snapshot = null;
        if (processId <= 4 || processId == _processes.CurrentProcessId)
            return Blocked(processId, "pcboost/system");

        snapshot = _processes.GetProcess(processId);
        if (snapshot is null)
            return OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Opt_Process_NotFound"));

        var protection = ProtectionOf(processId, snapshot.Name, snapshot.ExecutablePath, _processes.CurrentProcessId);
        return protection.IsCritical ? Blocked(processId, snapshot.Name) : OperationResult.Ok();
    }

    private OperationResult Blocked(int processId, string name)
    {
        _logger.LogWarning("Action refusée sur un processus protégé : {Name} ({ProcessId})", name, processId);
        return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Opt_Process_Protected"), name);
    }

    private ProtectionInfo ProtectionOf(int processId, string name, string? path, int currentProcessId)
    {
        if (processId == currentProcessId || KnownSoftware.NeverTouchProcesses.Contains(CriticalName(name)))
            return new ProtectionInfo(ProtectionLevel.Critical, TextRef.Of("Protection_Critical"));
        return _protection.GetProtection(name, path);
    }

    private static string CriticalName(string name) => Core.Security.CriticalProcessProtection.Normalize(name);

    private void Remember(IReadOnlyList<ProcessSnapshot> snapshots, DateTimeOffset at)
    {
        var map = new Dictionary<(int, DateTimeOffset?), (TimeSpan, long)>(snapshots.Count);
        foreach (var p in snapshots) map[(p.ProcessId, p.StartTime)] = (p.TotalProcessorTime, p.IoReadBytes + p.IoWriteBytes);
        _previous = map;
        _previousAt = at;
    }

    private int LogicalProcessors()
    {
        if (_logicalProcessors > 0) return _logicalProcessors;
        try
        {
            var count = _systemInfo.GetCpuInfo().LogicalProcessors;
            _logicalProcessors = count > 0 ? count : Environment.ProcessorCount;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Nombre de processeurs logiques indisponible");
            _logicalProcessors = Environment.ProcessorCount;
        }
        return _logicalProcessors;
    }
}
