using PCBoost.Core.Common;

namespace PCBoost.Core.Models.SystemInfo;

public enum ProcessorArchitecture { Unknown = 0, X86, X64, Arm64 }

public sealed record OsInfo(
    string ProductName,
    string DisplayVersion,
    int BuildNumber,
    int UpdateBuildRevision,
    ProcessorArchitecture Architecture,
    bool Is64BitOperatingSystem,
    bool IsWindows11,
    TimeSpan Uptime,
    bool IsElevated)
{
    /// <summary>Windows 10 1809 (17763) est la version minimale prise en charge.</summary>
    public const int MinimumSupportedBuild = 17763;

    public bool IsSupported => BuildNumber >= MinimumSupportedBuild;
}

public sealed record CpuInfo(
    string Name,
    string Manufacturer,
    int PhysicalCores,
    int LogicalProcessors,
    int? BaseClockMHz,
    int? MaxClockMHz,
    int? L3CacheKB);

public sealed record MemoryInfo(
    long TotalBytes,
    long AvailableBytes,
    long CommitTotalBytes,
    long CommitLimitBytes,
    int? SpeedMHz,
    int? ModuleCount)
{
    public long UsedBytes => Math.Max(0, TotalBytes - AvailableBytes);
    public double UsedPercent => TotalBytes <= 0 ? 0 : UsedBytes * 100d / TotalBytes;
}

public enum GpuVendor { Unknown = 0, Nvidia, Amd, Intel, Microsoft, Qualcomm, Other }

public sealed record GpuInfo(
    string Name,
    GpuVendor Vendor,
    long? DedicatedVideoMemoryBytes,
    long? SharedSystemMemoryBytes,
    string? DriverVersion,
    bool IsSoftwareAdapter,
    bool IsLikelyIntegrated);

public enum StorageMediaType { Unknown = 0, Hdd, Ssd, Scm }

public enum StorageBusType { Unknown = 0, Sata, Nvme, Usb, Sas, Raid, Virtual, Other }

public sealed record StorageDrive(
    string RootPath,
    string? Label,
    string? FileSystem,
    long TotalBytes,
    long FreeBytes,
    bool IsSystemDrive,
    bool IsRemovable,
    StorageMediaType MediaType,
    StorageBusType BusType,
    string? Model)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double FreePercent => TotalBytes <= 0 ? 0 : FreeBytes * 100d / TotalBytes;
}

public enum PowerSource { Unknown = 0, AC, Battery }

public sealed record PowerStatus(PowerSource Source, int? BatteryPercent, bool HasBattery);

public sealed record PowerScheme(Guid Id, string Name)
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
}

/// <summary>Échantillon de charge système (moniteur temps réel).</summary>
public sealed record SystemMetricsSample(
    DateTimeOffset Timestamp,
    double CpuPercent,
    double MemoryUsedPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    double? DiskActivePercent,
    double? DiskReadBytesPerSec,
    double? DiskWriteBytesPerSec,
    double? GpuPercent,
    long? GpuDedicatedMemoryUsedBytes,
    double? NetworkReceiveBytesPerSec,
    double? NetworkSendBytesPerSec,
    int ProcessCount);

/// <summary>Températures : chaque valeur peut être indisponible (§27).</summary>
public sealed record TemperatureReadings(SensorReading Cpu, SensorReading Gpu, SensorReading Storage)
{
    public static TemperatureReadings None { get; } = new(
        SensorReading.Unavailable(Availability.NoSensor),
        SensorReading.Unavailable(Availability.NoSensor),
        SensorReading.Unavailable(Availability.NoSensor));
}

public enum ProcessPriority { Idle = 0, BelowNormal, Normal, AboveNormal, High, RealTime, Unknown }

/// <summary>Instantané brut d'un processus. <c>Name</c> = nom d'image avec extension (« chrome.exe ») ; pseudo-processus sans extension (System, Idle, Registry).</summary>
public sealed record ProcessSnapshot(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    long WorkingSetBytes,
    long PrivateBytes,
    TimeSpan TotalProcessorTime,
    long IoReadBytes,
    long IoWriteBytes,
    int SessionId,
    DateTimeOffset? StartTime,
    ProcessPriority Priority,
    bool HasMainWindow,
    string? MainWindowTitle,
    bool IsCurrentUser);

/// <summary>Identité légère d'un processus (surveillance peu coûteuse). Nom d'image avec extension.</summary>
public readonly record struct ProcessIdentity(int ProcessId, string Name);
