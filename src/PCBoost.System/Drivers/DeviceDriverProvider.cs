using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Drivers;
using PCBoost.Platform.Health;

namespace PCBoost.Platform.Drivers;

/// <summary>
/// Périphériques présents (Win32_PnPEntity : identifiants matériels et compatibles, code de problème) et pilote installé
/// de chacun (Win32_PnPSignedDriver : version, date, éditeur, fichier INF). Lecture seule, sans autorisation administrateur.
/// </summary>
public sealed class DeviceDriverProvider : IDeviceDriverProvider
{
    private const int MaxDevices = 4096;

    private readonly ILogger<DeviceDriverProvider> _logger;

    public DeviceDriverProvider(ILogger<DeviceDriverProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<DeviceDriverProvider>.Instance;
    }

    /// <summary>
    /// Lecture complète ou échec : si l'une des deux requêtes échoue ou s'interrompt (délai dépassé sur un PC lent), aucune
    /// liste partielle n'est renvoyée — des versions manquantes fausseraient l'état « avant » et les revérifications.
    /// </summary>
    public OperationResult<IReadOnlyList<InstalledDriver>> GetInstalledDrivers()
    {
        var entitiesRead = Wmi.TryQuery(Wmi.CimV2,
            "SELECT DeviceID, Name, PNPClass, HardwareID, CompatibleID, ConfigManagerErrorCode, Present FROM Win32_PnPEntity",
            o => Wmi.String(o, "DeviceID") is { } id && Wmi.Bool(o, "Present") != false
                ? new EntityRow(id, Wmi.String(o, "Name"), Wmi.String(o, "PNPClass"), Wmi.Strings(o, "HardwareID"), Wmi.Strings(o, "CompatibleID"),
                    Wmi.Int32(o, "ConfigManagerErrorCode") ?? 0)
                : null,
            _logger, out var entities);
        if (!entitiesRead || entities.Count == 0) return OperationResult<IReadOnlyList<InstalledDriver>>.Fail(OperationErrorKind.NotSupported);

        var driversRead = Wmi.TryQuery(Wmi.CimV2,
            "SELECT DeviceID, DeviceName, DeviceClass, DriverVersion, DriverDate, DriverProviderName, InfName FROM Win32_PnPSignedDriver",
            o => Wmi.String(o, "DeviceID") is { } id
                ? new DriverRow(id, Wmi.String(o, "DeviceName"), Wmi.String(o, "DeviceClass"), Wmi.String(o, "DriverVersion"),
                    DriverWmiParsing.ParseCimDate(Wmi.String(o, "DriverDate")), Wmi.String(o, "DriverProviderName"), Wmi.String(o, "InfName"))
                : null,
            _logger, out var drivers);
        // Tout PC a des pilotes signés : une liste vide signale une lecture qui n'a pas abouti.
        if (!driversRead || drivers.Count == 0) return OperationResult<IReadOnlyList<InstalledDriver>>.Fail(OperationErrorKind.Failed);

        return OperationResult<IReadOnlyList<InstalledDriver>>.Ok(DriverWmiParsing.Merge(entities, drivers, MaxDevices));
    }

    public Core.Drivers.ComputerIdentity? GetComputerIdentity()
    {
        var system = Wmi.Query(Wmi.CimV2, "SELECT Manufacturer, Model FROM Win32_ComputerSystem",
            o => new[] { HealthEventParsers.Clean(Wmi.String(o, "Manufacturer")), HealthEventParsers.Clean(Wmi.String(o, "Model")) }, _logger).FirstOrDefault();
        // Lenovo place le nom commercial (« ThinkPad T480 ») dans Win32_ComputerSystemProduct.Version.
        var product = Wmi.Query(Wmi.CimV2, "SELECT Version FROM Win32_ComputerSystemProduct",
            o => HealthEventParsers.Clean(Wmi.String(o, "Version")) ?? string.Empty, _logger).FirstOrDefault();
        var board = Wmi.Query(Wmi.CimV2, "SELECT Manufacturer FROM Win32_BaseBoard",
            o => HealthEventParsers.Clean(Wmi.String(o, "Manufacturer")) ?? string.Empty, _logger).FirstOrDefault();
        if (system is null && string.IsNullOrEmpty(board)) return null;
        return DriverWmiParsing.Identity(system?[0], system?[1], product, board);
    }

