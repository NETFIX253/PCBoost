using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Drivers;

namespace PCBoost.TestUtilities;

/// <summary>Recherche Windows Update simulée.</summary>
public sealed class FakeDriverUpdateSource : IDriverUpdateSource
{
    public DriverSearchResult Result { get; set; } = new(OperationResult.Ok(), [], false, false);

    public int SearchCount { get; private set; }

    /// <summary>Si défini, la recherche attend ce signal (tests d'annulation).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<DriverSearchResult> SearchAsync(CancellationToken cancellationToken = default)
    {
        SearchCount++;
        if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Result;
    }
}

/// <summary>Périphériques et pilotes installés simulés.</summary>
public sealed class FakeDeviceDriverProvider : IDeviceDriverProvider
{
    public List<InstalledDriver> Drivers { get; } = [];

    public bool Fails { get; set; }

    public int ReadCount { get; private set; }

    public Core.Drivers.ComputerIdentity? Computer { get; set; }

    public Core.Drivers.ComputerIdentity? GetComputerIdentity() => Computer;

    public OperationResult<IReadOnlyList<InstalledDriver>> GetInstalledDrivers()
    {
        ReadCount++;
        return Fails
            ? OperationResult<IReadOnlyList<InstalledDriver>>.Fail(OperationErrorKind.NotSupported)
            : OperationResult<IReadOnlyList<InstalledDriver>>.Ok(Drivers.ToList());
    }
}

public sealed class FakeNetworkCostProvider : INetworkCostProvider
{
    public bool? Metered { get; set; }

    public bool? IsMeteredConnection() => Metered;
}

/// <summary>Fabriques de données de pilotes pour les tests.</summary>
public static class DriverSamples
{
    public static InstalledDriver Device(string instanceId = @"PCI\VEN_8086&DEV_5917&SUBSYS_00000000&REV_07\3&11583659&0&10",
        string hardwareId = @"PCI\VEN_8086&DEV_5917", string name = "Intel(R) UHD Graphics 620", string? deviceClass = "Display",
        string? version = "27.20.100.8681", DateOnly? date = null, string? provider = "Intel Corporation", string? inf = "oem12.inf", int problem = 0)
        => new(instanceId, name, deviceClass, [hardwareId + "&SUBSYS_00000000&REV_07", hardwareId], [@"PCI\CC_030000"], version,
            date ?? new DateOnly(2021, 3, 12), provider, inf, problem);

    public static DriverUpdateOffer Offer(string updateId = "0c3a9a55-5b0f-4f1d-9b3e-2a6b7f0c1d2e", string hardwareId = @"PCI\VEN_8086&DEV_5917",
        string? driverClass = "Display", string? version = "31.0.101.2125", DateOnly? date = null, string? provider = "Intel Corporation",
        bool optional = false, DateTimeOffset? published = null, long? size = 300_000_000, bool eula = true, bool userInput = false,
        string? title = null, DriverRebootBehavior reboot = DriverRebootBehavior.Never)
        => new(updateId, 1, title ?? $"Intel Corporation - {driverClass} - {version}", driverClass, hardwareId, "Intel Corporation", "Intel(R) UHD Graphics 620",
            provider, date ?? new DateOnly(2023, 5, 15), version, optional, published ?? new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            size, eula, userInput, reboot);
}
