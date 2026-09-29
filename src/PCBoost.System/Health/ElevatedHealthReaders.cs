using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Models.Health;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform.Health;

/// <summary>
/// Lectures et actions réservées à l'assistant administrateur (PCBoost.Elevator). Aucune ne prend de paramètre fourni par
/// l'appelant : sources, requêtes et description du point de restauration sont fixes.
/// </summary>
internal static class ElevatedHealthReaders
{
    public const string DiagnosticsPerformanceChannel = "Microsoft-Windows-Diagnostics-Performance/Operational";
    public const string RestorePointDescription = "PCBoost - avant optimisation avancée";

    /// <summary>MODIFY_SETTINGS : modification de paramètres système (type documenté de SystemRestore.CreateRestorePoint).</summary>
    private const int ModifySettings = 12;
    /// <summary>BEGIN_SYSTEM_CHANGE, valeur utilisée par Checkpoint-Computer.</summary>
    private const int BeginSystemChange = 100;
    private const uint ErrorServiceDisabled = 1058;

    /// <summary>Windows ne crée pas plus d'un point de restauration par période de 24 heures (valeur par défaut).</summary>
    public static readonly TimeSpan RestorePointFrequency = TimeSpan.FromHours(24);

    public static IReadOnlyList<DiskReliability> ReadDiskReliability(DateTimeOffset now, ILogger logger)
    {
        var result = new List<DiskReliability>();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(Wmi.Storage), new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
            using var disks = searcher.Get();
            foreach (ManagementObject disk in disks)
            {
                using (disk)
                {
                    if (Wmi.String(disk, "DeviceId") is not { } id) continue;
                    using var counters = disk.GetRelated("MSFT_StorageReliabilityCounter");
                    foreach (var counter in counters)
                    {
                        using (counter)
                        {
                            result.Add(new DiskReliability(
                                id,
                                Wmi.Int32(counter, "Wear") is int w and >= 0 and <= 100 ? w : null,
                                Temperature(Wmi.Int32(counter, "Temperature")),
                                Temperature(Wmi.Int32(counter, "TemperatureMax")),
                                NonNegative(Wmi.Int64(counter, "PowerOnHours")),
                                NonNegative(Wmi.Int64(counter, "ReadErrorsTotal")),
                                NonNegative(Wmi.Int64(counter, "ReadErrorsUncorrected")),
                                NonNegative(Wmi.Int64(counter, "WriteErrorsTotal")),
                                now));
                            break;
                        }
                    }
                }
                if (result.Count >= HealthElevatedData.MaxDisks) break;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Compteurs de fiabilité illisibles");
        }
        return result;
    }

    public static (IReadOnlyList<BootRecord> Boots, IReadOnlyList<BootDegradation> Degradations, bool AccessDenied) ReadBootPerformance()
    {
        var boots = EventLogXmlReader.Read(DiagnosticsPerformanceChannel, "*[System[(EventID=100)]]", HealthElevatedData.MaxBoots);
        var degradations = EventLogXmlReader.Read(DiagnosticsPerformanceChannel, "*[System[(EventID>=101 and EventID<=110)]]", HealthElevatedData.MaxDegradations);
        return (
            boots.Events.Select(HealthEventParsers.BootRecord).OfType<BootRecord>().ToList(),
            degradations.Events.Select(HealthEventParsers.Degradation).OfType<BootDegradation>().ToList(),
            boots.AccessDenied && degradations.AccessDenied);
    }

    /// <summary>
    /// Crée un point de restauration puis vérifie qu'il existe. Si un point a été créé il y a moins de 24 heures, Windows
    /// n'en crée pas d'autre : ce point récent est signalé. Protection du système désactivée : rien n'est modifié.
    /// </summary>
    public static (RestorePointStatus Status, DateTimeOffset? CreatedAt) CreateRestorePoint(DateTimeOffset now, ILogger logger)
    {
        try
        {
            var before = LatestRestorePoint(logger);
            if (before is { } recent && now - recent.Time < RestorePointFrequency && recent.Time <= now.AddMinutes(5))
                return (RestorePointStatus.RecentExists, recent.Time);

            using var restore = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var input = restore.GetMethodParameters("CreateRestorePoint");
            input["Description"] = RestorePointDescription;
            input["RestorePointType"] = ModifySettings;
            input["EventType"] = BeginSystemChange;
            using var output = restore.InvokeMethod("CreateRestorePoint", input, null);
            var code = Convert.ToUInt32(output?["ReturnValue"] ?? uint.MaxValue, CultureInfo.InvariantCulture);
            if (code == ErrorServiceDisabled) return (RestorePointStatus.Disabled, null);
            if (code != 0)
            {
                logger.LogDebug("CreateRestorePoint : code {Code}", code);
                return (RestorePointStatus.Failed, null);
            }

            var after = LatestRestorePoint(logger);
            return after is { } created && (before is null || created.Sequence > before.Value.Sequence)
                ? (RestorePointStatus.Created, created.Time)
                : (RestorePointStatus.Failed, null);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException or FormatException or OverflowException)
        {
            logger.LogDebug(ex, "Point de restauration impossible");
            return (RestorePointStatus.Failed, null);
        }
    }

    private static (DateTimeOffset Time, long Sequence)? LatestRestorePoint(ILogger logger)
    {
        var points = Wmi.Query(@"root\default", "SELECT CreationTime, SequenceNumber FROM SystemRestore",
            o => Wmi.String(o, "CreationTime") is { } dmtf && Wmi.Int64(o, "SequenceNumber") is { } seq ? new PointBox(dmtf, seq) : null,
            logger);
        (DateTimeOffset Time, long Sequence)? latest = null;
        foreach (var point in points)
        {
            DateTimeOffset time;
            try
            {
                time = new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(point.Dmtf).ToUniversalTime(), TimeSpan.Zero);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }
            if (latest is null || point.Sequence > latest.Value.Sequence) latest = (time, point.Sequence);
        }
        return latest;
    }

    /// <summary>Fichiers « NOM.EXE-XXXXXXXX.pf » du dossier Prefetch : nom d'exécutable → dernière exécution.</summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> ReadLastRun(ILogger logger)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows)) return result;
        var prefetch = Path.Combine(windows, "Prefetch");
        try
        {
            foreach (var file in new DirectoryInfo(prefetch).EnumerateFiles("*.pf"))
            {
                if (PrefetchExecutable(file.Name) is not { } exe) continue;
                var time = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                if (!result.TryGetValue(exe, out var existing) || time > existing) result[exe] = time;
                if (result.Count >= HealthElevatedData.MaxPrefetchEntries) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogDebug(ex, "Dossier Prefetch illisible");
        }
        return result;
    }

    /// <summary>« VLC.EXE-2B3C4D5E.pf » → « vlc.exe » ; null si le nom ne suit pas ce format.</summary>
    public static string? PrefetchExecutable(string fileName)
    {
        if (!fileName.EndsWith(".pf", StringComparison.OrdinalIgnoreCase)) return null;
        var dash = fileName.LastIndexOf('-');
        if (dash <= 0) return null;
        var exe = fileName[..dash].ToLowerInvariant();
        return HealthElevatedData.IsExecutableName(exe) ? exe : null;
    }

    private static int? Temperature(int? value) => value is > 0 and < 150 ? value : null;

    private static long? NonNegative(long? value) => value is >= 0 ? value : null;

    private sealed record PointBox(string Dmtf, long Sequence);
}
