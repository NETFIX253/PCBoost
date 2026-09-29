using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>
/// Lecture des processus. L'énumération système (Process.GetProcesses) fournit sans handle le nom, la mémoire et la session ;
/// chaque processus est ensuite ouvert avec PROCESS_QUERY_LIMITED_INFORMATION (chemin, temps CPU, E/S, priorité, propriétaire).
/// Un processus inaccessible est renvoyé avec des valeurs partielles.
/// </summary>
public sealed class ProcessProvider : IProcessProvider
{
    private readonly ILogger<ProcessProvider> _logger;
    private readonly Lock _ownerLock = new();
    private readonly Dictionary<(int ProcessId, long CreationTime), bool> _ownerCache = [];
    private readonly Lazy<int> _currentSessionId;

    public ProcessProvider(ILogger<ProcessProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<ProcessProvider>.Instance;
        _currentSessionId = new Lazy<int>(ReadCurrentSessionId);
    }

    public int CurrentProcessId => Environment.ProcessId;

    public int CurrentSessionId => _currentSessionId.Value;

    public IReadOnlyList<ProcessSnapshot> GetProcesses()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Énumération des processus impossible");
            return [];
        }

        var names = SnapshotNameMap();
        var windows = WindowEnumerator.GetMainWindows();
        var seen = new HashSet<(int, long)>();
        var result = new List<ProcessSnapshot>(processes.Length);
        foreach (var process in processes)
        {
            using (process)
            {
                var snapshot = Build(process, names, windows, seen);
                if (snapshot is not null) result.Add(snapshot);
            }
        }

        lock (_ownerLock)
        {
            foreach (var key in _ownerCache.Keys.Where(k => !seen.Contains(k)).ToList())
                _ownerCache.Remove(key);
        }
        return result;
    }

    public IReadOnlyList<ProcessIdentity> GetProcessIdentities()
    {
        try
        {
            return NativeProcess.SnapshotNames().Select(e => new ProcessIdentity(e.ProcessId, e.Name)).ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Instantané Toolhelp32 impossible");
            return [];
        }
    }

    public ProcessSnapshot? GetProcess(int processId)
    {
        if (processId < 0) return null;
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }

        using (process)
        {
            var names = SnapshotNameMap();
            var windows = WindowEnumerator.GetMainWindows();
            return Build(process, names, windows, seen: null);
        }
    }

    public string? GetExecutablePath(int processId)
    {
        using var handle = NativeProcess.Open(processId, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, out _);
        return handle is null ? null : NativeProcess.GetImagePath(handle);
    }

    public bool IsRunning(int processId, DateTimeOffset? expectedStartTime = null)
    {
        if (processId < 0) return false;
        var handle = NativeProcess.Open(processId, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION | Kernel32.SYNCHRONIZE, out var error)
            ?? NativeProcess.Open(processId, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, out error);
        if (handle is null)
        {
            // Accès refusé : le processus existe, mais son heure de démarrage n'est pas vérifiable.
            return error == Win32Errors.ERROR_ACCESS_DENIED && expectedStartTime is null;
        }

        using (handle)
        {
            if (!NativeProcess.IsAlive(handle)) return false;
            if (expectedStartTime is null) return true;
            var creation = NativeProcess.GetCreationFileTime(handle);
            var start = creation is null ? null : NativeProcess.FileTimeToDateTime(creation.Value);
            return start is not null && StartTimesMatch(start.Value, expectedStartTime.Value);
        }
    }

    /// <summary>Tolérance d'une seconde : l'heure de démarrage peut avoir été arrondie lors de sa persistance.</summary>
    internal static bool StartTimesMatch(DateTimeOffset actual, DateTimeOffset expected)
        => (actual - expected).Duration() <= TimeSpan.FromSeconds(1);

    private ProcessSnapshot? Build(Process process, Dictionary<int, string> names, Dictionary<int, TopLevelWindow> windows, HashSet<(int, long)>? seen)
    {
        int pid;
        try
        {
            pid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var name = names.TryGetValue(pid, out var n) ? n : SafeRead(() => process.ProcessName, $"pid-{pid}");
        var workingSet = SafeRead(() => process.WorkingSet64, 0L);
        var privateBytes = SafeRead(() => process.PrivateMemorySize64, 0L);
        var sessionId = SafeRead(() => process.SessionId, -1);
        var basePriority = SafeRead(() => process.BasePriority, 0);

        string? path = null;
        var cpu = TimeSpan.Zero;
        long ioRead = 0, ioWrite = 0;
        DateTimeOffset? start = null;
        ProcessPriority? priority = null;
        var isCurrentUser = false;

        using (var handle = NativeProcess.Open(pid, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, out _))
        {
            if (handle is not null)
            {
                path = NativeProcess.GetImagePath(handle);
                if (NativeProcess.TryGetTimes(handle, out var creation, out var total))
                {
                    cpu = total;
                    start = NativeProcess.FileTimeToDateTime(creation);
                }
                if (Kernel32.GetProcessIoCounters(handle, out var io))
                {
                    ioRead = (long)Math.Min(io.ReadTransferCount, long.MaxValue);
                    ioWrite = (long)Math.Min(io.WriteTransferCount, long.MaxValue);
                }
                priority = NativeProcess.GetPriority(handle);
                isCurrentUser = IsOwnedByCurrentUser(pid, creation, handle, seen);
            }
        }

        if (sessionId < 0 && Kernel32.ProcessIdToSessionId((uint)pid, out var session))
            sessionId = (int)session;

        windows.TryGetValue(pid, out var window);
        return new ProcessSnapshot(
            pid,
            name,
            path,
            workingSet,
            privateBytes,
            cpu,
            ioRead,
            ioWrite,
            Math.Max(sessionId, 0),
            start,
            priority ?? PriorityMapping.FromBasePriority(basePriority),
            window.Handle != 0,
            window.Handle != 0 && window.Title.Length > 0 ? window.Title : null,
            isCurrentUser);
    }

    private bool IsOwnedByCurrentUser(int pid, long creationTime, SafeKernelHandle handle, HashSet<(int, long)>? seen)
    {
        var key = (pid, creationTime);
        seen?.Add(key);
        lock (_ownerLock)
        {
            if (creationTime != 0 && _ownerCache.TryGetValue(key, out var cached)) return cached;
        }

        var current = TokenHelper.CurrentUserSid;
        var owner = TokenHelper.GetProcessUserSid(handle);
        var result = current is not null && owner is not null && owner.Equals(current);

        if (creationTime != 0)
        {
            lock (_ownerLock) _ownerCache[key] = result;
        }
        return result;
    }

    private Dictionary<int, string> SnapshotNameMap()
    {
        var map = new Dictionary<int, string>(256);
        try
        {
            foreach (var (pid, _, name) in NativeProcess.SnapshotNames())
                map[pid] = name;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Noms Toolhelp32 indisponibles");
        }
        return map;
    }

    private static int ReadCurrentSessionId()
        => Kernel32.ProcessIdToSessionId((uint)Environment.ProcessId, out var session) ? (int)session : 0;

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or PlatformNotSupportedException)
        {
            return fallback;
        }
    }
}
