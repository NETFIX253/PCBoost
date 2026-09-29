using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>
/// Informations matérielles et système (registre, WMI, DXGI, Win32). Les données statiques sont mises en cache :
/// processeur, GPU, barrettes mémoire et programmes installés 10 min, disques 30 s.
/// </summary>
public sealed class SystemInfoProvider : ISystemInfoProvider
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string CentralProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly TimeSpan StaticLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DriveLifetime = TimeSpan.FromSeconds(30);

    private readonly ILogger<SystemInfoProvider> _logger;
    private readonly TimedCache<CpuInfo> _cpu;
    private readonly TimedCache<IReadOnlyList<GpuInfo>> _gpus;
    private readonly TimedCache<(int? SpeedMHz, int? ModuleCount)> _memoryModules;
    private readonly TimedCache<IReadOnlyList<StorageDrive>> _drives;
    private readonly TimedCache<int?> _installedPrograms;

    public SystemInfoProvider(ILogger<SystemInfoProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<SystemInfoProvider>.Instance;
        _cpu = new(StaticLifetime, ReadCpu);
        _gpus = new(StaticLifetime, ReadGpus);
        _memoryModules = new(StaticLifetime, ReadMemoryModules);
        _drives = new(DriveLifetime, ReadDrives);
        _installedPrograms = new(StaticLifetime, ReadInstalledProgramCount);
    }

    public OsInfo GetOsInfo()
    {
        string? productName = null, displayVersion = null;
        int build = Environment.OSVersion.Version.Build, ubr = 0;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(CurrentVersionKey);
            if (key is not null)
            {
                productName = key.GetValue("ProductName") as string;
                displayVersion = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string;
                if (int.TryParse(key.GetValue("CurrentBuildNumber") as string ?? key.GetValue("CurrentBuild") as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b > 0)
                    build = b;
                if (key.GetValue("UBR") is int u) ubr = u;
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "Lecture de CurrentVersion impossible");
        }

        return new OsInfo(
            HardwareClassification.CorrectProductName(productName, build),
            displayVersion?.Trim() ?? string.Empty,
            build,
            ubr,
            HardwareClassification.ArchitectureFrom(RuntimeInformation.OSArchitecture),
            Environment.Is64BitOperatingSystem,
            build >= HardwareClassification.Windows11FirstBuild,
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            TokenHelper.IsCurrentProcessElevated);
    }

    public CpuInfo GetCpuInfo() => _cpu.Get();

    public MemoryInfo GetMemoryInfo()
    {
        var status = new Kernel32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Kernel32.MEMORYSTATUSEX>() };
        long total = 0, available = 0;
        if (Kernel32.GlobalMemoryStatusEx(ref status))
        {
            total = (long)status.ullTotalPhys;
            available = (long)status.ullAvailPhys;
        }

        long commitTotal = 0, commitLimit = 0;
        if (Kernel32.GetPerformanceInfo(out var perf, (uint)Marshal.SizeOf<Kernel32.PERFORMANCE_INFORMATION>()))
        {
            var pageSize = (long)perf.PageSize;
            commitTotal = (long)perf.CommitTotal * pageSize;
            commitLimit = (long)perf.CommitLimit * pageSize;
        }

        var (speed, modules) = _memoryModules.Get();
        return new MemoryInfo(total, available, commitTotal, commitLimit, speed, modules);
    }

    public IReadOnlyList<GpuInfo> GetGpus() => _gpus.Get();

    public IReadOnlyList<StorageDrive> GetDrives() => _drives.Get();

    public PowerStatus GetPowerStatus()
    {
        if (!Kernel32.GetSystemPowerStatus(out var status))
            return new PowerStatus(PowerSource.Unknown, null, false);

        // BatteryFlag : 128 = pas de batterie système, 255 = état inconnu.
        var hasBattery = status.BatteryFlag != 255 && (status.BatteryFlag & 128) == 0;
        var source = status.ACLineStatus switch
        {
            1 => PowerSource.AC,
            0 => PowerSource.Battery,
            _ => PowerSource.Unknown,
        };
        int? percent = hasBattery && status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;
        return new PowerStatus(source, percent, hasBattery);
    }

    public int? GetInstalledProgramCount() => _installedPrograms.Get();

    private CpuInfo ReadCpu()
    {
        var processors = Wmi.Query(Wmi.CimV2,
            "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L3CacheSize FROM Win32_Processor",
            o => new
            {
                Name = Wmi.String(o, "Name"),
                Manufacturer = Wmi.String(o, "Manufacturer"),
                Cores = Wmi.Int32(o, "NumberOfCores"),
                Logical = Wmi.Int32(o, "NumberOfLogicalProcessors"),
                Clock = Wmi.Int32(o, "MaxClockSpeed"),
                L3 = Wmi.Int32(o, "L3CacheSize"),
            },
            _logger);

        string? registryName = null, registryVendor = null;
        int? registryMHz = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CentralProcessorKey);
            registryName = (key?.GetValue("ProcessorNameString") as string)?.Trim();
            registryVendor = (key?.GetValue("VendorIdentifier") as string)?.Trim();
            if (key?.GetValue("~MHz") is int mhz && mhz > 0) registryMHz = mhz;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "Lecture du processeur dans le registre impossible");
        }

        var first = processors.FirstOrDefault();
        var cores = processors.Sum(p => p.Cores ?? 0);
        var logical = processors.Sum(p => p.Logical ?? 0);
        if (cores <= 0) cores = CountPhysicalCores() ?? 0;
        if (logical <= 0) logical = Environment.ProcessorCount;
        if (cores <= 0) cores = logical;
        var l3 = processors.Sum(p => p.L3 ?? 0);

        // Win32_Processor.MaxClockSpeed correspond en pratique à la fréquence nominale ; la fréquence maximale
        // (turbo) n'est pas exposée de manière fiable par Windows : elle reste inconnue plutôt qu'estimée.
        var baseClock = first?.Clock is > 0 ? first.Clock : registryMHz;
        return new CpuInfo(
            first?.Name ?? registryName ?? "Processeur",
            first?.Manufacturer ?? registryVendor ?? string.Empty,
            cores,
            logical,
            baseClock,
            null,
            l3 > 0 ? l3 : null);
    }

    private static unsafe int? CountPhysicalCores()
    {
        uint length = 0;
        Kernel32.GetLogicalProcessorInformationEx(Kernel32.RelationProcessorCore, null, ref length);
        if (length == 0 || length > 1 << 20) return null;
        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            if (!Kernel32.GetLogicalProcessorInformationEx(Kernel32.RelationProcessorCore, p, ref length)) return null;
            var count = 0;
            uint offset = 0;
            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX : { int Relationship; DWORD Size; union … } de taille variable.
            while (offset + 8 <= length)
            {
                var size = *(uint*)(p + offset + 4);
                if (size == 0) break;
                if (*(int*)(p + offset) == Kernel32.RelationProcessorCore) count++;
                offset += size;
            }
            return count > 0 ? count : null;
        }
    }

    private (int? SpeedMHz, int? ModuleCount) ReadMemoryModules()
    {
        var modules = Wmi.Query(Wmi.CimV2,
            "SELECT Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory",
            o => new { Speed = Wmi.Int32(o, "Speed"), Configured = Wmi.Int32(o, "ConfiguredClockSpeed") },
            _logger);
        if (modules.Count == 0) return (null, null);
        var speeds = modules.Select(m => m.Configured is > 0 ? m.Configured : m.Speed).Where(s => s is > 0).Select(s => s!.Value).ToList();
        return (speeds.Count > 0 ? speeds.Min() : null, modules.Count);
    }

    private IReadOnlyList<GpuInfo> ReadGpus()
    {
        List<DxgiAdapter> adapters;
        try
        {
            adapters = DxgiAdapterReader.Enumerate();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            _logger.LogDebug(ex, "DXGI indisponible");
            adapters = [];
        }

        var controllers = Wmi.Query(Wmi.CimV2,
            "SELECT Name, DriverVersion, PNPDeviceID FROM Win32_VideoController",
            o => new { Name = Wmi.String(o, "Name"), Driver = Wmi.String(o, "DriverVersion"), Ids = HardwareClassification.ParsePciIds(Wmi.String(o, "PNPDeviceID")) },
            _logger);

        string? DriverFor(DxgiAdapter adapter)
            => controllers.FirstOrDefault(c => c.Ids is { } ids && ids.VendorId == adapter.VendorId && ids.DeviceId == adapter.DeviceId)?.Driver
               ?? controllers.FirstOrDefault(c => string.Equals(c.Name, adapter.Description, StringComparison.OrdinalIgnoreCase))?.Driver;

        GpuInfo ToInfo(DxgiAdapter a)
        {
            var vendor = HardwareClassification.VendorFromPciId(a.VendorId);
            return new GpuInfo(
                a.Description.Length > 0 ? a.Description : "GPU",
                vendor,
                a.DedicatedVideoMemory,
                a.SharedSystemMemory,
                DriverFor(a),
                a.IsSoftware,
                !a.IsSoftware && HardwareClassification.IsLikelyIntegrated(vendor, a.DedicatedVideoMemory));
        }

        var hardware = adapters.Where(a => !a.IsSoftware).Select(ToInfo).ToList();
        if (hardware.Count > 0) return hardware;

        // Aucun GPU matériel : le pilote d'affichage de base (logiciel) est signalé comme tel.
        return adapters.Where(a => a.IsSoftware).Take(1).Select(ToInfo).ToList();
    }

    private IReadOnlyList<StorageDrive> ReadDrives()
    {
        var partitions = Wmi.Query(Wmi.Storage,
            "SELECT DriveLetter, DiskNumber FROM MSFT_Partition",
            o => Wmi.Char(o, "DriveLetter") is { } letter && Wmi.Int32(o, "DiskNumber") is { } disk
                ? new { Letter = char.ToUpperInvariant(letter), Disk = disk }
                : null,
            _logger);
        var disks = Wmi.Query(Wmi.Storage,
            "SELECT DeviceId, MediaType, BusType, FriendlyName, Model FROM MSFT_PhysicalDisk",
            o => int.TryParse(Wmi.String(o, "DeviceId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                ? new
                {
                    Id = id,
                    Media = HardwareClassification.MediaTypeFromMsft(Wmi.Int32(o, "MediaType")),
                    Bus = HardwareClassification.BusTypeFromMsft(Wmi.Int32(o, "BusType")),
                    Model = Wmi.String(o, "FriendlyName") ?? Wmi.String(o, "Model"),
                }
                : null,
            _logger);

        var systemRoot = GetSystemDriveRoot();
        var result = new List<StorageDrive>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Énumération des lecteurs impossible");
            return result;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
                var root = drive.RootDirectory.FullName;
                var letter = root.Length > 0 ? char.ToUpperInvariant(root[0]) : '\0';
                var diskNumber = partitions.FirstOrDefault(p => p.Letter == letter)?.Disk;
                var disk = diskNumber is null ? null : disks.FirstOrDefault(d => d.Id == diskNumber);
                var bus = disk?.Bus ?? StorageBusType.Unknown;
                string? label = drive.VolumeLabel;
                result.Add(new StorageDrive(
                    root,
                    string.IsNullOrWhiteSpace(label) ? null : label,
                    drive.DriveFormat,
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase),
                    drive.DriveType == DriveType.Removable || bus == StorageBusType.Usb,
                    disk?.Media ?? StorageMediaType.Unknown,
                    bus,
                    disk?.Model));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                _logger.LogDebug(ex, "Lecteur ignoré");
            }
        }
        return result;
    }

    internal static string GetSystemDriveRoot()
    {
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (string.IsNullOrEmpty(systemDrive))
            systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (string.IsNullOrEmpty(systemDrive)) return @"C:\";
        return systemDrive.TrimEnd('\\') + "\\";
    }

    private int? ReadInstalledProgramCount()
    {
        var entries = new List<UninstallEntry>();
        var readAny = false;
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallKey);
                if (uninstall is null) continue;
                readAny = true;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var entry = uninstall.OpenSubKey(name);
                        if (entry is null) continue;
                        entries.Add(new UninstallEntry(
                            entry.GetValue("DisplayName") as string,
                            entry.GetValue("SystemComponent") as int?,
                            entry.GetValue("ParentKeyName") as string,
                            entry.GetValue("ReleaseType") as string));
                    }
                    catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                    {
                        // Entrée illisible : ignorée.
                    }
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                _logger.LogDebug(ex, "Clé de désinstallation illisible ({Hive}, {View})", hive, view);
            }
        }
        return readAny ? InstalledProgramFilter.CountDistinct(entries) : null;
    }
}
