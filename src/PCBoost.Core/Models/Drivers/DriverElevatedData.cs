using System.Globalization;
using PCBoost.Core.Drivers;
using PCBoost.Core.Models.Health;

namespace PCBoost.Core.Models.Drivers;

/// <summary>
/// Paramètres et résultats échangés avec l'assistant administrateur pour les pilotes (dictionnaires texte → texte de
/// <c>ElevatedRequest.Parameters</c> et <c>ElevatedResponse.Data</c>). Toute valeur invalide est ignorée à la lecture.
/// </summary>
public static class DriverElevatedData
{
    /// <summary>Nombre maximal de mises à jour installées en une fois.</summary>
    public const int MaxUpdates = 16;

    public const string UpdatesKey = "updates";
    public const string EnableProtectionKey = "enableProtection";
    public const string ProgressPipeKey = "progressPipe";
    public const string DevicesKey = "devices";
    public const string PreviousVersionsKey = "previous";
    public const string InstalledVersionKey = "installed";

    /// <summary>Séparateur des identifiants d'instance et des versions (absent de tout identifiant valide).</summary>
    public const char DeviceSeparator = '|';

    // ---- Demande d'installation ----

    public static Dictionary<string, string> InstallParameters(IEnumerable<string> updateIds, bool enableProtection)
    {
        ArgumentNullException.ThrowIfNull(updateIds);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [UpdatesKey] = string.Join(',', updateIds),
            [EnableProtectionKey] = enableProtection ? "true" : "false",
        };
    }

    /// <summary>Identifiants distincts, au format GUID « D », 1 à <see cref="MaxUpdates"/> ; null si la liste est invalide.</summary>
    public static IReadOnlyList<string>? ParseUpdateIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxUpdates * 37) return null;
        var ids = text.Split(',');
        if (ids.Length is 0 or > MaxUpdates || ids.Any(id => !DriverIdentifiers.IsValidUpdateId(id))) return null;
        var distinct = ids.Select(id => id.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == ids.Length ? distinct : null;
    }

    // ---- Demande de retour au pilote précédent ----

    /// <summary>
    /// Périphériques à ramener (identifiant et version précédente, alignés) et version installée par la mise à jour ; null
    /// si le retour ciblé n'est pas possible : version installée inconnue, ou un périphérique noté sans version précédente
    /// valide (il resterait sur le nouveau pilote).
    /// </summary>
    public static Dictionary<string, string>? RollbackParameters(DriverUpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var targets = state.RollbackTargets();
        if (targets.Count == 0 || targets.Count != (state.Devices?.Count ?? 0) || !DriverIdentifiers.IsValidVersion(state.NewVersion)) return null;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DevicesKey] = string.Join(DeviceSeparator, targets.Select(t => t.InstanceId)),
            [PreviousVersionsKey] = string.Join(DeviceSeparator, targets.Select(t => t.PreviousVersion)),
            [InstalledVersionKey] = state.NewVersion!,
        };
    }

    /// <summary>Identifiants d'instance distincts et valides, 1 à <see cref="DriverUpdatePolicy.MaxDevicesPerUpdate"/>.</summary>
    public static IReadOnlyList<string>? ParseDevices(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > DriverUpdatePolicy.MaxDevicesPerUpdate * (DriverIdentifiers.MaxDeviceIdLength + 1)) return null;
        var ids = text.Split(DeviceSeparator);
        if (ids.Length is 0 || ids.Length > DriverUpdatePolicy.MaxDevicesPerUpdate || ids.Any(id => !DriverIdentifiers.IsValidInstanceId(id))) return null;
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Length ? ids : null;
    }

    /// <summary>Périphériques et versions précédentes alignés (même nombre, versions valides) ; null sinon.</summary>
    public static IReadOnlyList<DriverRollbackTarget>? ParseRollbackTargets(string? devices, string? previousVersions)
    {
        if (ParseDevices(devices) is not { } ids || string.IsNullOrEmpty(previousVersions) || previousVersions.Length > DriverUpdatePolicy.MaxDevicesPerUpdate * 24)
            return null;
        var versions = previousVersions.Split(DeviceSeparator);
        if (versions.Length != ids.Count || versions.Any(v => !DriverIdentifiers.IsValidVersion(v))) return null;
        return ids.Select((id, i) => new DriverRollbackTarget(id, versions[i])).ToList();
    }

    // ---- Résultat de l'installation ----

    public static Dictionary<string, string> EncodeInstall(DriverInstallStop stop, RestorePointStatus restorePoint, DateTimeOffset? restorePointAt,
        bool protectionEnabled, IReadOnlyList<DriverInstallOutcome> drivers, bool rebootRequired)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stop"] = stop.ToString(),
            ["rp.status"] = restorePoint.ToString(),
            ["rp.enabled"] = Bool(protectionEnabled),
            ["reboot"] = Bool(rebootRequired),
        };
        if (restorePointAt is { } at) data["rp.time"] = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var list = drivers.Take(MaxUpdates).ToList();
        data["count"] = list.Count.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < list.Count; i++)
        {
            var d = list[i];
            data[$"{i}.id"] = d.UpdateId;
            data[$"{i}.status"] = d.Status.ToString();
            data[$"{i}.hr"] = d.HResult.ToString(CultureInfo.InvariantCulture);
            data[$"{i}.reboot"] = Bool(d.RebootRequired);
            if (DriverIdentifiers.IsValidVersion(d.NewVersion)) data[$"{i}.version"] = d.NewVersion!;
            if (d.ProblemCodeAfter is { } problem) data[$"{i}.problem"] = problem.ToString(CultureInfo.InvariantCulture);
        }
        return data;
    }

    public static (DriverInstallStop Stop, RestorePointStatus RestorePoint, DateTimeOffset? RestorePointAt, bool ProtectionEnabled,
        IReadOnlyList<DriverInstallOutcome> Drivers, bool RebootRequired) DecodeInstall(IReadOnlyDictionary<string, string> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var stop = ParseEnum(data, "stop", DriverInstallStop.None);
        var restorePoint = ParseEnum(data, "rp.status", RestorePointStatus.Failed);
        DateTimeOffset? at = data.TryGetValue("rp.time", out var t) && long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                             && seconds is > 0 and < 32503680000
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
        var drivers = new List<DriverInstallOutcome>();
        var count = data.TryGetValue("count", out var c) && int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Min(n, MaxUpdates) : 0;
        for (var i = 0; i < count; i++)
        {
            if (!data.TryGetValue($"{i}.id", out var id) || !DriverIdentifiers.IsValidUpdateId(id)) continue;
            if (!TryParseName<DriverInstallStatus>(data, $"{i}.status", out var status)) continue;
            var hr = data.TryGetValue($"{i}.hr", out var h) && int.TryParse(h, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            var version = data.TryGetValue($"{i}.version", out var v) && DriverIdentifiers.IsValidVersion(v) ? v : null;
            int? problem = data.TryGetValue($"{i}.problem", out var p) && int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code < 1000 ? code : null;
            drivers.Add(new DriverInstallOutcome(id, status, hr, GetBool(data, $"{i}.reboot"), version, problem));
        }
        return (stop, restorePoint, at, GetBool(data, "rp.enabled"), drivers, GetBool(data, "reboot"));
    }

    // ---- Résultat du retour au pilote précédent ----

    public static Dictionary<string, string> EncodeRollback(IReadOnlyList<DriverRollbackOutcome> outcomes, bool rebootRequired)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var list = outcomes.Take(DriverUpdatePolicy.MaxDevicesPerUpdate).ToList();
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["count"] = list.Count.ToString(CultureInfo.InvariantCulture),
            ["reboot"] = Bool(rebootRequired),
        };
        for (var i = 0; i < list.Count; i++)
        {
            data[$"{i}.device"] = list[i].InstanceId;
            data[$"{i}.status"] = list[i].Status.ToString();
            data[$"{i}.error"] = list[i].Win32Error.ToString(CultureInfo.InvariantCulture);
            if (DriverIdentifiers.IsValidVersion(list[i].VersionAfter)) data[$"{i}.version"] = list[i].VersionAfter!;
        }
        return data;
    }

    public static (IReadOnlyList<DriverRollbackOutcome> Outcomes, bool RebootRequired) DecodeRollback(IReadOnlyDictionary<string, string> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var outcomes = new List<DriverRollbackOutcome>();
        var count = data.TryGetValue("count", out var c) && int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? Math.Min(n, DriverUpdatePolicy.MaxDevicesPerUpdate)
            : 0;
        for (var i = 0; i < count; i++)
        {
            if (!data.TryGetValue($"{i}.device", out var device) || !DriverIdentifiers.IsValidInstanceId(device)) continue;
            if (!TryParseName<DriverRollbackStatus>(data, $"{i}.status", out var status)) continue;
            var error = data.TryGetValue($"{i}.error", out var e) && int.TryParse(e, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            var version = data.TryGetValue($"{i}.version", out var v) && DriverIdentifiers.IsValidVersion(v) ? v : null;
            outcomes.Add(new DriverRollbackOutcome(device, status, version, error));
        }
        return (outcomes, GetBool(data, "reboot"));
    }

    // ---- Point de restauration à la demande ----

    public static Dictionary<string, string> RestorePointParameters(bool enableProtection)
        => new(StringComparer.Ordinal) { [EnableProtectionKey] = enableProtection ? "true" : "false" };

    public static Dictionary<string, string> EncodeRestorePoint(RestorePointStatus status, DateTimeOffset? createdAt, bool protectionEnabled)
        => EncodeInstall(DriverInstallStop.None, status, createdAt, protectionEnabled, [], false);

    public static (RestorePointStatus Status, DateTimeOffset? CreatedAt, bool ProtectionEnabled) DecodeRestorePoint(IReadOnlyDictionary<string, string> data)
    {
        var (_, status, createdAt, enabled, _, _) = DecodeInstall(data);
        return (status, createdAt, enabled);
    }

    // ---- Progression (une ligne par étape, transmise par canal nommé) ----

    /// <summary>« Installing;2;3 ».</summary>
    public static string FormatProgress(DriverInstallProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return string.Create(CultureInfo.InvariantCulture, $"{progress.Stage};{progress.Index};{progress.Total}");
    }

    public static DriverInstallProgress? ParseProgress(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 64) return null;
        var parts = line.Trim().Split(';');
        if (parts.Length != 3 || !TryParseName<DriverInstallStage>(parts[0], out var stage)) return null;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            || total > MaxUpdates || index > total)
        {
            return null;
        }
        return new DriverInstallProgress(stage, index, total);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static bool GetBool(IReadOnlyDictionary<string, string> data, string key)
        => data.TryGetValue(key, out var value) && string.Equals(value, "true", StringComparison.Ordinal);

    private static T ParseEnum<T>(IReadOnlyDictionary<string, string> data, string key, T fallback) where T : struct, Enum
        => TryParseName<T>(data, key, out var value) ? value : fallback;

    private static bool TryParseName<T>(IReadOnlyDictionary<string, string> data, string key, out T value) where T : struct, Enum
    {
        value = default;
        return data.TryGetValue(key, out var text) && TryParseName(text, out value);
    }

    /// <summary>Nom exact d'une valeur de l'énumération (les nombres sont refusés).</summary>
    private static bool TryParseName<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        return !string.IsNullOrEmpty(text) && char.IsAsciiLetter(text[0]) && Enum.TryParse(text, ignoreCase: false, out value) && Enum.IsDefined(value);
    }
}
