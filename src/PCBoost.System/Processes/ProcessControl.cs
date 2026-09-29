using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>
/// Actions sur les processus (Win32). Les vérifications de protection (CriticalProcessProtection) sont faites par le
/// service appelant ; ce fournisseur ajoute des garde-fous minimaux : jamais de priorité Temps réel, jamais les
/// pseudo-processus Idle/System ni PCBoost lui-même.
/// </summary>
public sealed class ProcessControl : IProcessControl
{
    private readonly ILogger<ProcessControl> _logger;

    public ProcessControl(ILogger<ProcessControl>? logger = null)
    {
        _logger = logger ?? NullLogger<ProcessControl>.Instance;
    }

    /// <summary>Le mode efficacité (présenté comme tel par le Gestionnaire des tâches) est une fonctionnalité de Windows 11.</summary>
    public bool IsEfficiencyModeSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public OperationResult RequestClose(int processId)
    {
        if (IsForbiddenTarget(processId)) return Blocked();
        if (!Exists(processId)) return NotFound();

        var visible = WindowEnumerator.EnumerateTopLevelWindows(visibleOnly: true)
            .Where(w => w.ProcessId == processId)
            .ToList();
        // Fenêtres principales (sans propriétaire, non masquées par DWM) en priorité : leurs boîtes de dialogue se ferment avec elles.
        var main = visible.Where(w => !w.HasOwner && !w.IsCloaked).ToList();
        var windows = main.Count > 0 ? main : visible;
        if (windows.Count == 0)
            return OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Sys_ProcessNoWindow"));

        var posted = 0;
        var lastError = 0;
        foreach (var window in windows)
        {
            if (User32.PostMessage(window.Handle, User32.WM_CLOSE, 0, 0)) posted++;
            else lastError = Marshal.GetLastPInvokeError();
        }

        if (posted > 0)
        {
            _logger.LogInformation("Fermeture demandée au processus {ProcessId} ({Count} fenêtre(s))", processId, posted);
            return OperationResult.Ok();
        }
        // UIPI : une fenêtre d'un processus de niveau d'intégrité supérieur refuse les messages (ERROR_ACCESS_DENIED).
        return Win32Errors.Fail(lastError == 0 ? Win32Errors.ERROR_ACCESS_DENIED : lastError, AccessDeniedMessage(lastError));
    }

    public OperationResult Terminate(int processId)
    {
        if (IsForbiddenTarget(processId)) return Blocked();
        using var handle = NativeProcess.Open(processId, Kernel32.PROCESS_TERMINATE, out var error);
        if (handle is null) return OpenFailure(error);

        if (!Kernel32.TerminateProcess(handle, 1))
        {
            var terminateError = Marshal.GetLastPInvokeError();
            return Win32Errors.Fail(terminateError, AccessDeniedMessage(terminateError));
        }
        _logger.LogInformation("Processus {ProcessId} terminé à la demande de l'utilisateur", processId);
        return OperationResult.Ok();
    }

    public OperationResult<ProcessPriority> GetPriority(int processId)
    {
        using var handle = NativeProcess.Open(processId, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, out var error);
        if (handle is null) return OpenFailure(error).ToTyped<ProcessPriority>();
        var priority = NativeProcess.GetPriority(handle);
        return priority is null
            ? Win32Errors.Fail<ProcessPriority>(Marshal.GetLastPInvokeError())
            : OperationResult<ProcessPriority>.Ok(priority.Value);
    }

    public OperationResult SetPriority(int processId, ProcessPriority priority)
    {
        if (priority == ProcessPriority.RealTime)
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_RealTimePriorityBlocked"));
        var priorityClass = PriorityMapping.ToPriorityClass(priority);
        if (priorityClass is null) return OperationResult.Fail(OperationErrorKind.InvalidInput);
        if (IsForbiddenTarget(processId)) return Blocked();

        using var handle = NativeProcess.Open(processId, Kernel32.PROCESS_SET_INFORMATION | Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, out var error);
        if (handle is null) return OpenFailure(error);
        if (!Kernel32.SetPriorityClass(handle, priorityClass.Value))
        {
            var setError = Marshal.GetLastPInvokeError();
            return Win32Errors.Fail(setError, AccessDeniedMessage(setError));
        }
        _logger.LogInformation("Priorité du processus {ProcessId} : {Priority}", processId, priority);
        return OperationResult.Ok();
    }

