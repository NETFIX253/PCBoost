using System.Buffers.Binary;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Startup;

namespace PCBoost.Optimization.Startup;

/// <summary>
/// Mécanisme officiel « StartupApproved » (celui du Gestionnaire des tâches) :
/// <c>…\Explorer\StartupApproved\Run</c> (HKCU/HKLM Run), <c>\Run32</c> (HKLM WOW64), <c>\StartupFolder</c> (nom de fichier).
/// Valeur absente = activé ; octet 0 pair (0x02, 0x06) = activé ; impair (0x01, 0x03, 0x07) = désactivé.
/// L'entrée elle-même (valeur Run, raccourci) n'est jamais supprimée ni modifiée.
/// </summary>
public static class StartupApproved
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Chemins HKLM identiques (casse comprise) à la liste blanche de l'Elevator.</summary>
    public const string MachineApprovedRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string MachineApprovedRun32 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    public const string MachineApprovedStartupFolder = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    public const string UserApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string UserApprovedStartupFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public const int ValueLength = 12;

    /// <summary>Emplacement de la clé Run lue pour un type d'entrée (null pour les dossiers et tâches).</summary>
    public static RegistryLocation? RunLocation(StartupLocation location) => location switch
    {
        StartupLocation.RegistryRunUser => new RegistryLocation(RegistryHiveKind.CurrentUser, RunKeyPath),
        StartupLocation.RegistryRunMachine => new RegistryLocation(RegistryHiveKind.LocalMachine, RunKeyPath, RegistryViewKind.Registry64),
        StartupLocation.RegistryRunMachine32 => new RegistryLocation(RegistryHiveKind.LocalMachine, RunKeyPath, RegistryViewKind.Registry32),
        _ => null,
    };

    /// <summary>Clé StartupApproved qui porte l'état d'une entrée (null pour une tâche planifiée).</summary>
    public static RegistryLocation? ApprovedLocation(StartupLocation location) => location switch
    {
        StartupLocation.RegistryRunUser => new RegistryLocation(RegistryHiveKind.CurrentUser, UserApprovedRun),
        StartupLocation.RegistryRunMachine => new RegistryLocation(RegistryHiveKind.LocalMachine, MachineApprovedRun, RegistryViewKind.Registry64),
        StartupLocation.RegistryRunMachine32 => new RegistryLocation(RegistryHiveKind.LocalMachine, MachineApprovedRun32, RegistryViewKind.Registry64),
        StartupLocation.StartupFolderUser => new RegistryLocation(RegistryHiveKind.CurrentUser, UserApprovedStartupFolder),
        StartupLocation.StartupFolderCommon => new RegistryLocation(RegistryHiveKind.LocalMachine, MachineApprovedStartupFolder, RegistryViewKind.Registry64),
        _ => null,
    };

    /// <summary>Interprète la valeur StartupApproved : absente ou non binaire = activé ; octet 0 impair = désactivé.</summary>
    public static bool IsEnabled(RegistryValueData? data)
    {
        if (data is null) return true;
        if (data.Type != RegistryValueType.Binary || data.Value is not byte[] bytes || bytes.Length == 0) return true;
        return (bytes[0] & 0x01) == 0;
    }

    /// <summary>Valeur « activé » écrite par le Gestionnaire des tâches : 12 octets, 02 00 … 00.</summary>
    public static byte[] EnabledValue()
    {
        var value = new byte[ValueLength];
        value[0] = 0x02;
        return value;
    }

    /// <summary>Valeur « désactivé » : 03 00 00 00 suivi de l'horodatage FILETIME UTC (8 octets, petit-boutiste).</summary>
    public static byte[] DisabledValue(DateTimeOffset nowUtc)
    {
        var value = new byte[ValueLength];
        value[0] = 0x03;
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(4), nowUtc.ToFileTime());
        return value;
    }
}
