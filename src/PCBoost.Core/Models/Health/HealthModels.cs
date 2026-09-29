using PCBoost.Core.Common;
using PCBoost.Core.Models.SystemInfo;

namespace PCBoost.Core.Models.Health;

/// <summary>État de santé déclaré par Windows pour un disque physique (MSFT_PhysicalDisk.HealthStatus).</summary>
public enum DiskHealthStatus { Unknown = 0, Healthy, Warning, Unhealthy }

/// <summary>
/// Compteurs de fiabilité d'un disque (MSFT_StorageReliabilityCounter), lisibles uniquement avec une autorisation
/// administrateur. Chaque valeur est null si le disque ne la fournit pas.
/// </summary>
public sealed record DiskReliability(
    string DeviceId,
    int? WearPercent,
    int? TemperatureCelsius,
    int? TemperatureMaxCelsius,
    long? PowerOnHours,
    long? ReadErrorsTotal,
    long? ReadErrorsUncorrected,
    long? WriteErrorsTotal,
    DateTimeOffset MeasuredAt)
{
    public bool HasAnyValue => WearPercent.HasValue || TemperatureCelsius.HasValue || PowerOnHours.HasValue
        || ReadErrorsTotal.HasValue || ReadErrorsUncorrected.HasValue || WriteErrorsTotal.HasValue;
}

/// <summary>Disque physique et son état de santé.</summary>
public sealed record DiskHealthInfo(
    string DeviceId,
    string FriendlyName,
    StorageMediaType MediaType,
    StorageBusType BusType,
    long SizeBytes,
    DiskHealthStatus Status,
    bool IsSystemDisk,
    DiskReliability? Reliability)
{
    /// <summary>Usure au-delà de laquelle un SSD approche de sa fin de vie (pourcentage d'endurance consommée).</summary>
    public const int WearCriticalPercent = 90;
    public const int WearWarningPercent = 70;

    public bool HasUncorrectedErrors => Reliability?.ReadErrorsUncorrected is > 0;

    public bool IsCritical => Status == DiskHealthStatus.Unhealthy || HasUncorrectedErrors || Reliability?.WearPercent >= WearCriticalPercent;

    public bool IsWarning => !IsCritical && (Status == DiskHealthStatus.Warning || Reliability?.WearPercent >= WearWarningPercent);
}

/// <summary>
/// Batterie d'un portable (IOCTL_BATTERY_QUERY_INFORMATION). Capacités en mWh, sauf si <paramref name="CapacityIsRelative"/> :
/// le pilote ne fournit alors que des unités relatives (seul le rapport entre les deux capacités a un sens).
/// </summary>
public sealed record BatteryInfo(
    string? Name,
    string? Manufacturer,
    string? Chemistry,
    long? DesignCapacityMWh,
    long? FullChargeCapacityMWh,
    int? CycleCount,
    bool CapacityIsRelative = false)
{
    public const double WornPercent = 60;
    public const double AgingPercent = 80;

    /// <summary>Capacité actuelle rapportée à la capacité d'origine (plafonnée à 100 %), null si l'une manque.</summary>
    public double? HealthPercent => DesignCapacityMWh is > 0 && FullChargeCapacityMWh is > 0
        ? Math.Min(100, FullChargeCapacityMWh.Value * 100d / DesignCapacityMWh.Value)
        : null;
}

/// <summary>Périphérique signalé en erreur par le Gestionnaire de périphériques (code de problème ≠ 0).</summary>
public sealed record DeviceProblem(string Name, string? DeviceClass, int ProblemCode, string? Manufacturer)
{
    /// <summary>Codes exclus : 22 = désactivé volontairement, 45 = périphérique non connecté.</summary>
    public static bool IsReportable(int problemCode) => problemCode is not (0 or 22 or 45);
}

/// <summary>
/// Limitation de la vitesse du processeur : événements Windows (Kernel-Processor-Power 37, « vitesse limitée par le
/// microprogramme ») et épisodes observés par PCBoost (charge élevée avec fréquence nettement réduite).
/// </summary>
public sealed record ThermalLimitInfo(
    int FirmwareLimitEvents,
    DateTimeOffset? LastFirmwareLimitEvent,
    int ObservedEpisodes,
    ThrottlingEpisode? LastEpisode);

