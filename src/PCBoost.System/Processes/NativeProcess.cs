using System.Runtime.InteropServices;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>Fonctions Win32 élémentaires sur un processus, sans exception (valeurs null si inaccessible).</summary>
internal static unsafe class NativeProcess
{
    /// <summary>Ouvre un processus ; renvoie null et le code d'erreur Win32 si l'ouverture échoue.</summary>
    public static SafeKernelHandle? Open(int processId, uint access, out int error)
    {
        error = 0;
        if (processId < 0)
        {
            error = Win32Errors.ERROR_INVALID_PARAMETER;
            return null;
        }
        var handle = Kernel32.OpenProcess(access, false, (uint)processId);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return null;
        }
        return handle;
    }

    public static string? GetImagePath(SafeKernelHandle process)
    {
        uint size = 1024;
        var buffer = stackalloc char[1024];
        if (Kernel32.QueryFullProcessImageName(process, 0, buffer, ref size))
            return new string(buffer, 0, (int)size);

        if (Marshal.GetLastPInvokeError() != Win32Errors.ERROR_INSUFFICIENT_BUFFER)
            return null;

        // Chemins longs (préfixe \\?\ possible) : jusqu'à 32 767 caractères.
        const int Max = 32767;
        var heap = new char[Max];
        size = Max;
        fixed (char* p = heap)
        {
            return Kernel32.QueryFullProcessImageName(process, 0, p, ref size) ? new string(p, 0, (int)size) : null;
        }
    }

    /// <summary>Heure de création (FILETIME brut, 100 ns depuis 1601) ou null.</summary>
    public static long? GetCreationFileTime(SafeKernelHandle process)
        => Kernel32.GetProcessTimes(process, out var creation, out _, out _, out _) ? creation : null;

    public static bool TryGetTimes(SafeKernelHandle process, out long creationFileTime, out TimeSpan totalProcessorTime)
    {
        if (Kernel32.GetProcessTimes(process, out creationFileTime, out _, out var kernel, out var user))
        {
            totalProcessorTime = TimeSpan.FromTicks(kernel + user);
            return true;
        }
        creationFileTime = 0;
        totalProcessorTime = TimeSpan.Zero;
        return false;
    }

    public static DateTimeOffset? FileTimeToDateTime(long fileTime)
    {
        if (fileTime <= 0) return null;
        try
        {
            return DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static ProcessPriority? GetPriority(SafeKernelHandle process)
    {
        var value = Kernel32.GetPriorityClass(process);
        return value == 0 ? null : PriorityMapping.FromPriorityClass(value);
    }

    /// <summary>
    /// État du mode efficacité : EcoQoS (limitation de vitesse d'exécution demandée explicitement).
    /// Renvoie null si Windows ne permet pas de lire l'état (GetProcessInformation non pris en charge pour cette classe).
    /// </summary>
    public static bool? GetPowerThrottling(SafeKernelHandle process, out int error)
    {
        error = 0;
        var state = new Kernel32.PROCESS_POWER_THROTTLING_STATE { Version = Kernel32.PROCESS_POWER_THROTTLING_CURRENT_VERSION };
        if (!Kernel32.GetProcessInformation(process, Kernel32.ProcessPowerThrottling, &state, (uint)sizeof(Kernel32.PROCESS_POWER_THROTTLING_STATE)))
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }
        const uint Speed = Kernel32.PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
        return (state.ControlMask & Speed) != 0 && (state.StateMask & Speed) != 0;
    }

    /// <summary>
    /// Active (ControlMask = StateMask = EXECUTION_SPEED) ou rend à Windows la gestion de la limitation
    /// (ControlMask = StateMask = 0, comportement par défaut d'un processus).
    /// </summary>
    public static bool SetPowerThrottling(SafeKernelHandle process, bool enabled, out int error)
    {
        error = 0;
        var state = new Kernel32.PROCESS_POWER_THROTTLING_STATE
        {
            Version = Kernel32.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = enabled ? Kernel32.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            StateMask = enabled ? Kernel32.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
        };
        if (Kernel32.SetProcessInformation(process, Kernel32.ProcessPowerThrottling, &state, (uint)sizeof(Kernel32.PROCESS_POWER_THROTTLING_STATE)))
            return true;
        error = Marshal.GetLastPInvokeError();
        return false;
    }

    /// <summary>Le processus est-il encore actif (handle ouvert avec SYNCHRONIZE ou QUERY_LIMITED) ?</summary>
    public static bool IsAlive(SafeKernelHandle process)
    {
        if (Kernel32.GetExitCodeProcess(process, out var code) && code != Kernel32.STILL_ACTIVE)
            return false;
        // Un processus peut renvoyer 259 comme code de sortie : on confirme avec l'état signalé du handle si possible.
        var wait = Kernel32.WaitForSingleObject(process, 0);
        return wait != Kernel32.WAIT_OBJECT_0;
    }

    /// <summary>Instantané Toolhelp32 léger : PID et nom d'image (avec extension), sans ouvrir les processus.</summary>
    public static List<(int ProcessId, int ParentProcessId, string Name)> SnapshotNames()
    {
        var result = new List<(int, int, string)>(256);
        using var snapshot = Kernel32.CreateToolhelp32Snapshot(Kernel32.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid) return result;

        var entry = new Kernel32.PROCESSENTRY32W { dwSize = (uint)sizeof(Kernel32.PROCESSENTRY32W) };
        if (!Kernel32.Process32First(snapshot, ref entry)) return result;
        do
        {
            var pid = (int)entry.th32ProcessID;
            var name = pid == 0 ? "Idle" : NativeStrings.FromFixedBuffer(entry.szExeFile, 260);
            result.Add((pid, (int)entry.th32ParentProcessID, name));
            entry.dwSize = (uint)sizeof(Kernel32.PROCESSENTRY32W);
        }
        while (Kernel32.Process32Next(snapshot, ref entry));
        return result;
    }
}