    public OperationResult<bool> GetEfficiencyMode(int processId)
    {
        if (!IsEfficiencyModeSupported)
            return OperationResult<bool>.Fail(OperationErrorKind.NotSupported, TextRef.Of("Sys_EfficiencyModeNotSupported"));

        using var handle = NativeProcess.Open(processId, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, out var error);
        if (handle is null) return OpenFailure(error).ToTyped<bool>();
        var state = NativeProcess.GetPowerThrottling(handle, out var readError);
        return state is null
            ? Win32Errors.Fail<bool>(readError)
            : OperationResult<bool>.Ok(state.Value);
    }

    /// <summary>
    /// Comme le Gestionnaire des tâches : EcoQoS (limitation de la vitesse d'exécution) + classe de priorité « Inactive ».
    /// La désactivation rend la limitation à Windows et remet la priorité « Normale » si elle était encore « Inactive ».
    /// </summary>
    public OperationResult SetEfficiencyMode(int processId, bool enabled)
    {
        if (!IsEfficiencyModeSupported)
            return OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Sys_EfficiencyModeNotSupported"));
        if (IsForbiddenTarget(processId)) return Blocked();

        using var handle = NativeProcess.Open(processId, Kernel32.PROCESS_SET_INFORMATION | Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, out var error);
        if (handle is null) return OpenFailure(error);

        if (!NativeProcess.SetPowerThrottling(handle, enabled, out var throttleError))
            return Win32Errors.Fail(throttleError, AccessDeniedMessage(throttleError));

        if (enabled)
        {
            if (!Kernel32.SetPriorityClass(handle, Kernel32.IDLE_PRIORITY_CLASS))
            {
                var priorityError = Marshal.GetLastPInvokeError();
                // Annule la moitié déjà appliquée pour ne pas laisser un état incohérent.
                NativeProcess.SetPowerThrottling(handle, false, out _);
                return Win32Errors.Fail(priorityError, AccessDeniedMessage(priorityError));
            }
        }
        else if (Kernel32.GetPriorityClass(handle) == Kernel32.IDLE_PRIORITY_CLASS
                 && !Kernel32.SetPriorityClass(handle, Kernel32.NORMAL_PRIORITY_CLASS))
        {
            var priorityError = Marshal.GetLastPInvokeError();
            return Win32Errors.Fail(priorityError, AccessDeniedMessage(priorityError));
        }

        _logger.LogInformation("Mode efficacité du processus {ProcessId} : {Enabled}", processId, enabled);
        return OperationResult.Ok();
    }

    /// <summary>Idle (0), System (4) et PCBoost lui-même ne sont jamais modifiés par ce fournisseur.</summary>
    internal static bool IsForbiddenTarget(int processId)
        => processId <= 4 || processId == Environment.ProcessId;

    private static bool Exists(int processId)
    {
        using var handle = NativeProcess.Open(processId, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, out var error);
        if (handle is null) return error == Win32Errors.ERROR_ACCESS_DENIED;
        return NativeProcess.IsAlive(handle);
    }

    private static OperationResult OpenFailure(int error)
        => error == Win32Errors.ERROR_INVALID_PARAMETER
            ? NotFound()
            : Win32Errors.Fail(error, AccessDeniedMessage(error));

    private static TextRef? AccessDeniedMessage(int error)
        => error == Win32Errors.ERROR_ACCESS_DENIED ? TextRef.Of("Sys_ProcessAccessDenied") : null;

    private static OperationResult NotFound()
        => OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Sys_ProcessNotFound"));

    private static OperationResult Blocked()
        => OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_ProtectedTarget"));
}

internal static class OperationResultExtensions
{
    public static OperationResult<T> ToTyped<T>(this OperationResult result)
        => new(result.Success, default, result.Error, result.Message, result.TechnicalDetail);
}