    internal sealed record EntityRow(string InstanceId, string? Name, string? PnpClass, IReadOnlyList<string> HardwareIds, IReadOnlyList<string> CompatibleIds, int ProblemCode);

    internal sealed record DriverRow(string InstanceId, string? DeviceName, string? DeviceClass, string? Version, DateOnly? Date, string? Provider, string? InfName);
}

/// <summary>Lectures pures des valeurs WMI des pilotes (testables hors Windows).</summary>
internal static class DriverWmiParsing
{
    /// <summary>Date CIM « 20230315000000.******+000 » → 2023-03-15 ; null si illisible.</summary>
    public static DateOnly? ParseCimDate(string? value)
    {
        if (value is null || value.Length < 8) return null;
        return DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year >= 1980
            ? date
            : null;
    }

    /// <summary>
    /// Identité du PC : modèle commercial Lenovo (Win32_ComputerSystemProduct.Version) à la place du code de type ;
    /// valeurs génériques des PC assemblés (« System manufacturer », « To be filled by O.E.M. ») ignorées.
    /// </summary>
    public static Core.Drivers.ComputerIdentity Identity(string? manufacturer, string? model, string? productVersion, string? boardManufacturer)
    {
        static string? Real(string? value)
            => string.IsNullOrWhiteSpace(value) || value.Contains("manufacturer", StringComparison.OrdinalIgnoreCase)
               || value.Contains("O.E.M", StringComparison.OrdinalIgnoreCase) || value.Contains("Default string", StringComparison.OrdinalIgnoreCase)
               || value.Contains("System Product Name", StringComparison.OrdinalIgnoreCase)
                ? null
                : value.Trim();

        var maker = Real(manufacturer);
        var name = Real(model);
        if (maker is not null && maker.StartsWith("Lenovo", StringComparison.OrdinalIgnoreCase) && Real(productVersion) is { } friendly && friendly.Length > 2)
            name = friendly;
        return new Core.Drivers.ComputerIdentity(maker, name, Real(boardManufacturer));
    }

    /// <summary>Associe à chaque périphérique présent son pilote (même identifiant d'instance, sans tenir compte de la casse).</summary>
    public static IReadOnlyList<InstalledDriver> Merge(IReadOnlyList<DeviceDriverProvider.EntityRow> entities,
        IReadOnlyList<DeviceDriverProvider.DriverRow> drivers, int max)
    {
        var byId = new Dictionary<string, DeviceDriverProvider.DriverRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var driver in drivers) byId.TryAdd(driver.InstanceId, driver);

        var result = new List<InstalledDriver>();
        foreach (var entity in entities)
        {
            if (result.Count >= max) break;
            byId.TryGetValue(entity.InstanceId, out var driver);
            var name = HealthEventParsers.Clean(entity.Name) ?? HealthEventParsers.Clean(driver?.DeviceName) ?? entity.InstanceId;
            var version = driver?.Version is { } v && Core.Drivers.DriverIdentifiers.IsValidVersion(v) ? v : null;
            var inf = driver?.InfName is { } i && Core.Drivers.DriverIdentifiers.IsValidInfName(i) ? i : null;
            result.Add(new InstalledDriver(
                entity.InstanceId,
                name,
                HealthEventParsers.Clean(entity.PnpClass) ?? HealthEventParsers.Clean(driver?.DeviceClass),
                entity.HardwareIds,
                entity.CompatibleIds,
                version,
                driver?.Date,
                HealthEventParsers.Clean(driver?.Provider),
                inf,
                entity.ProblemCode));
        }
        return result;
    }
}
