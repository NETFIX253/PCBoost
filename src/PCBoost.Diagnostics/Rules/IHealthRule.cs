using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Settings;

namespace PCBoost.Diagnostics.Rules;

/// <summary>
/// Règle de santé (§26). Enregistrez une implémentation supplémentaire en DI (<c>IEnumerable&lt;IHealthRule&gt;</c>)
/// pour étendre le moteur. Une règle ne produit un constat que si la mesure est disponible ; elle ne suppose jamais de valeur.
/// </summary>
public interface IHealthRule
{
    /// <summary>Identifiant stable (reporté dans <see cref="HealthFinding.RuleId"/>).</summary>
    string Id { get; }

    /// <summary>Évalue le rapport ; <c>null</c> si rien à signaler ou si la mesure est absente.</summary>
    HealthFinding? Evaluate(SystemAnalysisReport report, HealthThresholds thresholds);
}

/// <summary>Identifiants stables des règles intégrées.</summary>
public static class HealthRuleIds
{
    public const string Memory = "health.memory";
    public const string SystemDriveFreeSpace = "health.storage.system-free";
    public const string StartupCount = "health.startup.count";
    public const string CpuSustained = "health.cpu.sustained";
    public const string DiskActive = "health.disk.active";
    public const string CpuTemperature = "health.thermal.cpu";
    public const string GpuTemperature = "health.thermal.gpu";
    public const string StorageTemperature = "health.thermal.storage";
    public const string Uptime = "health.system.uptime";
    public const string Cleanable = "health.cleanup.recoverable";
    public const string BackgroundProcesses = "health.processes.background";
    public const string PowerSaverOnAc = "health.power.saver-on-ac";
    public const string UnsupportedBuild = "health.system.unsupported-build";
    public const string DiskHealth = "health.hardware.disk";
    public const string BatteryWear = "health.hardware.battery";
    public const string DeviceProblems = "health.hardware.devices";
    public const string ThermalLimit = "health.hardware.thermal-limit";
}

/// <summary>Catégories des constats (<see cref="HealthFinding.Category"/>).</summary>
public static class HealthCategories
{
    public const string Memory = "memory";
    public const string Storage = "storage";
    public const string Startup = "startup";
    public const string Cpu = "cpu";
    public const string Disk = "disk";
    public const string Thermal = "thermal";
    public const string System = "system";
    public const string Cleanup = "cleanup";
    public const string Processes = "processes";
    public const string Power = "power";
    public const string Hardware = "hardware";
}
