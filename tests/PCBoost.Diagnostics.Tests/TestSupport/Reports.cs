using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Models.SystemInfo;

namespace PCBoost.Diagnostics.Tests.TestSupport;

/// <summary>Construction de rapports d'analyse de test (base saine, modifications explicites).</summary>
internal static class Reports
{
    public static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>PC sain : chaque facteur du score obtient tous ses points, aucune règle ne se déclenche.</summary>
    public static SystemAnalysisReport Healthy() => new()
    {
        Timestamp = Now,
        Os = new OsInfo("Windows 11 Pro", "24H2", 26100, 1000, ProcessorArchitecture.X64, true, true, TimeSpan.FromHours(20), false),
        Cpu = new CpuInfo("Test CPU 8C", "GenuineIntel", 8, 16, 3000, 4500, 16384),
        Memory = new MemoryInfo(16L * ByteSize.GiB, 10L * ByteSize.GiB, 8L * ByteSize.GiB, 32L * ByteSize.GiB, 3200, 2),
        Gpus = [new GpuInfo("Test GPU", GpuVendor.Nvidia, 8L * ByteSize.GiB, 8L * ByteSize.GiB, "1.0", false, false)],
        Drives = [Drive(512L * ByteSize.GiB, 200L * ByteSize.GiB, StorageMediaType.Ssd)],
        Load = new LoadObservation(10, 25, 37.5, 5, 3, TimeSpan.FromSeconds(3), 6),
        Temperatures = TemperatureReadings.None,
        Power = new PowerStatus(PowerSource.AC, 100, true),
        ActivePowerScheme = new PowerScheme(PowerScheme.Balanced, "Utilisation normale"),
        InstalledProgramCount = 60,
        StartupEntries = StartupEntries(3, 2),
        RunningProcessCount = 150,
        BackgroundProcessCount = 40,
        CleanableBytes = 100 * ByteSize.MiB,
    };

    /// <summary>PC surchargé : la plupart des règles se déclenchent.</summary>
    public static SystemAnalysisReport Overloaded() => Healthy()
        .WithMemory(93, 8L * ByteSize.GiB)
        .WithCpu(92, 100)
        .WithDisk(97)
        .WithSystemDrive(4, 128L * ByteSize.GiB, StorageMediaType.Hdd)
        .WithStartup(16)
        .WithCleanable(6L * ByteSize.GiB)
        .WithUptime(TimeSpan.FromDays(20))
        .WithBackground(220)
        .WithTemperatures(95, 90, 70)
        .WithPowerSaverOnAc()
        .WithTopProcesses() with
    {
        Cpu = new CpuInfo("Old CPU", "GenuineIntel", 2, 4, 2000, 2600, 3072),
        Gpus = [new GpuInfo("Intel HD Graphics", GpuVendor.Intel, 128 * ByteSize.MiB, 2L * ByteSize.GiB, "1.0", false, true)],
        HardwareProfile = new HardwareProfile(HardwareTier.LegacyLowResource, [TextRef.Of("Diag_Profile_Reason_Ram", 8)], false, true, true, true, false),
    };

    /// <summary>Rapport où rien n'a pu être mesuré (sous-étapes en échec).</summary>
    public static SystemAnalysisReport Unmeasured() => new()
    {
        Timestamp = Now,
        Os = new OsInfo(string.Empty, string.Empty, 0, 0, ProcessorArchitecture.Unknown, false, false, TimeSpan.Zero, false),
        Cpu = new CpuInfo(string.Empty, string.Empty, 0, 0, null, null, null),
        Memory = new MemoryInfo(0, 0, 0, 0, null, null),
        Gpus = [],
        Drives = [],
        Load = new LoadObservation(0, 0, 0, null, null, TimeSpan.FromSeconds(3), 0),
        Temperatures = TemperatureReadings.None,
        Power = new PowerStatus(PowerSource.Unknown, null, false),
    };

