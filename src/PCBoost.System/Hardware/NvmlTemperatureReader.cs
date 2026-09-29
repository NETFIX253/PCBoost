using System.Runtime.InteropServices;

namespace PCBoost.Platform;

/// <summary>
/// Température GPU NVIDIA via NVML (bibliothèque installée avec le pilote), chargée dynamiquement :
/// nvml.dll (System32) puis %ProgramFiles%\NVIDIA Corporation\NVSMI\nvml.dll.
/// </summary>
internal sealed unsafe class NvmlTemperatureReader : IDisposable
{
    private const int NVML_SUCCESS = 0;
    private const int NVML_TEMPERATURE_GPU = 0;

    private readonly nint _library;
    private readonly delegate* unmanaged<int> _shutdown;
    private readonly delegate* unmanaged<nint, int, uint*, int> _getTemperature;
    private readonly nint _device;
    private bool _disposed;

    private NvmlTemperatureReader(nint library, delegate* unmanaged<int> shutdown, delegate* unmanaged<nint, int, uint*, int> getTemperature, nint device)
    {
        _library = library;
        _shutdown = shutdown;
        _getTemperature = getTemperature;
        _device = device;
    }

    /// <summary>Charge et initialise NVML pour le GPU d'indice 0, ou renvoie null (pilote absent, erreur NVML).</summary>
    public static NvmlTemperatureReader? TryCreate()
    {
        if (!TryLoadLibrary(out var library)) return null;
        try
        {
            if (!NativeLibrary.TryGetExport(library, "nvmlInit_v2", out var init)
                || !NativeLibrary.TryGetExport(library, "nvmlShutdown", out var shutdown)
                || !NativeLibrary.TryGetExport(library, "nvmlDeviceGetHandleByIndex_v2", out var getHandle)
                || !NativeLibrary.TryGetExport(library, "nvmlDeviceGetTemperature", out var getTemperature))
            {
                NativeLibrary.Free(library);
                return null;
            }

            if (((delegate* unmanaged<int>)init)() != NVML_SUCCESS)
            {
                NativeLibrary.Free(library);
                return null;
            }

            nint device;
            if (((delegate* unmanaged<uint, nint*, int>)getHandle)(0, &device) != NVML_SUCCESS || device == 0)
            {
                ((delegate* unmanaged<int>)shutdown)();
                NativeLibrary.Free(library);
                return null;
            }

            return new NvmlTemperatureReader(library, (delegate* unmanaged<int>)shutdown, (delegate* unmanaged<nint, int, uint*, int>)getTemperature, device);
        }
        catch (Exception ex) when (ex is SEHException or AccessViolationException or BadImageFormatException)
        {
            NativeLibrary.Free(library);
            return null;
        }
    }

    /// <summary>Température du cœur GPU en °C, ou null.</summary>
    public double? ReadCelsius()
    {
        if (_disposed) return null;
        uint value;
        return _getTemperature(_device, NVML_TEMPERATURE_GPU, &value) == NVML_SUCCESS ? value : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown();
        NativeLibrary.Free(_library);
    }

    private static bool TryLoadLibrary(out nint library)
    {
        if (NativeLibrary.TryLoad("nvml.dll", typeof(NvmlTemperatureReader).Assembly, DllImportSearchPath.System32, out library))
            return true;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(programFiles)) return false;
        var path = Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll");
        return File.Exists(path) && NativeLibrary.TryLoad(path, out library);
    }
}
