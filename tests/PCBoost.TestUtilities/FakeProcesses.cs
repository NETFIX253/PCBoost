using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.SystemInfo;

namespace PCBoost.TestUtilities;

public sealed class FakeProcessProvider : IProcessProvider, IProcessControl
{
    private readonly Dictionary<int, ProcessSnapshot> _processes = new();
    private readonly Dictionary<int, bool> _efficiency = new();

    public List<int> TerminatedProcessIds { get; } = [];
    public List<int> CloseRequestedProcessIds { get; } = [];
    public HashSet<int> AccessDeniedProcessIds { get; } = [];

    public int CurrentProcessId { get; set; } = 4242;
    public int CurrentSessionId { get; set; } = 1;
    public bool IsEfficiencyModeSupported { get; set; } = true;

    public ProcessSnapshot Add(int pid, string name, string? path = null, long workingSet = 50 * 1024 * 1024,
        TimeSpan? cpu = null, bool hasWindow = false, bool currentUser = true, int session = 1,
        ProcessPriority priority = ProcessPriority.Normal, DateTimeOffset? start = null, long ioRead = 0)
    {
        var s = new ProcessSnapshot(pid, name, path, workingSet, workingSet, cpu ?? TimeSpan.FromSeconds(1), ioRead, 0,
            session, start ?? new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero), priority, hasWindow, hasWindow ? name : null, currentUser);
        _processes[pid] = s;
        return s;
    }

    public void Update(int pid, Func<ProcessSnapshot, ProcessSnapshot> update) => _processes[pid] = update(_processes[pid]);

    public void Remove(int pid) => _processes.Remove(pid);

    public IReadOnlyList<ProcessSnapshot> GetProcesses() => _processes.Values.ToList();

    public IReadOnlyList<ProcessIdentity> GetProcessIdentities() => _processes.Values.Select(p => new ProcessIdentity(p.ProcessId, p.Name)).ToList();

    public ProcessSnapshot? GetProcess(int processId) => _processes.GetValueOrDefault(processId);

    public string? GetExecutablePath(int processId) => _processes.GetValueOrDefault(processId)?.ExecutablePath;

    public bool IsRunning(int processId, DateTimeOffset? expectedStartTime = null)
        => _processes.TryGetValue(processId, out var p) && (expectedStartTime is null || p.StartTime == expectedStartTime);

    public OperationResult RequestClose(int processId)
    {
        if (!_processes.ContainsKey(processId)) return OperationResult.Fail(OperationErrorKind.NotFound);
        CloseRequestedProcessIds.Add(processId);
        return OperationResult.Ok();
    }

    public OperationResult Terminate(int processId)
    {
        if (AccessDeniedProcessIds.Contains(processId)) return OperationResult.Fail(OperationErrorKind.AccessDenied);
        if (!_processes.Remove(processId)) return OperationResult.Fail(OperationErrorKind.NotFound);
        TerminatedProcessIds.Add(processId);
        return OperationResult.Ok();
    }

    public OperationResult<ProcessPriority> GetPriority(int processId)
        => _processes.TryGetValue(processId, out var p) ? OperationResult<ProcessPriority>.Ok(p.Priority) : OperationResult<ProcessPriority>.Fail(OperationErrorKind.NotFound);

    public OperationResult SetPriority(int processId, ProcessPriority priority)
    {
        if (AccessDeniedProcessIds.Contains(processId)) return OperationResult.Fail(OperationErrorKind.AccessDenied);
        if (!_processes.TryGetValue(processId, out var p)) return OperationResult.Fail(OperationErrorKind.NotFound);
        _processes[processId] = p with { Priority = priority };
        return OperationResult.Ok();
    }

    public OperationResult<bool> GetEfficiencyMode(int processId)
        => _processes.ContainsKey(processId) ? OperationResult<bool>.Ok(_efficiency.GetValueOrDefault(processId)) : OperationResult<bool>.Fail(OperationErrorKind.NotFound);

    public OperationResult SetEfficiencyMode(int processId, bool enabled)
    {
        if (AccessDeniedProcessIds.Contains(processId)) return OperationResult.Fail(OperationErrorKind.AccessDenied);
        if (!_processes.ContainsKey(processId)) return OperationResult.Fail(OperationErrorKind.NotFound);
        _efficiency[processId] = enabled;
        return OperationResult.Ok();
    }
}
