using System.Globalization;
using System.Runtime.InteropServices;
using PCBoost.Core.Common;
using PCBoost.Core.Models.SystemInfo;

namespace PCBoost.Platform;

/// <summary>Règles de classement pures (testables hors Windows) utilisées par <see cref="SystemInfoProvider"/>.</summary>
internal static class HardwareClassification
{
    public const int Windows11FirstBuild = 22000;

    public const uint VendorNvidia = 0x10DE;
    public const uint VendorAmd = 0x1002;
    public const uint VendorIntel = 0x8086;
    public const uint VendorMicrosoft = 0x1414;
    public const uint VendorQualcomm = 0x5143;

    public static GpuVendor VendorFromPciId(uint vendorId) => vendorId switch
    {
        VendorNvidia => GpuVendor.Nvidia,
        VendorAmd => GpuVendor.Amd,
        VendorIntel => GpuVendor.Intel,
        VendorMicrosoft => GpuVendor.Microsoft,
        VendorQualcomm => GpuVendor.Qualcomm,
        0 => GpuVendor.Unknown,
        _ => GpuVendor.Other,
    };

    /// <summary>
    /// Intel et Qualcomm fournissent surtout des GPU intégrés (sauf Intel Arc, doté de plusieurs Go de mémoire dédiée) ;
    /// ailleurs, moins de 512 Mo de mémoire dédiée indique une puce intégrée (APU).
    /// </summary>
    public static bool IsLikelyIntegrated(GpuVendor vendor, long? dedicatedBytes)
    {
        if (vendor is GpuVendor.Intel or GpuVendor.Qualcomm)
            return dedicatedBytes is null || dedicatedBytes < 2 * ByteSize.GiB;
        return dedicatedBytes is not null && dedicatedBytes < 512 * ByteSize.MiB;
    }

    /// <summary>Le registre de Windows 11 indique encore « Windows 10 » dans ProductName.</summary>
    public static string CorrectProductName(string? productName, int build)
    {
        var name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
        if (build >= Windows11FirstBuild && name.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            name = name.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        return name;
    }

    /// <summary>MSFT_PhysicalDisk.MediaType : 3 = HDD, 4 = SSD, 5 = SCM.</summary>
    public static StorageMediaType MediaTypeFromMsft(int? value) => value switch
    {
        3 => StorageMediaType.Hdd,
        4 => StorageMediaType.Ssd,
        5 => StorageMediaType.Scm,
        _ => StorageMediaType.Unknown,
    };

    /// <summary>MSFT_PhysicalDisk.BusType : 17 = NVMe, 11 = SATA, 7 = USB, 10 = SAS, 8 = RAID, 15 = virtuel.</summary>
    public static StorageBusType BusTypeFromMsft(int? value) => value switch
    {
        null or 0 => StorageBusType.Unknown,
        17 => StorageBusType.Nvme,
        11 => StorageBusType.Sata,
        7 => StorageBusType.Usb,
        10 => StorageBusType.Sas,
        8 => StorageBusType.Raid,
        15 => StorageBusType.Virtual,
        _ => StorageBusType.Other,
    };

    public static ProcessorArchitecture ArchitectureFrom(Architecture architecture) => architecture switch
    {
        Architecture.X64 => ProcessorArchitecture.X64,
        Architecture.X86 => ProcessorArchitecture.X86,
        Architecture.Arm64 => ProcessorArchitecture.Arm64,
        _ => ProcessorArchitecture.Unknown,
    };

    /// <summary>Extrait VEN_xxxx et DEV_xxxx d'un identifiant PnP (ex. « PCI\VEN_10DE&amp;DEV_1C82&amp;SUBSYS_… »).</summary>
    public static (uint VendorId, uint DeviceId)? ParsePciIds(string? pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId)) return null;
        var vendor = ReadHexAfter(pnpDeviceId, "VEN_");
        var device = ReadHexAfter(pnpDeviceId, "DEV_");
        return vendor is null || device is null ? null : (vendor.Value, device.Value);
    }

    private static uint? ReadHexAfter(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || index + marker.Length + 4 > text.Length) return null;
        return uint.TryParse(text.AsSpan(index + marker.Length, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}

/// <summary>Entrée brute d'une clé de désinstallation.</summary>
internal sealed record UninstallEntry(string? DisplayName, int? SystemComponent, string? ParentKeyName, string? ReleaseType);

internal static class InstalledProgramFilter
{
    /// <summary>
    /// Programmes visibles : DisplayName non vide, pas de composant système, pas de mise à jour ni de correctif rattaché
    /// à un produit parent ; dédoublonnés par nom (vues 64/32 bits et HKCU).
    /// </summary>
    public static int CountDistinct(IEnumerable<UninstallEntry> entries)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (IsVisibleProgram(entry)) names.Add(entry.DisplayName!.Trim());
        }
        return names.Count;
    }

    public static bool IsVisibleProgram(UninstallEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.DisplayName)) return false;
        if (entry.SystemComponent == 1) return false;
        if (!string.IsNullOrWhiteSpace(entry.ParentKeyName)) return false;
        if (entry.ReleaseType is { } type
            && (type.Equals("Update", StringComparison.OrdinalIgnoreCase)
                || type.Equals("Hotfix", StringComparison.OrdinalIgnoreCase)
                || type.Equals("Security Update", StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }
}
