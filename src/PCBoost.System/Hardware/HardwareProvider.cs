using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>
/// Températures (cache 5 s). CPU : zones thermiques ACPI (PDH, puis WMI si élevé) ; GPU : NVML pour NVIDIA ;
/// stockage : compteurs de fiabilité du disque système (élévation requise). Aucune valeur n'est estimée :
/// une lecture absente ou hors plage (≤ 0 °C ou &gt; 120 °C) est rapportée comme indisponible.
/// </summary>
public sealed class HardwareProvider : IHardwareProvider, IDisposable
{
    internal const string ThermalZonePath = @"\Thermal Zone Information(*)\Temperature";
    internal const string AcpiSource = "ACPI thermal zone";
    internal const string NvmlSource = "NVIDIA NVML";
    internal const string StorageSource = "Storage reliability counter";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private static readonly long RetryUnavailableMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

    private readonly ISystemInfoProvider _systemInfo;
    private readonly ILogger<HardwareProvider> _logger;
    private readonly Lock _lock = new();
    private readonly TimedCache<TemperatureReadings> _cache;

    private PdhQuery? _thermalQuery;
    private PdhCounter? _thermalCounter;
    private long _thermalRetryAt;

    private NvmlTemperatureReader? _nvml;
    private long _nvmlRetryAt;
    private bool _disposed;

    public HardwareProvider(ISystemInfoProvider systemInfo, ILogger<HardwareProvider>? logger = null)
    {
        _systemInfo = systemInfo;
        _logger = logger ?? NullLogger<HardwareProvider>.Instance;
        _cache = new TimedCache<TemperatureReadings>(CacheLifetime, ReadAll);
    }

    public TemperatureReadings GetTemperatures()
    {
        lock (_lock)
        {
            return _disposed ? TemperatureReadings.None : _cache.Get();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _thermalQuery?.Dispose();
            _nvml?.Dispose();
        }
    }

    private TemperatureReadings ReadAll()
    {
        var elevated = TokenHelper.IsCurrentProcessElevated;
        return new TemperatureReadings(ReadCpu(elevated), ReadGpu(), ReadStorage(elevated));
    }

    private SensorReading ReadCpu(bool elevated)
    {
        var fromPdh = ReadThermalZonesPdh();
        if (fromPdh is not null) return SensorReading.Of(fromPdh.Value, AcpiSource);

        if (!elevated) return SensorReading.Unavailable(Availability.RequiresElevation, AcpiSource);

        var zones = Wmi.Query(Wmi.RootWmi,
            "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature",
            o => Wmi.Int64(o, "CurrentTemperature") is { } t ? new Box(TemperatureMath.TenthsKelvinToCelsius(t)) : null,
            _logger);
        if (zones.Count == 0) return SensorReading.Unavailable(Availability.NoSensor, AcpiSource);
        var max = TemperatureMath.MaxPlausible(zones.Select(z => z.Value));
        return max is null ? SensorReading.Unavailable(Availability.Unavailable, AcpiSource) : SensorReading.Of(max.Value, AcpiSource);
    }

    private double? ReadThermalZonesPdh()
    {
        try
        {
            if (_thermalCounter is null)
            {
                if (Environment.TickCount64 < _thermalRetryAt) return null;
                _thermalQuery?.Dispose();
                _thermalQuery = PdhQuery.TryOpen();
                _thermalCounter = _thermalQuery?.TryAdd(ThermalZonePath);
                if (_thermalCounter is null)
                {
                    _thermalQuery?.Dispose();
                    _thermalQuery = null;
                    _thermalRetryAt = Environment.TickCount64 + RetryUnavailableMs;
                    return null;
                }
            }

            if (_thermalQuery is null || !_thermalQuery.Collect()) return null;
            // Compteur brut en kelvins : une seule collecte suffit.
            var kelvins = _thermalCounter.ReadInstances(noCap100: true);
            return kelvins is null ? null : TemperatureMath.MaxPlausible(kelvins.Select(TemperatureMath.KelvinToCelsius));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "PDH indisponible pour les zones thermiques");
            _thermalRetryAt = long.MaxValue;
            return null;
        }
    }

    private SensorReading ReadGpu()
    {
        IReadOnlyList<GpuInfo> gpus;
        try
        {
            gpus = _systemInfo.GetGpus();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Liste des GPU indisponible");
            return SensorReading.Unavailable(Availability.Unavailable);
        }

        var hardware = gpus.Where(g => !g.IsSoftwareAdapter).ToList();
        if (hardware.Count == 0) return SensorReading.Unavailable(Availability.NoSensor);
        if (!hardware.Any(g => g.Vendor == GpuVendor.Nvidia)) return SensorReading.Unavailable(Availability.NotSupported);

        if (_nvml is null)
        {
            if (Environment.TickCount64 < _nvmlRetryAt) return SensorReading.Unavailable(Availability.Unavailable, NvmlSource);
            _nvml = NvmlTemperatureReader.TryCreate();
            if (_nvml is null)
            {
                _nvmlRetryAt = Environment.TickCount64 + RetryUnavailableMs;
                _logger.LogInformation("NVML indisponible : température GPU non mesurée");
                return SensorReading.Unavailable(Availability.Unavailable, NvmlSource);
            }
        }

        var celsius = _nvml.ReadCelsius();
        return celsius is { } c && TemperatureMath.IsPlausible(c)
            ? SensorReading.Of(c, NvmlSource)
            : SensorReading.Unavailable(Availability.Unavailable, NvmlSource);
    }

    private SensorReading ReadStorage(bool elevated)
    {
        if (!elevated) return SensorReading.Unavailable(Availability.RequiresElevation, StorageSource);

        var systemLetter = char.ToUpperInvariant(SystemInfoProvider.GetSystemDriveRoot()[0]);
        var diskNumbers = Wmi.Query(Wmi.Storage,
            "SELECT DriveLetter, DiskNumber FROM MSFT_Partition",
            o => Wmi.Char(o, "DriveLetter") is { } l && char.ToUpperInvariant(l) == systemLetter && Wmi.Int64(o, "DiskNumber") is { } d ? new Box(d) : null,
            _logger);
        if (diskNumbers.Count == 0) return SensorReading.Unavailable(Availability.Unavailable, StorageSource);
        var deviceId = ((long)diskNumbers[0].Value).ToString(CultureInfo.InvariantCulture);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(Wmi.Storage),
                new ObjectQuery($"SELECT * FROM MSFT_PhysicalDisk WHERE DeviceId = '{deviceId}'"));
            using var disks = searcher.Get();
            foreach (ManagementObject disk in disks)
            {
                using (disk)
                {
                    using var counters = disk.GetRelated("MSFT_StorageReliabilityCounter");
                    foreach (var counter in counters)
                    {
                        using (counter)
                        {
                            var value = Wmi.Int64(counter, "Temperature");
                            if (value is { } t && TemperatureMath.IsPlausible(t))
                                return SensorReading.Of(t, StorageSource);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Compteurs de fiabilité du stockage indisponibles");
            return SensorReading.Unavailable(Availability.Unavailable, StorageSource);
        }
        return SensorReading.Unavailable(Availability.NoSensor, StorageSource);
    }

    private sealed record Box(double Value);
}
