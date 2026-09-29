using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>
/// Échantillonnage léger de la charge (appelé chaque seconde) : CPU par GetSystemTimes, mémoire par GlobalMemoryStatusEx,
/// disque, réseau et GPU par PDH (noms anglais). Un compteur absent donne un champ null. Au premier appel les débits sont null.
/// </summary>
public sealed class SystemMetricsProvider : ISystemMetricsProvider
{
    internal const string DiskIdlePath = @"\PhysicalDisk(_Total)\% Idle Time";
    internal const string DiskReadPath = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
    internal const string DiskWritePath = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
    internal const string NetworkReceivedPath = @"\Network Interface(*)\Bytes Received/sec";
    internal const string NetworkSentPath = @"\Network Interface(*)\Bytes Sent/sec";
    internal const string Gpu3DPath = @"\GPU Engine(*engtype_3D)\Utilization Percentage";
    internal const string GpuDedicatedPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
    internal const string ProcessorPerformancePath = @"\Processor Information(_Total)\% Processor Performance";

    /// <summary>Les instances « GPU Engine » suivent les processus : le compteur est recréé régulièrement.</summary>
    private static readonly long GpuRecreateIntervalMs = (long)TimeSpan.FromSeconds(30).TotalMilliseconds;

    private readonly IClock _clock;
    private readonly ILogger<SystemMetricsProvider> _logger;
    private readonly Lock _lock = new();

    private PdhQuery? _query;
    private PdhCounter? _diskIdle, _diskRead, _diskWrite, _netReceived, _netSent, _processorPerformance;
    private bool _initialized;

    private PdhQuery? _gpuQuery;
    private PdhCounter? _gpu3D, _gpuDedicated;
    private long _gpuCreatedAt;
    private bool _gpuJustCreated;

    private long _prevIdle, _prevKernel, _prevUser;
    private bool _hasPreviousTimes;
    private bool _disposed;

    public SystemMetricsProvider(IClock clock, ILogger<SystemMetricsProvider>? logger = null)
    {
        _clock = clock;
        _logger = logger ?? NullLogger<SystemMetricsProvider>.Instance;
        if (Kernel32.GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser))
            _hasPreviousTimes = true;
    }

    public SystemMetricsSample Sample()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureCounters();

            var cpu = SampleCpu();

            var memory = new Kernel32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Kernel32.MEMORYSTATUSEX>() };
            long total = 0, available = 0;
            if (Kernel32.GlobalMemoryStatusEx(ref memory))
            {
                total = (long)memory.ullTotalPhys;
                available = (long)memory.ullAvailPhys;
            }
            var used = Math.Max(0, total - available);

            double? diskActive = null, diskRead = null, diskWrite = null, netIn = null, netOut = null, performance = null;
            if (_query is not null && _query.Collect())
            {
                diskActive = MetricMath.DiskActivePercent(_diskIdle?.ReadDouble());
                diskRead = MetricMath.NonNegative(_diskRead?.ReadDouble());
                diskWrite = MetricMath.NonNegative(_diskWrite?.ReadDouble());
                netIn = MetricMath.Sum(_netReceived?.ReadInstances());
                netOut = MetricMath.Sum(_netSent?.ReadInstances());
                performance = MetricMath.ProcessorPerformance(_processorPerformance?.ReadDouble(noCap100: true));
            }

            var (gpuPercent, gpuDedicated) = SampleGpu();

            var processCount = Kernel32.GetPerformanceInfo(out var perf, (uint)Marshal.SizeOf<Kernel32.PERFORMANCE_INFORMATION>())
                ? (int)perf.ProcessCount
                : 0;

            return new SystemMetricsSample(
                _clock.UtcNow,
                cpu,
                total > 0 ? MetricMath.Clamp(used * 100d / total) : 0,
                used,
                total,
                diskActive,
                diskRead,
                diskWrite,
                gpuPercent,
                gpuDedicated is null ? null : (long)gpuDedicated.Value,
                netIn,
                netOut,
                processCount,
                performance);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _query?.Dispose();
            _gpuQuery?.Dispose();
            _query = null;
            _gpuQuery = null;
        }
    }

    private double SampleCpu()
    {
        if (!Kernel32.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var result = _hasPreviousTimes ? MetricMath.CpuPercent(_prevIdle, _prevKernel, _prevUser, idle, kernel, user) : 0;
        (_prevIdle, _prevKernel, _prevUser, _hasPreviousTimes) = (idle, kernel, user, true);
        return result;
    }

    private void EnsureCounters()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _query = PdhQuery.TryOpen();
            if (_query is not null)
            {
                _diskIdle = _query.TryAdd(DiskIdlePath);
                _diskRead = _query.TryAdd(DiskReadPath);
                _diskWrite = _query.TryAdd(DiskWritePath);
                _netReceived = _query.TryAdd(NetworkReceivedPath);
                _netSent = _query.TryAdd(NetworkSentPath);
                _processorPerformance = _query.TryAdd(ProcessorPerformancePath);
                LogMissing(_diskIdle, DiskIdlePath);
                LogMissing(_netReceived, NetworkReceivedPath);
            }
            CreateGpuQuery();
            _gpuJustCreated = _gpuQuery is not null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "PDH indisponible : disque, réseau et GPU ne seront pas mesurés");
            _query = null;
        }
    }

    private (double? Percent, double? Dedicated) SampleGpu()
    {
        if (_gpuJustCreated)
        {
            // Requête créée pendant cet appel : une seule collecte, le taux d'utilisation n'est pas encore mesurable.
            _gpuJustCreated = false;
            return (null, _gpuDedicated?.ReadInstances() is { } dedicated ? MetricMath.Sum(dedicated) : null);
        }
        if (Environment.TickCount64 - _gpuCreatedAt >= GpuRecreateIntervalMs)
        {
            // Dernière lecture sur l'ancienne requête (intervalle valide), puis nouvelle requête amorcée.
            var values = ReadGpu();
            CreateGpuQuery();
            return values;
        }
        return ReadGpu();
    }

    private (double? Percent, double? Dedicated) ReadGpu()
    {
        if (_gpuQuery is null || !_gpuQuery.Collect()) return (null, null);
        return (MetricMath.SumClamped(_gpu3D?.ReadInstances()), MetricMath.Sum(_gpuDedicated?.ReadInstances()));
    }

    private void CreateGpuQuery()
    {
        _gpuQuery?.Dispose();
        _gpuQuery = null;
        _gpu3D = _gpuDedicated = null;
        _gpuCreatedAt = Environment.TickCount64;

        var query = PdhQuery.TryOpen();
        if (query is null) return;
        _gpu3D = query.TryAdd(Gpu3DPath);
        _gpuDedicated = query.TryAdd(GpuDedicatedPath);
        if (_gpu3D is null && _gpuDedicated is null)
        {
            query.Dispose();
            return;
        }
        query.Collect();
        _gpuQuery = query;
    }

    private void LogMissing(PdhCounter? counter, string path)
    {
        if (counter is null) _logger.LogInformation("Compteur de performance absent : {Path}", path);
    }
}
