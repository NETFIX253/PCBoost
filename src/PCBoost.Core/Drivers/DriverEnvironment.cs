using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Drivers;

namespace PCBoost.Core.Drivers;

/// <summary>
/// Configuration de Windows qui conditionne les mises à jour de pilotes, lue dans le registre (lecture seule) :
/// stratégies de l'organisation, service Windows Update, redémarrage en attente, protection du système.
/// </summary>
public sealed record DriverEnvironment(
    bool DriversExcludedByPolicy,
    bool UpdateServiceDisabled,
    bool ManagedUpdateServer,
    bool RebootPending,
    SystemProtectionState Protection)
{
    /// <summary>Arrêt imposé par la configuration avant toute installation, ou <see cref="DriverInstallStop.None"/>.</summary>
    public DriverInstallStop InstallStop
        => DriversExcludedByPolicy ? DriverInstallStop.ExcludedByPolicy
            : UpdateServiceDisabled ? DriverInstallStop.UpdateServiceDisabled
            : RebootPending ? DriverInstallStop.RebootPending
            : Protection == SystemProtectionState.DisabledByPolicy ? DriverInstallStop.RestorePointUnavailable
            : DriverInstallStop.None;
}

/// <summary>
/// Lecture de <see cref="DriverEnvironment"/>, identique dans l'application (affichage, demande d'installation) et dans
/// l'assistant administrateur (revérification juste avant l'installation). PCBoost ne modifie aucune de ces valeurs.
/// </summary>
public static class DriverEnvironmentReader
{
    public static readonly RegistryLocation WindowsUpdatePolicy = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
    public static readonly RegistryLocation WindowsUpdateAuPolicy = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU");
    public static readonly RegistryLocation MdmUpdatePolicy = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\Microsoft\PolicyManager\current\device\Update");
    public static readonly RegistryLocation UpdateService = new(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\wuauserv");
    public static readonly RegistryLocation RebootRequiredKey = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
    public static readonly RegistryLocation RebootPendingKey = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
    public static readonly RegistryLocation SystemRestorePolicy = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore");
    public static readonly RegistryLocation SystemRestoreSettings = new(RegistryHiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");

    /// <summary>Valeur « Start » d'un service désactivé.</summary>
    private const int ServiceDisabled = 4;

    public static DriverEnvironment Read(IRegistryProvider registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var excluded = Dword(registry, WindowsUpdatePolicy, "ExcludeWUDriversInQualityUpdate") == 1
                       || Dword(registry, MdmUpdatePolicy, "ExcludeWUDriversInQualityUpdate") == 1;
        return new DriverEnvironment(
            excluded,
            Dword(registry, UpdateService, "Start") == ServiceDisabled,
            Dword(registry, WindowsUpdateAuPolicy, "UseWUServer") == 1,
            registry.KeyExists(RebootRequiredKey) || registry.KeyExists(RebootPendingKey),
            ReadProtection(registry));
    }

    public static SystemProtectionState ReadProtection(IRegistryProvider registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (Dword(registry, SystemRestorePolicy, "DisableSR") == 1) return SystemProtectionState.DisabledByPolicy;
        return Dword(registry, SystemRestoreSettings, "RPSessionInterval") switch
        {
            0 => SystemProtectionState.Disabled,
            > 0 => SystemProtectionState.Enabled,
            _ => SystemProtectionState.Unknown,
        };
    }

    private static int? Dword(IRegistryProvider registry, RegistryLocation location, string name)
        => registry.GetValue(location, name) is { Type: RegistryValueType.DWord, Value: int value } ? value : null;
}
