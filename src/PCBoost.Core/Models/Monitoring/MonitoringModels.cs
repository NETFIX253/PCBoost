namespace PCBoost.Core.Models.Monitoring;

public enum MonitoringMode
{
    /// <summary>Fenêtre visible sur une page de métriques : 1 s.</summary>
    Active = 0,
    /// <summary>Application en arrière-plan : 5 s.</summary>
    Background = 1,
    /// <summary>Surveillance suspendue par l'utilisateur.</summary>
    Paused = 2,
}

public enum MetricKind { Cpu = 0, Memory, Disk, Gpu, NetworkReceive, NetworkSend, CpuTemperature, GpuTemperature }

public sealed record MetricStatistics(MetricKind Metric, double? Current, double? Average, double? Maximum, int SampleCount);

/// <summary>Instantané persisté pour l'historique (§66).</summary>
public sealed record PerformanceSnapshot(
    DateTimeOffset Timestamp,
    double CpuPercent,
    double MemoryPercent,
    double? DiskActivePercent,
    double? GpuPercent,
    double? CpuTemperatureC,
    double? GpuTemperatureC,
    double? Fps,
    double? FrameTimeMs);