/// <summary>Épisode observé : charge processeur élevée pendant que la performance du processeur restait basse.</summary>
public sealed record ThrottlingEpisode(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    double AverageCpuPercent,
    double AverageProcessorPerformancePercent,
    double? MaxCpuTemperatureC);

public enum BootKind { Unknown = 0, Cold, FastStartup, Resume }

/// <summary>Démarrage relevé dans le journal Système (lisible sans autorisation administrateur).</summary>
public sealed record BootSession(DateTimeOffset StartedAt, BootKind Kind, DateTimeOffset? UserLogonAt);

/// <summary>Mesure Windows d'un démarrage (Diagnostics-Performance, événement 100). Durées hors saisie du mot de passe.</summary>
public sealed record BootRecord(
    DateTimeOffset Timestamp,
    TimeSpan BootTime,
    TimeSpan MainPathBootTime,
    TimeSpan PostBootTime,
    int? StartupAppCount);

public enum BootDegradationKind { Application = 0, Driver, Service, Other }

/// <summary>Élément signalé par Windows comme ayant ralenti un démarrage (événements 101 à 110).</summary>
public sealed record BootDegradation(
    DateTimeOffset Timestamp,
    BootDegradationKind Kind,
    string Name,
    string? FileName,
    TimeSpan TotalTime,
    TimeSpan DegradationTime);

/// <summary>Moyennes des démarrages mesurés avant et après la dernière modification des programmes au démarrage.</summary>
public sealed record BootComparison(DateTimeOffset ChangedAt, TimeSpan AverageBefore, int BootsBefore, TimeSpan AverageAfter, int BootsAfter)
{
    public TimeSpan Difference => AverageAfter - AverageBefore;
}

/// <summary>Mesures Windows du démarrage lues avec autorisation administrateur, conservées localement.</summary>
public sealed record BootPerformanceData(DateTimeOffset ReadAt, IReadOnlyList<BootRecord> Boots, IReadOnlyList<BootDegradation> Degradations);

/// <summary>État de santé matériel à un instant donné. Chaque partie peut être vide ou indisponible.</summary>
public sealed record HardwareHealthReport(
    DateTimeOffset Timestamp,
    IReadOnlyList<DiskHealthInfo> Disks,
    Availability DiskAvailability,
    DateTimeOffset? ReliabilityMeasuredAt,
    IReadOnlyList<BatteryInfo> Batteries,
    Availability BatteryAvailability,
    IReadOnlyList<DeviceProblem> DeviceProblems,
    Availability DeviceAvailability,
    ThermalLimitInfo Thermal)
{
    public static HardwareHealthReport Empty(DateTimeOffset timestamp) => new(
        timestamp, [], Availability.Unavailable, null, [], Availability.Unavailable, [], Availability.Unavailable,
        new ThermalLimitInfo(0, null, 0, null));
}

/// <summary>Durée de démarrage : démarrages récents (journal Système) et mesures Windows lues avec autorisation.</summary>
public sealed record BootTimeReport(
    IReadOnlyList<BootSession> Sessions,
    BootPerformanceData? Measurements,
    DateTimeOffset? LastStartupChangeAt,
    BootComparison? Comparison)
{
    public BootRecord? LatestBoot => Measurements?.Boots.Count > 0 ? Measurements.Boots[0] : null;

    /// <summary>La majorité des démarrages récents sont des « démarrages rapides » : Windows ne les mesure pas comme des démarrages complets.</summary>
    public bool MostlyFastStartup => Sessions.Count > 0 && Sessions.Count(s => s.Kind == BootKind.FastStartup) * 2 > Sessions.Count;
}

public enum RestorePointStatus { Created = 0, RecentExists, Disabled, Failed }

/// <summary>Résultat de la création d'un point de restauration Windows.</summary>
public sealed record RestorePointResult(RestorePointStatus Status, DateTimeOffset? PointCreatedAt, TextRef? Message)
{
    /// <summary>Un point de restauration exploitable existe (créé à l'instant ou récent).</summary>
    public bool IsAvailable => Status is RestorePointStatus.Created or RestorePointStatus.RecentExists;
}
