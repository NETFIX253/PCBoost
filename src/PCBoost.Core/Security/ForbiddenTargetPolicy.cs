using PCBoost.Core.Abstractions.Platform;

namespace PCBoost.Core.Security;

/// <summary>
/// Politique absolue (§6, §58) : aucune modification ne peut viser les protections de sécurité,
/// Windows Update, le pare-feu, BitLocker, Secure Boot, le BCD ou les services système.
/// Appliquée par OptimizationSafetyValidator ET par PCBoost.Elevator (défense en profondeur).
/// </summary>
public static class ForbiddenTargetPolicy
{
    private static readonly string[] ForbiddenRegistryFragments =
    [
        @"\Windows Defender",
        @"\Microsoft Antimalware",
        @"\WindowsUpdate",
        @"\Windows Update",
        @"\WindowsFirewall",
        @"\SharedAccess\Parameters\FirewallPolicy",
        @"\SecurityHealthService",
        @"\SmartScreen",
        @"\Policies\Microsoft\Windows\System\EnableSmartScreen",
        @"\BitLocker",
        @"\FVE",
        @"\SecureBoot",
        @"\CurrentControlSet\Services",
        @"\CurrentControlSet\Control\DeviceGuard",
        @"\CurrentControlSet\Control\Session Manager\Memory Management",
        @"\CurrentControlSet\Control\Lsa",
        @"\BCD00000000",
        @"\Tcpip\Parameters",
        @"\Multimedia\SystemProfile",
        @"\Image File Execution Options",
        @"\Winlogon",
    ];

    private static readonly string[] ForbiddenFileFragments =
    [
        @"\Windows\System32\",
        @"\Windows\SysWOW64\",
        @"\Windows\WinSxS\",
        @"\Windows\Boot\",
        @"\Windows\servicing\",
        @"\ProgramData\Microsoft\Windows Defender\",
        @"\Windows\SoftwareDistribution\DataStore\",
        @"\System Volume Information\",
        @"\$Recycle.Bin\S-1-5-18",
        @"\Recovery\",
    ];

    /// <summary>Clés HKLM que l'Elevator est autorisé à modifier (liste blanche fermée).</summary>
    public static readonly IReadOnlyList<string> ElevatedRegistryAllowList =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
    ];

    public static bool IsForbiddenRegistryPath(string keyPath)
    {
        var normalized = @"\" + (keyPath ?? string.Empty).Replace('/', '\\').Trim('\\');
        return ForbiddenRegistryFragments.Any(f => normalized.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsForbiddenRegistryLocation(RegistryLocation location)
        => IsForbiddenRegistryPath(location.KeyPath);

    public static bool IsAllowedElevatedRegistryPath(string keyPath)
    {
        var normalized = (keyPath ?? string.Empty).Replace('/', '\\').Trim('\\');
        return ElevatedRegistryAllowList.Any(a => string.Equals(a, normalized, StringComparison.OrdinalIgnoreCase))
            && !IsForbiddenRegistryPath(normalized);
    }

    public static bool IsForbiddenFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var normalized = path.Replace('/', '\\');
        return ForbiddenFileFragments.Any(f => normalized.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Vérifie une cible textuelle de modification (format "HKCU\…", "HKLM\…", chemin de fichier, "power:{guid}"…).
    /// </summary>
    public static bool IsForbiddenTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        if (target.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) || target.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
            return IsForbiddenRegistryPath(target[5..]);
        if (target.Length > 2 && target[1] == ':' && (target[2] == '\\' || target[2] == '/'))
            return IsForbiddenFilePath(target);
        if (target.StartsWith("service:", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