    public static StorageDrive Drive(long total, long free, StorageMediaType media, string root = @"C:\")
        => new(root, "Windows", "NTFS", total, free, true, false, media, StorageBusType.Sata, "Disk");

    public static IReadOnlyList<StartupEntry> StartupEntries(int enabled, int disabled = 0, bool canDisable = false)
        => Enumerable.Range(0, enabled + disabled).Select(i => new StartupEntry
        {
            Id = $"run:{i}",
            Name = $"App {i}",
            Location = StartupLocation.RegistryRunUser,
            SourcePath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            ItemName = $"App{i}",
            IsEnabled = i < enabled,
            Recommendation = canDisable ? StartupRecommendation.CanDisable : StartupRecommendation.Optional,
        }).ToList();

    public static SystemAnalysisReport WithMemory(this SystemAnalysisReport r, double usedPercent, long? totalBytes = null)
    {
        var total = totalBytes ?? r.Memory.TotalBytes;
        var available = (long)(total * (100 - usedPercent) / 100);
        return r with
        {
            Memory = r.Memory with { TotalBytes = total, AvailableBytes = available },
            Load = r.Load with { MemoryUsedPercent = usedPercent },
        };
    }

    public static SystemAnalysisReport WithCpu(this SystemAnalysisReport r, double average, double? max = null)
        => r with { Load = r.Load with { CpuAveragePercent = average, CpuMaxPercent = max ?? average } };

    public static SystemAnalysisReport WithDisk(this SystemAnalysisReport r, double? activePercent)
        => r with { Load = r.Load with { DiskActiveAveragePercent = activePercent } };

    public static SystemAnalysisReport WithNoLoadSamples(this SystemAnalysisReport r)
        => r with { Load = new LoadObservation(0, 0, 0, null, null, TimeSpan.FromSeconds(3), 0) };

    public static SystemAnalysisReport WithSystemDrive(this SystemAnalysisReport r, double freePercent, long? totalBytes = null, StorageMediaType? media = null)
    {
        var current = r.SystemDrive ?? Drive(512L * ByteSize.GiB, 200L * ByteSize.GiB, StorageMediaType.Ssd);
        var total = totalBytes ?? current.TotalBytes;
        var drive = current with { TotalBytes = total, FreeBytes = (long)(total * freePercent / 100), MediaType = media ?? current.MediaType };
        return r with { Drives = [drive, .. r.Drives.Where(d => !d.IsSystemDrive)] };
    }

    public static SystemAnalysisReport WithoutDrives(this SystemAnalysisReport r) => r with { Drives = [] };

    public static SystemAnalysisReport WithStartup(this SystemAnalysisReport r, int enabled, int disabled = 0, bool canDisable = false)
        => r with { StartupEntries = StartupEntries(enabled, disabled, canDisable) };

    public static SystemAnalysisReport WithCleanable(this SystemAnalysisReport r, long? bytes) => r with { CleanableBytes = bytes };

    public static SystemAnalysisReport WithUptime(this SystemAnalysisReport r, TimeSpan uptime) => r with { Os = r.Os with { Uptime = uptime } };

    public static SystemAnalysisReport WithBackground(this SystemAnalysisReport r, int background, int running = 300)
        => r with { BackgroundProcessCount = background, RunningProcessCount = running };

    public static SystemAnalysisReport WithTemperatures(this SystemAnalysisReport r, double? cpu, double? gpu = null, double? storage = null)
        => r with { Temperatures = new TemperatureReadings(Reading(cpu), Reading(gpu), Reading(storage)) };

    public static SystemAnalysisReport WithPowerSaverOnAc(this SystemAnalysisReport r)
        => r with { ActivePowerScheme = new PowerScheme(PowerScheme.PowerSaver, "Économie d'énergie"), Power = new PowerStatus(PowerSource.AC, 100, true) };

    public static SystemAnalysisReport WithTopProcesses(this SystemAnalysisReport r) => r with
    {
        TopMemoryProcesses =
        [
            new ProcessUsage(100, "chrome.exe", @"C:\Program Files\Google\Chrome\chrome.exe", 5, 2L * ByteSize.GiB, true),
            new ProcessUsage(101, "Teams.exe", null, 2, 1L * ByteSize.GiB, true),
            new ProcessUsage(102, "OneDrive.exe", null, 1, 500 * ByteSize.MiB, true),
            new ProcessUsage(103, "notepad.exe", null, 0.1, 20 * ByteSize.MiB, true),
        ],
        TopCpuProcesses =
        [
            new ProcessUsage(200, "MsMpEng.exe", null, 45, 300 * ByteSize.MiB, false),
            new ProcessUsage(100, "chrome.exe", null, 5, 2L * ByteSize.GiB, true),
        ],
    };

    private static SensorReading Reading(double? value) => value is double v ? SensorReading.Of(v, "test") : SensorReading.Unavailable(Availability.NoSensor);
}
