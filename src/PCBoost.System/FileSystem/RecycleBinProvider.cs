using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>Corbeille de tous les lecteurs (SHQueryRecycleBinW / SHEmptyRecycleBinW, sans confirmation ni son ni progression).</summary>
public sealed class RecycleBinProvider : IRecycleBinProvider
{
    private readonly ILogger<RecycleBinProvider> _logger;

    public RecycleBinProvider(ILogger<RecycleBinProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<RecycleBinProvider>.Instance;
    }

    public OperationResult<(long Bytes, long Items)> Query()
    {
        try
        {
            int hr;
            long size, items;
            if (nint.Size == 8)
            {
                var info = new Shell32.SHQUERYRBINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Shell32.SHQUERYRBINFO>() };
                hr = Shell32.SHQueryRecycleBin(null, ref info);
                (size, items) = (info.i64Size, info.i64NumItems);
            }
            else
            {
                var info = new Shell32.SHQUERYRBINFO32 { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Shell32.SHQUERYRBINFO32>() };
                hr = Shell32.SHQueryRecycleBin32(null, ref info);
                (size, items) = (info.i64Size, info.i64NumItems);
            }

            if (hr < 0)
                return OperationResult<(long, long)>.Fail(Win32Errors.Classify(Win32Errors.FromHResult(hr)), null, $"HRESULT 0x{hr:X8}");
            return OperationResult<(long, long)>.Ok((Math.Max(0, size), Math.Max(0, items)));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return OperationResult<(long, long)>.FromException(ex);
        }
    }

    public OperationResult Empty()
    {
        var before = Query();
        if (before.Success && before.Value.Items == 0) return OperationResult.Ok();

        try
        {
            var hr = Shell32.SHEmptyRecycleBin(0, null, Shell32.SHERB_NOCONFIRMATION | Shell32.SHERB_NOPROGRESSUI | Shell32.SHERB_NOSOUND);
            if (hr >= 0)
            {
                _logger.LogInformation("Corbeille vidée");
                return OperationResult.Ok();
            }

            // Certaines versions renvoient E_UNEXPECTED lorsque la corbeille est déjà vide.
            var after = Query();
            if (after.Success && after.Value.Items == 0) return OperationResult.Ok();
            return OperationResult.Fail(Win32Errors.Classify(Win32Errors.FromHResult(hr)), null, $"HRESULT 0x{hr:X8}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return OperationResult.FromException(ex);
        }
    }
}
