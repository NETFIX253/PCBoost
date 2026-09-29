using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>Plans d'alimentation (API documentée powrprof.dll).</summary>
public sealed unsafe class PowerProvider : IPowerProvider
{
    private readonly ILogger<PowerProvider> _logger;

    public PowerProvider(ILogger<PowerProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<PowerProvider>.Instance;
    }

    public PowerScheme? GetActiveScheme()
    {
        try
        {
            if (PowrProf.PowerGetActiveScheme(0, out var pointer) != PowrProf.ERROR_SUCCESS || pointer == 0) return null;
            Guid id;
            try
            {
                id = *(Guid*)pointer;
            }
            finally
            {
                Kernel32.LocalFree(pointer);
            }
            return new PowerScheme(id, ReadFriendlyName(id) ?? id.ToString());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "powrprof indisponible");
            return null;
        }
    }

    public IReadOnlyList<PowerScheme> GetSchemes()
    {
        var schemes = new List<PowerScheme>();
        try
        {
            for (uint index = 0; index < 64; index++)
            {
                Guid id;
                var size = (uint)sizeof(Guid);
                var status = PowrProf.PowerEnumerate(0, null, null, PowrProf.ACCESS_SCHEME, index, (byte*)&id, ref size);
                if (status == PowrProf.ERROR_NO_MORE_ITEMS) break;
                if (status != PowrProf.ERROR_SUCCESS) continue;
                schemes.Add(new PowerScheme(id, ReadFriendlyName(id) ?? id.ToString()));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "powrprof indisponible");
        }
        return schemes;
    }

    public OperationResult SetActiveScheme(Guid schemeId)
    {
        if (schemeId == Guid.Empty) return OperationResult.Fail(OperationErrorKind.InvalidInput);
        if (GetSchemes().All(s => s.Id != schemeId))
            return OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Sys_PowerSchemeNotFound"));

        try
        {
            var target = schemeId;
            var status = PowrProf.PowerSetActiveScheme(0, &target);
            if (status != PowrProf.ERROR_SUCCESS) return Win32Errors.Fail((int)status);
            _logger.LogInformation("Plan d'alimentation actif : {Scheme}", schemeId);
            return OperationResult.Ok();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return OperationResult.FromException(ex);
        }
    }

    private static string? ReadFriendlyName(Guid id)
    {
        uint size = 0;
        var status = PowrProf.PowerReadFriendlyName(0, &id, null, null, null, ref size);
        if ((status != PowrProf.ERROR_SUCCESS && status != PowrProf.ERROR_MORE_DATA) || size < 2 || size > 4096) return null;
        var buffer = stackalloc byte[(int)size];
        if (PowrProf.PowerReadFriendlyName(0, &id, null, null, buffer, ref size) != PowrProf.ERROR_SUCCESS) return null;
        var name = Marshal.PtrToStringUni((nint)buffer, (int)(size / 2)).TrimEnd('\0').Trim();
        return name.Length > 0 ? name : null;
    }
}