/// <summary>Correspondances entre classes de priorité Win32, priorité de base et <see cref="ProcessPriority"/>.</summary>
internal static class PriorityMapping
{
    public static ProcessPriority FromPriorityClass(uint priorityClass) => priorityClass switch
    {
        Kernel32.IDLE_PRIORITY_CLASS => ProcessPriority.Idle,
        Kernel32.BELOW_NORMAL_PRIORITY_CLASS => ProcessPriority.BelowNormal,
        Kernel32.NORMAL_PRIORITY_CLASS => ProcessPriority.Normal,
        Kernel32.ABOVE_NORMAL_PRIORITY_CLASS => ProcessPriority.AboveNormal,
        Kernel32.HIGH_PRIORITY_CLASS => ProcessPriority.High,
        Kernel32.REALTIME_PRIORITY_CLASS => ProcessPriority.RealTime,
        _ => ProcessPriority.Unknown,
    };

    public static uint? ToPriorityClass(ProcessPriority priority) => priority switch
    {
        ProcessPriority.Idle => Kernel32.IDLE_PRIORITY_CLASS,
        ProcessPriority.BelowNormal => Kernel32.BELOW_NORMAL_PRIORITY_CLASS,
        ProcessPriority.Normal => Kernel32.NORMAL_PRIORITY_CLASS,
        ProcessPriority.AboveNormal => Kernel32.ABOVE_NORMAL_PRIORITY_CLASS,
        ProcessPriority.High => Kernel32.HIGH_PRIORITY_CLASS,
        ProcessPriority.RealTime => Kernel32.REALTIME_PRIORITY_CLASS,
        _ => null,
    };

    /// <summary>Priorité de base (fournie sans handle par l'énumération système) vers la classe de priorité.</summary>
    public static ProcessPriority FromBasePriority(int basePriority) => basePriority switch
    {
        4 => ProcessPriority.Idle,
        6 => ProcessPriority.BelowNormal,
        8 => ProcessPriority.Normal,
        10 => ProcessPriority.AboveNormal,
        13 => ProcessPriority.High,
        24 => ProcessPriority.RealTime,
        _ => ProcessPriority.Unknown,
    };
}
