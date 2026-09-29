using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform.Health;

/// <summary>
/// État matériel lisible sans autorisation administrateur : santé des disques (MSFT_PhysicalDisk), batteries (pilote),
/// périphériques en erreur (Win32_PnPEntity), limitations de fréquence et démarrages (journal Système).
/// </summary>
public sealed class HardwareHealthProvider : IHardwareHealthProvider
{
    internal const string SystemChannel = "System";
    private const int MaxEvents = 500;

    private readonly ILogger<HardwareHealthProvider> _logger;

    public HardwareHealthProvider(ILogger<HardwareHealthProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<HardwareHealthProvider>.Instance;
    }

    public OperationResult<IReadOnlyList<DiskHealthInfo>> GetDisks()
    {
        var systemLetter = char.ToUpperInvariant(SystemInfoProvider.GetSystemDriveRoot()[0]);
        var systemDisks = Wmi.Query(Wmi.Storage,
            "SELECT DriveLetter, DiskNumber FROM MSFT_Partition",
            o => Wmi.Char(o, "DriveLetter") is { } l && char.ToUpperInvariant(l) == systemLetter && Wmi.Int64(o, "DiskNumber") is { } d
                ? d.ToString(CultureInfo.InvariantCulture)
                : null,
            _logger);

        var disks = Wmi.Query(Wmi.Storage,
            "SELECT DeviceId, FriendlyName, Model, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk",
            o => Wmi.String(o, "DeviceId") is { } id
                ? new DiskHealthInfo(
                    id,
                    Wmi.String(o, "FriendlyName") ?? Wmi.String(o, "Model") ?? id,
                    HardwareClassification.MediaTypeFromMsft(Wmi.Int32(o, "MediaType")),
                    HardwareClassification.BusTypeFromMsft(Wmi.Int32(o, "BusType")),
                    Wmi.Int64(o, "Size") ?? 0,
                    HealthClassification.DiskStatus(Wmi.Int32(o, "HealthStatus")),
                    systemDisks.Contains(id),
                    null)
                : null,
            _logger);

        return disks.Count == 0
            ? OperationResult<IReadOnlyList<DiskHealthInfo>>.Fail(OperationErrorKind.NotSupported)
            : OperationResult<IReadOnlyList<DiskHealthInfo>>.Ok(disks.OrderByDescending(d => d.IsSystemDisk).ThenBy(d => d.DeviceId, StringComparer.Ordinal).ToList());
    }

    public OperationResult<IReadOnlyList<BatteryInfo>> GetBatteries()
    {
        try
        {
            return BatteryReader.Read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Lecture des batteries impossible");
            return OperationResult<IReadOnlyList<BatteryInfo>>.Fail(OperationErrorKind.Failed);
        }
    }

    public OperationResult<IReadOnlyList<DeviceProblem>> GetDeviceProblems()
    {
        // La requête renvoie aussi une liste vide si WMI est indisponible : on vérifie que la classe répond.
        var reachable = Wmi.Query(Wmi.CimV2, "SELECT Name FROM Win32_ComputerSystem", o => Wmi.String(o, "Name") ?? string.Empty, _logger).Count > 0;
        if (!reachable) return OperationResult<IReadOnlyList<DeviceProblem>>.Fail(OperationErrorKind.NotSupported);

        var problems = Wmi.Query(Wmi.CimV2,
            "SELECT Name, PNPClass, ConfigManagerErrorCode, Manufacturer FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0",
            o => Wmi.Int32(o, "ConfigManagerErrorCode") is { } code && DeviceProblem.IsReportable(code)
                ? new DeviceProblem(
                    HealthEventParsers.Clean(Wmi.String(o, "Name")) ?? "?",
                    HealthEventParsers.Clean(Wmi.String(o, "PNPClass")),
                    code,
                    HealthEventParsers.Clean(Wmi.String(o, "Manufacturer")))
                : null,
            _logger);
        return OperationResult<IReadOnlyList<DeviceProblem>>.Ok(problems.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    public IReadOnlyList<DateTimeOffset> GetFirmwareLimitEvents(DateTimeOffset since)
    {
        var milliseconds = (long)Math.Clamp((DateTimeOffset.UtcNow - since).TotalMilliseconds, 0, TimeSpan.FromDays(365).TotalMilliseconds);
        var xpath = $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and (EventID=37) and TimeCreated[timediff(@SystemTime) <= {milliseconds.ToString(CultureInfo.InvariantCulture)}]]]";
        var result = EventLogXmlReader.Read(SystemChannel, xpath, MaxEvents);
        return result.Events.Select(HealthEventParsers.Load).OfType<System.Xml.Linq.XElement>()
            .Select(HealthEventParsers.TimeCreated).OfType<DateTimeOffset>().ToList();
    }

    public IReadOnlyList<BootSession> GetRecentBoots(int max)
    {
        if (max <= 0) return [];
        var boots = EventLogXmlReader.Read(SystemChannel,
            "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and (EventID=27)]]", max + 1);
        var logons = EventLogXmlReader.Read(SystemChannel,
            "*[System[Provider[@Name='Microsoft-Windows-Winlogon'] and (EventID=7001)]]", (max + 1) * 3);
        return HealthEventParsers.BootSessions(boots.Events, logons.Events, max);
    }
}

/// <summary>Classements purs (testables hors Windows).</summary>
internal static class HealthClassification
{
    /// <summary>MSFT_PhysicalDisk.HealthStatus : 0 = sain, 1 = avertissement, 2 = défaillant, 5 = inconnu.</summary>
    public static DiskHealthStatus DiskStatus(int? value) => value switch
    {
        0 => DiskHealthStatus.Healthy,
        1 => DiskHealthStatus.Warning,
        2 => DiskHealthStatus.Unhealthy,
        _ => DiskHealthStatus.Unknown,
    };
}
