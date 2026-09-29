using PCBoost.Core.Models.SystemInfo;

namespace PCBoost.Core.Abstractions.Platform;

/// <summary>Informations statiques ou lentes à obtenir sur le matériel et le système.</summary>
public interface ISystemInfoProvider
{
    OsInfo GetOsInfo();

    CpuInfo GetCpuInfo();

    MemoryInfo GetMemoryInfo();

    IReadOnlyList<GpuInfo> GetGpus();

    IReadOnlyList<StorageDrive> GetDrives();

    PowerStatus GetPowerStatus();

    /// <summary>Nombre de programmes installés (clés de désinstallation), null si illisible.</summary>
    int? GetInstalledProgramCount();
}

/// <summary>Échantillonnage de la charge système (PDH, GetSystemTimes…). Doit être peu coûteux.</summary>
public interface ISystemMetricsProvider : IDisposable
{
    /// <summary>
    /// Retourne un échantillon. Les valeurs de débit/pourcentage sont calculées depuis l'appel précédent ;
    /// le premier appel peut renvoyer des valeurs nulles pour ces champs.
    /// </summary>
    SystemMetricsSample Sample();
}

/// <summary>Capteurs matériels (températures). Aucune valeur n'est inventée.</summary>
public interface IHardwareProvider
{
    TemperatureReadings GetTemperatures();
}

public interface IPowerProvider
{
    PowerScheme? GetActiveScheme();

    IReadOnlyList<PowerScheme> GetSchemes();

    Common.OperationResult SetActiveScheme(Guid schemeId);
}

public interface IForegroundWindowProvider
{
    /// <summary>PID de la fenêtre au premier plan, ou null.</summary>
    int? GetForegroundProcessId();

    /// <summary>La fenêtre au premier plan couvre-t-elle tout son écran ?</summary>
    bool IsForegroundFullscreen();
}
