using PCBoost.Core.Common;
using PCBoost.Core.Drivers;
using PCBoost.Core.Models.Drivers;
using PCBoost.Core.Models.Health;

namespace PCBoost.App.Services;

/// <summary>Données d'exemple des captures de développement (jamais affichées hors de la pseudo-page de capture).</summary>
internal static class DevCaptureSamples
{
    public static (DriverScanResult Scan, DriverInstallResult Result) Drivers(DateTimeOffset now)
    {
        var gpu = new InstalledDriver(@"PCI\VEN_8086&DEV_5917&SUBSYS_00000000&REV_07\3&1&0&10", "Carte graphique d'exemple", "Display",
            [@"PCI\VEN_8086&DEV_5917"], [], "27.20.100.8681", new DateOnly(2021, 3, 12), "Intel Corporation", "oem12.inf", 0);
        var wifi = new InstalledDriver(@"PCI\VEN_10EC&DEV_B520\4&2", "Carte Wi-Fi d'exemple", "Net",
            [@"PCI\VEN_10EC&DEV_B520"], [], "6001.15.1.0", new DateOnly(2023, 2, 1), "Realtek Semiconductor Corp.", "oem6.inf", 0);
        var reader = new InstalledDriver(@"PCI\VEN_10EC&DEV_5260\4&3", "Lecteur de cartes d'exemple", "System",
            [@"PCI\VEN_10EC&DEV_5260"], [], null, null, null, null, 28);
        var nvme = new InstalledDriver(@"PCI\VEN_144D&DEV_A808\4&4", "Contrôleur NVMe d'exemple", "SCSIAdapter",
            [@"PCI\VEN_144D&DEV_A808"], [], "10.0.26100.1", new DateOnly(2006, 6, 21), "Microsoft", "stornvme.inf", 0);
        DriverUpdateOffer Offer(string id, InstalledDriver device, string? cls, string version, DateOnly date, string provider, bool optional = false,
            int publishedDaysAgo = 60, long size = 250_000_000, string? title = null, bool userInput = false)
            => new(id, 1, title ?? $"{provider} - {cls} - {version}", cls, device.HardwareIds[0], provider, device.DeviceName, provider, date, version, optional,
                now.AddDays(-publishedDaysAgo), size, true, userInput, DriverRebootBehavior.Possible);
        var offers = new[]
        {
            Offer("0c3a9a55-5b0f-4f1d-9b3e-2a6b7f0c1d2e", gpu, "Display", "31.0.101.2125", new DateOnly(2023, 5, 15), "Intel Corporation"),
            Offer("6f1e2d3c-4b5a-4968-8776-5a4b3c2d1e0f", wifi, "Net", "6001.16.140.0", new DateOnly(2026, 9, 20), "Realtek Semiconductor Corp.", publishedDaysAgo: 5, size: 18_000_000),
            Offer("7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d", reader, "System", "10.0.22000.20290", new DateOnly(2024, 1, 10), "Realtek Semiconductor Corp.", optional: true, size: 4_000_000),
            Offer("8b7c6d5e-4f3a-4b2c-8d9e-0f1a2b3c4d5e", nvme, "SCSIAdapter", "2.4.0.2401", new DateOnly(2025, 3, 3), "Samsung Electronics Co., Ltd", size: 2_000_000),
            Offer("11111111-2222-4333-8444-555555555555", gpu, "Firmware", "1.24.0", new DateOnly(2026, 1, 5), "Intel Corporation", title: "Intel Corporation - Firmware - 1.24.0", userInput: true),
        };
        var devices = new[] { gpu, wifi, reader, nvme };
        var candidates = offers.Select(o => DriverUpdatePolicy.Evaluate(o, devices, now)).OrderBy(c => c.Tier).ToList();
        var computer = new ComputerIdentity("HP", "PC portable d'exemple", "HP");
        var graphics = new InstalledDriver(@"PCI\VEN_10DE&DEV_28A1\4&5", "NVIDIA GeForce (exemple)", "Display", [@"PCI\VEN_10DE&DEV_28A1"], [],
            "32.0.16.1714", new DateOnly(2026, 9, 17), "NVIDIA", "oem52.inf", 0);
        var scan = new DriverScanResult(DriverScanState.Ready, candidates, now, false, false, false, SystemProtectionState.Enabled, null,
            computer, OfficialDriverSources.For(computer, [gpu, graphics]));
        var result = new DriverInstallResult(OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drv_Install_Partial")), DriverInstallStop.None,
            RestorePointStatus.Created, now.AddMinutes(-4), false,
            [new DriverInstallOutcome(offers[0].UpdateId, DriverInstallStatus.Installed, 0, true, "31.0.101.2125", 0),
             new DriverInstallOutcome(offers[1].UpdateId, DriverInstallStatus.Failed, unchecked((int)0x80240022), false, null, null)],
            true, null);
        return (scan, result);
    }
}
